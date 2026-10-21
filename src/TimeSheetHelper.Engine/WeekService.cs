using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Learning;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Engine.Rules;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Engine;

/// <summary>UTC range of slots; both ends are snapped to slot boundaries (start down, end up).</summary>
public sealed record SlotRange(DateTime StartUtc, DateTime EndUtc)
{
    public IEnumerable<DateTime> SlotStarts()
    {
        var ticks = EngineOptions.SlotLength.Ticks;
        var start = new DateTime(StartUtc.Ticks / ticks * ticks, DateTimeKind.Utc);
        for (var s = start; s < EndUtc; s += EngineOptions.SlotLength)
            yield return s;
    }
}

/// <summary>What the user corrected: an unclassified group within a range, or every activity of a range.</summary>
public sealed record CorrectionTarget(SlotRange Range, string? GroupKey = null);

/// <summary>What <see cref="WeekService.RemoveSlotsAsync"/> did.</summary>
/// <param name="SlotsRemoved">Slots now deleted (already deleted ones included).</param>
/// <param name="ActivitiesDeleted">Activities erased because every slot they touch is deleted.</param>
/// <param name="ActivitiesKept">Activities that also touch a slot still alive: kept, but ignored inside the deleted slots.</param>
public sealed record SlotRemovalResult(int SlotsRemoved, int ActivitiesDeleted, int ActivitiesKept);

/// <summary>How much a task has been used: last day used and number of distinct days it was used on.</summary>
public sealed record TaskUsage(int TaskId, DateOnly LastUsed, int Days);

