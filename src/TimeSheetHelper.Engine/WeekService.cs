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

/// <summary>Façade used by the UI: week and day views, manual assignments, suggestions and learned rules.</summary>
public sealed class WeekService(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ActivityStore activityStore,
    ActivityResolver resolver,
    WeekProposalBuilder builder,
    SlotAllocator allocator,
    ClassificationService classification,
    CorrectionLearner learner,
    RuleEvaluator ruleEvaluator)
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

    /// <summary>Same as <see cref="AssignSlotsAsync"/> for any set of slots (they need not be contiguous), saved in one go.</summary>
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

        foreach (var start in starts)
        {
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
        db.SlotOverrides.RemoveRange(existing.Where(o => wanted.Contains(o.SlotStartUtc)));
        await db.SaveChangesAsync(cancellationToken);
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
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            rules = await db.Rules.AsNoTracking().ToListAsync(cancellationToken);

        var window = new Interval(range.StartUtc, range.EndUtc);
        return resolver.Resolve(activities, rules)
            .Where(r => r.Role != ActivityRole.Mask && r.Window.Overlaps(window))
            .Where(r => groupKey is null || r.Candidate?.GroupKey == groupKey)
            .Select(r => r.Activity)
            .ToList();
    }
}
