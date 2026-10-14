using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Engine;

/// <summary>A slot touched by a group assignment, with the manual assignment it had before (if any).</summary>
public sealed record PendingSlot(DateTime SlotStartUtc, bool HadOverride, int? PreviousTaskId, string? PreviousNotes);

/// <summary>A task assigned to an unclassified group but not yet confirmed: the slots are assigned, no rule exists.</summary>
/// <param name="StoredRuleId">Set once <see cref="GroupAssignmentService.ConfirmAsync"/> stored the rule but did not finish (failure afterwards): a retry reuses that rule instead of deriving it again.</param>
public sealed record PendingGroupAssignment(
    Guid Id,
    DateOnly WeekStart,
    string GroupKey,
    GroupKind Kind,
    int TaskId,
    ActivityGroup Metadata,
    IReadOnlyList<PendingSlot> Slots,
    int? StoredRuleId = null)
{
    public decimal Hours => (decimal)(Slots.Count * EngineOptions.SlotLength.TotalHours);
}

/// <param name="Rule">The stored rule, or null when no stable rule could be derived (the assignment stays pending).</param>
/// <param name="SlotsKeptManual">Slots the new rule does not cover: they keep their manual assignment instead of falling back to unclassified.</param>
/// <param name="WeekSubmitted">The week was submitted meanwhile: nothing was changed and the pending state was dropped.</param>
public sealed record GroupRuleConfirmation(Rule? Rule, int SlotsKeptManual, bool WeekSubmitted = false);

/// <param name="SlotsRestored">Slots back to their previous state.</param>
/// <param name="SlotsSkipped">Slots left alone because the user changed or deleted them since (their newer choice wins).</param>
/// <param name="WeekSubmitted">The week was submitted meanwhile: nothing was changed and the pending state was dropped.</param>
public sealed record GroupAssignmentCancellation(int SlotsRestored, int SlotsSkipped, bool WeekSubmitted = false);

/// <param name="Kept">The pending state was settled by this call (false when already settled, or when the week was submitted).</param>
/// <param name="WeekSubmitted">The week was submitted meanwhile: the pending state was dropped.</param>
public sealed record GroupKeepResult(bool Kept, bool WeekSubmitted = false);