/// <summary>Façade used by the UI: week and day views, manual assignments, suggestions and learned rules.</summary>
public sealed class WeekService(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ActivityStore activityStore,
    ActivityResolver resolver,
    WeekProposalBuilder builder,
    SlotAllocator allocator,
    ClassificationService classification,
    CorrectionLearner learner,
    RuleEvaluator ruleEvaluator,
    PredictionFeedback feedback)
{
    /// <summary>Week proposal (Monday of <paramref name="monday"/>'s week). Classifies new groups when Claude is enabled.</summary>
    public Task<WeekProposal> GetWeekAsync(DateOnly monday, CancellationToken cancellationToken = default) =>
        builder.BuildAsync(monday, classifyMissing: true, cancellationToken);

    public Task<DayTimeline> GetDayTimelineAsync(DateOnly day, CancellationToken cancellationToken = default) =>
        builder.BuildDayAsync(day, cancellationToken);

    /// <summary>
    /// Assigns every slot of the range to a task, or marks it as not work when <paramref name="forecastTaskId"/> is null.
    /// <paramref name="notes"/> replaces any earlier comment of these slots (ignored for "not work").
    /// </summary>
    public Task AssignSlotsAsync(SlotRange range, int? forecastTaskId, string? notes = null, CancellationToken cancellationToken = default) =>
        AssignSlotStartsAsync(range.SlotStarts().ToList(), forecastTaskId, notes, cancellationToken);

    /// <summary>
    /// Same as <see cref="AssignSlotsAsync"/> for any set of slots (they need not be contiguous), saved in one go.
    /// Slots newly put on a task teach the prediction (<see cref="PredictionFeedback"/>); confirming a predicted slot goes through here too.
    /// </summary>
    public async Task AssignSlotStartsAsync(IReadOnlyCollection<DateTime> slotStarts, int? forecastTaskId, string? notes = null, CancellationToken cancellationToken = default)
    {
        var starts = slotStarts.Distinct().Order().ToList();
        if (starts.Count == 0)
            return;
        var (first, last) = (starts[0], starts[^1]);
        var comment = forecastTaskId is null ? null : NormalizeComment(notes);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.SlotOverrides
            .Where(o => o.SlotStartUtc >= first && o.SlotStartUtc <= last)
            .ToDictionaryAsync(o => o.SlotStartUtc, cancellationToken);

        var learned = new List<DateTime>();
        var unlearned = new List<SlotOverride>();
        foreach (var start in starts)
        {
            if (existing.GetValueOrDefault(start) is { } previous && previous.ForecastTaskId != forecastTaskId && previous.ForecastTaskId is not null)
                unlearned.Add(new SlotOverride { SlotStartUtc = start, ForecastTaskId = previous.ForecastTaskId });
            if (existing.GetValueOrDefault(start)?.ForecastTaskId != forecastTaskId)
                learned.Add(start);
            if (existing.TryGetValue(start, out var slot))
            {
                slot.ForecastTaskId = forecastTaskId;
                slot.Notes = comment;
            }
            else
            {
                db.SlotOverrides.Add(new SlotOverride { SlotStartUtc = start, ForecastTaskId = forecastTaskId, Notes = comment });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        await ForgetAsync(unlearned, cancellationToken);
        if (forecastTaskId is { } taskId && learned.Count > 0)
            await feedback.RecordAsync(learned, taskId, cancellationToken);
    }

    /// <summary>
    /// Clears slots for good ("Effacer"): their manual assignment is dropped and every activity linked to them is erased.
    /// The slots are tombstoned, so the engine ignores whatever a later sync brings back for them (same ExternalId, growing segment).
    /// An activity that also touches a slot still alive (a long meeting, a point event whose window reaches back) is kept,
    /// but ignored inside the deleted slots. Not to be confused with <c>AssignSlotStartsAsync(starts, null)</c> ("Pas du travail"), which keeps the activities.
    /// There is no undo: erased activities cannot come back.
    /// </summary>
    public async Task<SlotRemovalResult> RemoveSlotsAsync(IReadOnlyCollection<DateTime> slotStarts, CancellationToken cancellationToken = default)
    {
        var starts = slotStarts.Select(s => DateTime.SpecifyKind(s, DateTimeKind.Utc)).Distinct().Order().ToList();
        if (starts.Count == 0)
            return new SlotRemovalResult(0, 0, 0);
        var (first, last) = (starts[0], starts[^1]);
        var from = first - WeekProposalBuilder.LookBehind;
        var to = last + EngineOptions.SlotLength + WeekProposalBuilder.LookAhead;
        var wanted = starts.ToHashSet();

        var activities = await activityStore.GetRangeAsync(from, to, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.Rules.AsNoTracking().ToListAsync(cancellationToken);
        var alreadyRemoved = await db.RemovedSlots.AsNoTracking()
            .Where(r => r.SlotStartUtc >= from && r.SlotStartUtc < to)
            .Select(r => r.SlotStartUtc)
            .ToListAsync(cancellationToken);

        var allRemoved = alreadyRemoved.Concat(starts).ToHashSet();
        foreach (var start in starts.Except(alreadyRemoved))
            db.RemovedSlots.Add(new RemovedSlot { SlotStartUtc = start });

        var overrides = await db.SlotOverrides
            .Where(o => o.SlotStartUtc >= first && o.SlotStartUtc <= last)
            .ToListAsync(cancellationToken);
        var dropped = overrides.Where(o => wanted.Contains(o.SlotStartUtc)).ToList();
        db.SlotOverrides.RemoveRange(dropped);
        await ForgetAsync(dropped, cancellationToken);

        var windows = resolver.Resolve(activities, rules).ToDictionary(r => r.Activity, r => r.Window);
        var doomed = new HashSet<long>();
        var kept = 0;
        foreach (var activity in activities)
        {
            var window = windows.TryGetValue(activity, out var resolved) ? resolved : new Interval(activity.StartUtc, activity.EndUtc ?? activity.StartUtc);
            if (!RemovedSlots.Touched(window).Any(wanted.Contains))
                continue;
            if (RemovedSlots.CoversOnly(window, allRemoved))
                doomed.Add(activity.Id);
            else
                kept++;
        }

        if (doomed.Count > 0)
            db.Activities.RemoveRange(await db.Activities.Where(a => doomed.Contains(a.Id)).ToListAsync(cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
        return new SlotRemovalResult(starts.Count, doomed.Count, kept);
    }

    /// <summary>
    /// How each task has been used so far: last day and number of distinct days with hours in a draft or a manual assignment.
    /// Feeds the "Récents" and "Fréquence" orderings of the task picker.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, TaskUsage>> GetTaskUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.TimesheetEntries.AsNoTracking()
            .Where(e => e.Hours > 0)
            .Select(e => new { e.ForecastTaskId, e.Date })
            .ToListAsync(cancellationToken);
        var overrides = await db.SlotOverrides.AsNoTracking()
            .Where(o => o.ForecastTaskId != null)
            .Select(o => new { ForecastTaskId = o.ForecastTaskId!.Value, o.SlotStartUtc })
            .ToListAsync(cancellationToken);

        return entries.Select(e => (e.ForecastTaskId, e.Date))
            .Concat(overrides.Select(o => (o.ForecastTaskId, DateOnly.FromDateTime(o.SlotStartUtc))))
            .GroupBy(u => u.ForecastTaskId)
            .ToDictionary(g => g.Key, g => new TaskUsage(g.Key, g.Max(u => u.Item2), g.Select(u => u.Item2).Distinct().Count()));
    }

    /// <summary>The latest distinct comments given when assigning slots to a task, most recent first.</summary>
    public async Task<IReadOnlyList<string>> GetRecentCommentsAsync(int forecastTaskId, int max = 8, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var notes = await db.SlotOverrides.AsNoTracking()
            .Where(o => o.ForecastTaskId == forecastTaskId && o.Notes != null && o.Notes != "")
            .OrderByDescending(o => o.SlotStartUtc)
            .Select(o => o.Notes!)
            .Take(max * 50)
            .ToListAsync(cancellationToken);

        return notes.Distinct(StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
    }

    public const int MaxCommentLength = 500;

    private static string? NormalizeComment(string? notes)
    {
        var text = notes?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length > MaxCommentLength ? text[..MaxCommentLength] : text;
    }

    /// <summary>Removes manual assignments in the range: the engine decides again.</summary>
    public Task ClearSlotsAsync(SlotRange range, CancellationToken cancellationToken = default) =>
        ClearSlotStartsAsync(range.SlotStarts().ToList(), cancellationToken);

    public async Task ClearSlotStartsAsync(IReadOnlyCollection<DateTime> slotStarts, CancellationToken cancellationToken = default)
    {
        var starts = slotStarts.Distinct().Order().ToList();
        if (starts.Count == 0)
            return;
        var (first, last) = (starts[0], starts[^1]);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.SlotOverrides
            .Where(o => o.SlotStartUtc >= first && o.SlotStartUtc <= last)
            .ToListAsync(cancellationToken);
        var wanted = starts.ToHashSet();
        var dropped = existing.Where(o => wanted.Contains(o.SlotStartUtc)).ToList();
        db.SlotOverrides.RemoveRange(dropped);
        await db.SaveChangesAsync(cancellationToken);
        await ForgetAsync(dropped, cancellationToken);
    }

    /// <summary>Withdraws what <see cref="PredictionFeedback"/> learned from these manual assignments (activities must still exist).</summary>
    private async Task ForgetAsync(IEnumerable<SlotOverride> overrides, CancellationToken cancellationToken)
    {
        foreach (var byTask in overrides.Where(o => o.ForecastTaskId is not null).GroupBy(o => o.ForecastTaskId!.Value))
            await feedback.ForgetAsync(byTask.Select(o => o.SlotStartUtc).ToList(), byTask.Key, cancellationToken);
    }

    /// <summary>
    /// Accepts the cached classifier suggestion for a group by storing a rule (origin "claude").
    /// The classifier's suggested rule is used when it matches the group's activities of <paramref name="weekStart"/>
    /// (or when no week is given); otherwise a rule is derived from the group key.
    /// </summary>
    /// <returns>The stored rule, or null when there is no suggestion with a task for this group.</returns>
    public async Task<Rule?> AcceptSuggestionAsync(string groupKey, DateOnly? weekStart = null, CancellationToken cancellationToken = default)
    {
        List<Core.Forecast.ForecastTask> tasks;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            tasks = await db.ForecastTasks.AsNoTracking().Where(t => t.IsActive).ToListAsync(cancellationToken);

        var cached = await classification.GetCachedAsync([groupKey], tasks, cancellationToken);
        if (!cached.TryGetValue(groupKey, out var verdict) || verdict.TaskId is not { } taskId)
            return null;

        RuleProposal? proposal = null;
        if (verdict.SuggestedRule is { } suggested)
        {
            var members = weekStart is { } week
                ? await GroupActivitiesAsync(WeekRange(week), groupKey, cancellationToken)
                : null;
            if (members is null || members.Any(a => ruleEvaluator.Matches(suggested.Field, suggested.Operator, suggested.Pattern, a)))
            {
                proposal = new RuleProposal(suggested.Field, suggested.Operator, suggested.Pattern, taskId,
                    $"{CorrectionLearner.FieldLabel(suggested.Field)} : {suggested.Pattern}");
            }
        }

        proposal ??= learner.ProposeFromGroupKey(groupKey, taskId);
        return proposal is null ? null : await learner.PersistAsync(proposal, CorrectionLearner.ClaudeOrigin, cancellationToken);
    }

    /// <summary>Rule the engine would learn from a correction, without storing it (for a confirmation dialog).</summary>
    public async Task<RuleProposal?> ProposeRuleFromCorrectionAsync(CorrectionTarget target, int forecastTaskId, CancellationToken cancellationToken = default)
    {
        var range = new SlotRange(target.Range.StartUtc, target.Range.EndUtc);
        var activities = await GroupActivitiesAsync(range, target.GroupKey, cancellationToken);
        return learner.Propose(activities, forecastTaskId)
            ?? (target.GroupKey is null ? null : learner.ProposeFromGroupKey(target.GroupKey, forecastTaskId));
    }

    /// <summary>Learns and stores a rule (origin "correction") from a user correction. Null when nothing stable was found.</summary>
    public async Task<Rule?> CreateRuleFromCorrectionAsync(CorrectionTarget target, int forecastTaskId, CancellationToken cancellationToken = default)
    {
        var proposal = await ProposeRuleFromCorrectionAsync(target, forecastTaskId, cancellationToken);
        return proposal is null ? null : await learner.PersistAsync(proposal, CorrectionLearner.CorrectionOrigin, cancellationToken);
    }

    /// <summary>Recomputes the week and writes it to the draft, unless the draft was already submitted.</summary>
    public async Task<DraftSaveResult> RebuildDraftAsync(DateOnly monday, CancellationToken cancellationToken = default)
    {
        var proposal = await builder.BuildAsync(monday, classifyMissing: true, cancellationToken);
        return await builder.SaveDraftAsync(proposal, cancellationToken);
    }

    private SlotRange WeekRange(DateOnly week)
    {
        var start = WeekProposalBuilder.WeekStartOf(week);
        var (from, _) = allocator.DayBounds(start);
        var (_, to) = allocator.DayBounds(start.AddDays(6));
        return new SlotRange(from, to);
    }

    /// <summary>Work activities overlapping the range, restricted to an unclassified group when given.</summary>
    private async Task<IReadOnlyList<ActivityEvent>> GroupActivitiesAsync(SlotRange range, string? groupKey, CancellationToken cancellationToken)
    {
        var activities = await activityStore.GetRangeAsync(range.StartUtc - TimeSpan.FromDays(1), range.EndUtc, cancellationToken);
        List<Rule> rules;
        HashSet<DateTime> removed;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            rules = await db.Rules.AsNoTracking().ToListAsync(cancellationToken);
            removed = (await db.RemovedSlots.AsNoTracking()
                .Where(r => r.SlotStartUtc >= range.StartUtc - WeekProposalBuilder.LookBehind && r.SlotStartUtc < range.EndUtc + WeekProposalBuilder.LookAhead)
                .Select(r => r.SlotStartUtc)
                .ToListAsync(cancellationToken)).ToHashSet();
        }

        var window = new Interval(range.StartUtc, range.EndUtc);
        return resolver.Resolve(activities, rules)
            .Where(r => r.Role != ActivityRole.Mask && r.Window.Overlaps(window) && !RemovedSlots.CoversOnly(r.Window, removed))
            .Where(r => groupKey is null || r.Candidate?.GroupKey == groupKey)
            .Select(r => r.Activity)
            .ToList();
    }
}
