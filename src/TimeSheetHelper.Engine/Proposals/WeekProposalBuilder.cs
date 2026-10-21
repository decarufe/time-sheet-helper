using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Learning;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Engine.Proposals;

/// <summary>Builds the week proposal (hours per day per task, gaps, unclassified groups) and writes it to the draft.</summary>
public sealed class WeekProposalBuilder(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ActivityStore activityStore,
    ActivityResolver resolver,
    SlotAllocator allocator,
    ClassificationService classification,
    IOptions<EngineOptions> options)
{
    private const decimal HoursPerSlot = 0.25m;

    /// <summary>Activities this long before a range are loaded so commit windows can see the previous commit.</summary>
    internal static readonly TimeSpan LookBehind = TimeSpan.FromDays(1);

    /// <summary>Point events shortly after a range still reach into it through their evidence window.</summary>
    internal static readonly TimeSpan LookAhead = TimeSpan.FromHours(2);

    public static DateOnly WeekStartOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <param name="classifyMissing">Call the classifier for groups without a cached verdict (otherwise cache only).</param>
    public async Task<WeekProposal> BuildAsync(DateOnly weekStart, bool classifyMissing = true, CancellationToken cancellationToken = default)
    {
        weekStart = WeekStartOf(weekStart);
        var days = Enumerable.Range(0, 7).Select(weekStart.AddDays).ToList();
        var range = new Interval(allocator.DayBounds(days[0]).StartUtc, allocator.DayBounds(days[^1]).EndUtc);

        var data = await LoadAsync(range, cancellationToken);
        var resolved = Resolve(data);

        var slotsByDay = new Dictionary<DateOnly, IReadOnlyList<SlotAssignment>>();
        foreach (var day in days)
            slotsByDay[day] = allocator.AllocateDay(day, resolved, data.Overrides, data.IgnoredTaskIds, data.RemovedSlots);

        var dayProposals = days.Select(d => SummarizeDay(d, slotsByDay[d])).ToList();

        var groupMinutes = slotsByDay.Values
            .SelectMany(s => s)
            .Where(s => s.IsWork && s.TaskId is null && s.GroupKey is not null)
            .GroupBy(s => s.GroupKey!)
            .ToDictionary(g => g.Key, g => g.Count() * (int)EngineOptions.SlotLength.TotalMinutes);

        var groups = groupMinutes
            .OrderByDescending(g => g.Value)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => DescribeGroup(g.Key, TimeSpan.FromMinutes(g.Value), resolved, range))
            .ToList();

        var assignable = data.Tasks.Where(t => !t.IsIgnored).ToList();
        var assignableRules = data.Rules.Where(r => !data.IgnoredTaskIds.Contains(r.ForecastTaskId)).ToList();
        var verdicts = classifyMissing
            ? await classification.ClassifyAsync(groups, assignable, assignableRules, cancellationToken)
            : await classification.GetCachedAsync(groups.Select(g => g.Key), assignable.Where(t => t.IsActive).ToList(), cancellationToken);

        var groupProposals = groups
            .Select(g => new GroupProposal(
                g.Key,
                GroupKey.TryParse(g.Key, out var key) ? key.Kind : GroupKind.Source,
                groupMinutes[g.Key],
                g,
                verdicts.GetValueOrDefault(g.Key)))
            .ToList();

        return new WeekProposal(
            weekStart,
            dayProposals,
            groupProposals,
            slotsByDay,
            data.Tasks.ToDictionary(t => t.Id),
            data.SubmittedUtc is not null);
    }

    /// <summary>Slots of one local day with the activities they cite.</summary>
    public async Task<DayTimeline> BuildDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var (start, end) = allocator.DayBounds(day);
        var data = await LoadAsync(new Interval(start, end), cancellationToken);
        var resolved = Resolve(data);
        var slots = allocator.AllocateDay(day, resolved, data.Overrides, data.IgnoredTaskIds, data.RemovedSlots);

        var cited = slots.SelectMany(s => s.Evidence).ToHashSet();
        var activities = data.Activities.Where(a => cited.Contains(a.Id)).ToDictionary(a => a.Id);
        return new DayTimeline(day, slots, activities, data.Tasks.ToDictionary(t => t.Id));
    }

    /// <summary>
    /// Writes the proposal's classified hours into the week's draft. A submitted draft is never modified.
    /// Comments given when assigning slots become the entry's notes; otherwise notes of entries that still exist (same day and task) are kept.
    /// </summary>
    public async Task<DraftSaveResult> SaveDraftAsync(WeekProposal proposal, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var draft = await db.TimesheetDrafts
            .Include(d => d.Entries)
            .FirstOrDefaultAsync(d => d.WeekStart == proposal.WeekStart, cancellationToken);

        if (draft?.SubmittedUtc is not null)
            return new DraftSaveResult(draft, AlreadySubmitted: true);

        if (draft is null)
        {
            draft = new TimesheetDraft { WeekStart = proposal.WeekStart };
            db.TimesheetDrafts.Add(draft);
        }

        var knownTasks = await db.ForecastTasks.Where(t => t.ExternalId != ForecastTask.IgnoredExternalId).Select(t => t.Id).ToListAsync(cancellationToken);
        var notes = draft.Entries
            .Where(e => !string.IsNullOrEmpty(e.Notes))
            .GroupBy(e => (e.Date, e.ForecastTaskId))
            .ToDictionary(g => g.Key, g => g.First().Notes);

        db.TimesheetEntries.RemoveRange(draft.Entries);
        draft.Entries.Clear();

        foreach (var day in proposal.Days)
        {
            foreach (var task in day.Tasks.Where(t => t.Hours > 0 && knownTasks.Contains(t.ForecastTaskId)))
            {
                draft.Entries.Add(new TimesheetEntry
                {
                    Date = day.Date,
                    ForecastTaskId = task.ForecastTaskId,
                    Hours = task.Hours,
                    Notes = task.Notes ?? notes.GetValueOrDefault((day.Date, task.ForecastTaskId)),
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DraftSaveResult(draft, AlreadySubmitted: false);
    }

    private static string? JoinNotes(IEnumerable<string?> notes)
    {
        var distinct = notes
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return distinct.Count == 0 ? null : string.Join(" ; ", distinct);
    }

    internal DayProposal SummarizeDay(DateOnly day, IReadOnlyList<SlotAssignment> slots)
    {
        var tasks = slots
            .Where(s => s.IsClassified)
            .GroupBy(s => s.TaskId!.Value)
            .Select(g => new DayTaskHours(g.Key, g.Count() * HoursPerSlot, JoinNotes(g.Select(s => s.Notes))))
            .OrderByDescending(t => t.Hours)
            .ThenBy(t => t.ForecastTaskId)
            .ToList();

        var total = tasks.Sum(t => t.Hours);
        var unclassified = slots.Count(s => s.IsWork && s.TaskId is null) * HoursPerSlot;
        var target = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0m : options.Value.TargetHoursPerDay;
        var flag = total < target ? DayFlag.UnderTarget : total > target ? DayFlag.OverTarget : DayFlag.Ok;

        return new DayProposal(day, tasks, total, target, unclassified, flag);
    }

    internal ActivityGroup DescribeGroup(string key, TimeSpan duration, IReadOnlyList<ResolvedActivity> resolved, Interval range)
    {
        var max = options.Value.Claude.MaxSamples;
        var members = resolved
            .Where(r => r.Candidate?.GroupKey == key && r.Window.Overlaps(range))
            .OrderByDescending(r => r.Window.Length)
            .Select(r => r.Activity)
            .ToList();

        static List<string> Samples(IEnumerable<string?> values, int max) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();

        return new ActivityGroup(
            key,
            members.Select(a => a.Source).Distinct().Order().ToList(),
            Samples(members.Select(a => a.Title), max),
            Samples(members.Select(a => a.FilePath ?? a.Folder ?? a.Url), max),
            Samples(members.Select(a => a.Subject), max),
            duration);
    }

    /// <summary>Resolves the activities, leaving out those whose every slot was deleted by the user (e.g. re-ingested by a collector).</summary>
    private IReadOnlyList<ResolvedActivity> Resolve(WeekData data) =>
        resolver.Resolve(data.Activities, data.Rules, data.Feedback)
            .Where(r => !RemovedSlots.CoversOnly(r.Window, data.RemovedSlots))
            .ToList();

    private async Task<WeekData> LoadAsync(Interval range, CancellationToken cancellationToken)
    {
        var activities = await activityStore.GetRangeAsync(range.Start - LookBehind, range.End + LookAhead, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.Rules.AsNoTracking().ToListAsync(cancellationToken);
        var tasks = await db.ForecastTasks.AsNoTracking().ToListAsync(cancellationToken);
        var overrides = await db.SlotOverrides.AsNoTracking()
            .Where(o => o.SlotStartUtc >= range.Start && o.SlotStartUtc < range.End)
            .ToListAsync(cancellationToken);
        var removed = (await db.RemovedSlots.AsNoTracking()
            .Where(r => r.SlotStartUtc >= range.Start - LookBehind && r.SlotStartUtc < range.End + LookAhead)
            .Select(r => r.SlotStartUtc)
            .ToListAsync(cancellationToken)).ToHashSet();
        var weekStart = WeekStartOf(allocator.LocalDate(range.Start));
        var submitted = await db.TimesheetDrafts.AsNoTracking()
            .Where(d => d.WeekStart == weekStart)
            .Select(d => d.SubmittedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var feedback = await PredictionFeedback.LoadAsync(db, cancellationToken);

        return new WeekData(activities, rules, tasks, overrides, submitted, removed, feedback);
    }

    private sealed record WeekData(
        IReadOnlyList<ActivityEvent> Activities,
        IReadOnlyList<Rule> Rules,
        IReadOnlyList<ForecastTask> Tasks,
        IReadOnlyList<SlotOverride> Overrides,
        DateTime? SubmittedUtc,
        IReadOnlySet<DateTime> RemovedSlots,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Feedback)
    {
        public IReadOnlySet<int> IgnoredTaskIds { get; } = Tasks.Where(t => t.IsIgnored).Select(t => t.Id).ToHashSet();
    }
}