/// <summary>
/// "Assign a task to an unclassified group" in three steps: <see cref="AssignAsync"/> applies the task to the slots of the group
/// right away (preview in the totals) and keeps the group pending; then <see cref="ConfirmAsync"/> (store a rule),
/// <see cref="KeepOnceAsync"/> (keep the assignment, no rule) or <see cref="CancelAsync"/> (undo) settles it.
/// Pending state lives in memory for the life of the process: if it is lost (restart) the assignment simply stays as "this time only".
/// Nothing is lost silently and no rule is ever created without <see cref="ConfirmAsync"/>.
/// A group that grows while pending (new unclassified slots with the same key) is extended by calling <see cref="AssignAsync"/> again;
/// until then the new slots stay unclassified and the pending assignment does not cover them.
/// Once the week is submitted, every operation refuses (no override is touched, the draft is frozen) and drops the pending state.
/// </summary>
public sealed class GroupAssignmentService(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    WeekProposalBuilder builder,
    WeekService weeks,
    SlotAllocator allocator)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<PendingGroupAssignment> _pending = [];

    /// <summary>Pending assignments of a week, oldest first.</summary>
    public IReadOnlyList<PendingGroupAssignment> GetPending(DateOnly weekStart)
    {
        weekStart = WeekProposalBuilder.WeekStartOf(weekStart);
        lock (_pending)
            return _pending.Where(p => p.WeekStart == weekStart).ToList();
    }

    /// <summary>
    /// Pending assignments of a week like <see cref="GetPending"/>, but first drops them when the week has been submitted since.
    /// </summary>
    public async Task<IReadOnlyList<PendingGroupAssignment>> GetPendingAsync(DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        weekStart = WeekProposalBuilder.WeekStartOf(weekStart);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (GetPending(weekStart).Count > 0 && await IsSubmittedAsync(weekStart, cancellationToken))
                PurgeWeek(weekStart);
            return GetPending(weekStart);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Assigns <paramref name="taskId"/> to every work slot of the group in the week (slots deleted, "not work" or ignored are left alone).
    /// A group already pending is switched to the new task if needed and extended to the slots that appeared since
    /// (same <see cref="PendingGroupAssignment.Id"/>, the new slots are added to the snapshot so Annuler covers them); with nothing new it is returned as is.
    /// Null when there is nothing to assign, or when the week is submitted (any pending state of that week is then dropped).
    /// </summary>
    public async Task<PendingGroupAssignment?> AssignAsync(DateOnly weekStart, string groupKey, int taskId, CancellationToken cancellationToken = default)
    {
        weekStart = WeekProposalBuilder.WeekStartOf(weekStart);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var week = await builder.BuildAsync(weekStart, classifyMissing: false, cancellationToken);
            if (week.IsSubmitted)
            {
                PurgeWeek(weekStart);
                return null;
            }

            var existing = Find(weekStart, groupKey);
            if (existing is not null)
            {
                var current = existing.TaskId == taskId ? existing : await SwitchTaskAsync(existing, taskId, cancellationToken);
                var fresh = GroupSlotStarts(week, groupKey);
                return fresh.Count == 0 ? current : await ExtendAsync(current, fresh, cancellationToken);
            }

            var group = week.UnclassifiedGroups.FirstOrDefault(g => g.Key == groupKey);
            if (group is null || !week.Tasks.ContainsKey(taskId))
                return null;

            var starts = GroupSlotStarts(week, groupKey);
            if (starts.Count == 0)
                return null;

            var snapshot = await ApplyAsync(starts, taskId, alreadySnapshotted: null, cancellationToken);
            var pending = new PendingGroupAssignment(Guid.NewGuid(), weekStart, groupKey, group.Kind, taskId, group.Metadata, snapshot);
            lock (_pending)
                _pending.Add(pending);
            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// "Confirmer": stores the rule learned from the group (the existing correction flow). The manual assignment made by
    /// <see cref="AssignAsync"/> is then dropped since the rule decides; slots the rule does not cover keep it.
    /// When no stable rule can be derived nothing changes and the assignment stays pending. Null when the id is unknown (already settled).
    /// </summary>
    public async Task<GroupRuleConfirmation?> ConfirmAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Find(id) is not { } pending)
                return null;
            if (await IsSubmittedAsync(pending.WeekStart, cancellationToken))
            {
                PurgeWeek(pending.WeekStart);
                return new GroupRuleConfirmation(null, 0, WeekSubmitted: true);
            }

            // Retry-safe: the pending state is dropped last and remembers the stored rule (a retry cannot derive it again: the rule now
            // takes the group's activities out of the unclassified ones), RestoreAsync skips what is already restored
            // and KeepUncoveredAsync recomputes from the current state; a failure between two steps is fixed by confirming again.
            Rule? rule = null;
            if (pending.StoredRuleId is { } storedId)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                rule = await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == storedId, cancellationToken);
            }

            if (rule is null)
            {
                rule = await weeks.CreateRuleFromCorrectionAsync(new CorrectionTarget(WeekRange(pending.WeekStart), pending.GroupKey), pending.TaskId, cancellationToken);
                if (rule is null)
                    return new GroupRuleConfirmation(null, 0);

                pending = Replace(pending, pending with { StoredRuleId = rule.Id });
            }

            await RestoreAsync(pending, cancellationToken);
            var kept = await KeepUncoveredAsync(pending, cancellationToken);
            Remove(pending);
            return new GroupRuleConfirmation(rule, kept);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Cette fois seulement": keeps the assignment as is, without a rule. False when the id is unknown (already settled) or the week is submitted.</summary>
    public async Task<bool> KeepOnceAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await TryKeepOnceAsync(id, cancellationToken)).Kept;

    /// <summary>Same as <see cref="KeepOnceAsync"/>, telling a submitted week apart from an already settled assignment.</summary>
    public async Task<GroupKeepResult> TryKeepOnceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Find(id) is not { } pending)
                return new GroupKeepResult(false);
            if (await IsSubmittedAsync(pending.WeekStart, cancellationToken))
            {
                PurgeWeek(pending.WeekStart);
                return new GroupKeepResult(false, WeekSubmitted: true);
            }

            return new GroupKeepResult(Remove(pending));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Annuler": puts the slots back as they were before the assignment. Null when the id is unknown (already settled).</summary>
    public async Task<GroupAssignmentCancellation?> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Find(id) is not { } pending)
                return null;
            if (await IsSubmittedAsync(pending.WeekStart, cancellationToken))
            {
                PurgeWeek(pending.WeekStart);
                return new GroupAssignmentCancellation(0, 0, WeekSubmitted: true);
            }

            var result = await RestoreAsync(pending, cancellationToken);
            Remove(pending);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PendingGroupAssignment> SwitchTaskAsync(PendingGroupAssignment pending, int taskId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var starts = pending.Slots.Select(s => s.SlotStartUtc).Order().ToList();
        var overrides = await LoadOverridesAsync(db, starts[0], starts[^1], cancellationToken);
        foreach (var start in starts)
        {
            if (overrides.TryGetValue(start, out var slot) && IsOurs(slot, pending))
                slot.ForecastTaskId = taskId;
        }

        await db.SaveChangesAsync(cancellationToken);

        var switched = pending with { TaskId = taskId, StoredRuleId = null };
        Replace(pending, switched);
        return switched;
    }

    private PendingGroupAssignment Replace(PendingGroupAssignment old, PendingGroupAssignment updated)
    {
        lock (_pending)
            _pending[_pending.IndexOf(old)] = updated;
        return updated;
    }

    /// <summary>Applies the task to <paramref name="fresh"/> slots of an already pending group and records them in its snapshot.</summary>
    private async Task<PendingGroupAssignment> ExtendAsync(PendingGroupAssignment pending, IReadOnlyList<DateTime> fresh, CancellationToken cancellationToken)
    {
        var known = pending.Slots.Select(s => s.SlotStartUtc).ToHashSet();
        var added = await ApplyAsync(fresh, pending.TaskId, known, cancellationToken);
        var extended = pending with { Slots = [.. pending.Slots, .. added] };
        return Replace(pending, extended);
    }

    /// <summary>
    /// Writes the task on the slots and returns the snapshot entries to remember. Slots already in the snapshot keep their original
    /// entry (what they were before the first assignment), so Annuler restores the true earlier state.
    /// </summary>
    private async Task<List<PendingSlot>> ApplyAsync(IReadOnlyList<DateTime> starts, int taskId, ISet<DateTime>? alreadySnapshotted, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var overrides = await LoadOverridesAsync(db, starts[0], starts[^1], cancellationToken);
        var snapshot = new List<PendingSlot>(starts.Count);
        foreach (var start in starts)
        {
            var remember = alreadySnapshotted?.Contains(start) != true;
            if (overrides.TryGetValue(start, out var slot))
            {
                if (remember)
                    snapshot.Add(new PendingSlot(start, true, slot.ForecastTaskId, slot.Notes));
                slot.ForecastTaskId = taskId;
                slot.Notes = null;
            }
            else
            {
                if (remember)
                    snapshot.Add(new PendingSlot(start, false, null, null));
                db.SlotOverrides.Add(new SlotOverride { SlotStartUtc = start, ForecastTaskId = taskId });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private async Task<bool> IsSubmittedAsync(DateOnly weekStart, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TimesheetDrafts.AnyAsync(d => d.WeekStart == weekStart && d.SubmittedUtc != null, cancellationToken);
    }

    private void PurgeWeek(DateOnly weekStart)
    {
        lock (_pending)
            _pending.RemoveAll(p => p.WeekStart == weekStart);
    }

    /// <summary>Undoes what <see cref="AssignAsync"/> wrote, except where the user has changed the slot since.</summary>
    private async Task<GroupAssignmentCancellation> RestoreAsync(PendingGroupAssignment pending, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var starts = pending.Slots.Select(s => s.SlotStartUtc).Order().ToList();
        var overrides = await LoadOverridesAsync(db, starts[0], starts[^1], cancellationToken);

        int restored = 0, skipped = 0;
        foreach (var slot in pending.Slots)
        {
            if (!overrides.TryGetValue(slot.SlotStartUtc, out var current) || !IsOurs(current, pending))
            {
                skipped++;
                continue;
            }

            if (slot.HadOverride)
            {
                current.ForecastTaskId = slot.PreviousTaskId;
                current.Notes = slot.PreviousNotes;
            }
            else
            {
                db.SlotOverrides.Remove(current);
            }
            restored++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new GroupAssignmentCancellation(restored, skipped);
    }

    /// <summary>After a rule replaced the manual assignment, re-applies it on slots the rule does not classify (so they do not fall back silently).</summary>
    private async Task<int> KeepUncoveredAsync(PendingGroupAssignment pending, CancellationToken cancellationToken)
    {
        var week = await builder.BuildAsync(pending.WeekStart, classifyMissing: false, cancellationToken);
        var wanted = pending.Slots.Select(s => s.SlotStartUtc).ToHashSet();
        var uncovered = GroupSlotStarts(week, pending.GroupKey).Where(wanted.Contains).ToList();
        if (uncovered.Count == 0)
            return 0;

        await weeks.AssignSlotStartsAsync(uncovered, pending.TaskId, cancellationToken: cancellationToken);
        return uncovered.Count;
    }

    private static List<DateTime> GroupSlotStarts(WeekProposal week, string groupKey) =>
        week.Slots.Values
            .SelectMany(slots => slots)
            .Where(s => s.IsWork && s.TaskId is null && s.GroupKey == groupKey)
            .Select(s => s.SlotStartUtc)
            .Order()
            .ToList();

    private static bool IsOurs(SlotOverride slot, PendingGroupAssignment pending) =>
        slot.ForecastTaskId == pending.TaskId && slot.Notes is null;

    private static Task<Dictionary<DateTime, SlotOverride>> LoadOverridesAsync(TimeSheetDbContext db, DateTime first, DateTime last, CancellationToken cancellationToken) =>
        db.SlotOverrides
            .Where(o => o.SlotStartUtc >= first && o.SlotStartUtc <= last)
            .ToDictionaryAsync(o => o.SlotStartUtc, cancellationToken);

    private SlotRange WeekRange(DateOnly weekStart) =>
        new(allocator.DayBounds(weekStart).StartUtc, allocator.DayBounds(weekStart.AddDays(6)).EndUtc);

    private PendingGroupAssignment? Find(DateOnly weekStart, string groupKey)
    {
        lock (_pending)
            return _pending.FirstOrDefault(p => p.WeekStart == weekStart && p.GroupKey == groupKey);
    }

    private PendingGroupAssignment? Find(Guid id)
    {
        lock (_pending)
            return _pending.FirstOrDefault(p => p.Id == id);
    }

    private bool Remove(PendingGroupAssignment pending)
    {
        lock (_pending)
            return _pending.Remove(pending);
    }
}
