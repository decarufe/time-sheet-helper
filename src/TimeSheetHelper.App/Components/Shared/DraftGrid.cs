using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

public sealed record DraftRow(int TaskId, string Name, IReadOnlyDictionary<DateOnly, decimal> Hours, decimal Total)
{
    /// <summary>The task is left out of the fill (flagged, or ignored for this submission): its hours stay in the draft and are not sent.</summary>
    public bool Ignored { get; init; }

    /// <summary>The task is left out because of its own flag (not by the user's choice for this submission).</summary>
    public bool Automatic { get; init; }

    /// <summary>The task carries the flag, whether it is currently left out or the user re-included it.</summary>
    public bool Flagged { get; init; }
}

/// <summary>Where a day stands against its objective (within the tolerance, below, above) or unknown without an analysis.</summary>
public enum ObjectiveFlag { Unknown, Within, Below, Above }

/// <summary>The gap a cell of the draft table points at (the most severe open one).</summary>
public sealed record CellGapRef(string GapId, SubmissionGapSeverity Severity, string Title);

/// <summary>
/// The draft as the table of the Submit page shows it: the classified rows, the daily totals, and — read only — the unclassified work,
/// the objective and the time Forecast holds without a project (left out of the total). No Blazor: testable.
/// </summary>
public sealed class DraftGrid
{
    private readonly IReadOnlyDictionary<(int TaskId, DateOnly Day), CellGapRef> cellGaps;
    private readonly IReadOnlyDictionary<DateOnly, SubmissionDaySummary> summaryDays;

    private DraftGrid(
        IReadOnlyList<DraftRow> rows, IReadOnlyList<DateOnly> days, IReadOnlyDictionary<DateOnly, decimal> dayTotals,
        IReadOnlyDictionary<DateOnly, decimal> unclassified, IReadOnlyDictionary<DateOnly, decimal>? targets,
        IReadOnlyDictionary<DateOnly, decimal> nonProject, IReadOnlyDictionary<(int, DateOnly), CellGapRef> cellGaps,
        IReadOnlyDictionary<DateOnly, SubmissionDaySummary> summaryDays)
    {
        Rows = rows;
        Days = days;
        DayTotals = dayTotals;
        Unclassified = unclassified;
        Targets = targets;
        NonProject = nonProject;
        this.cellGaps = cellGaps;
        this.summaryDays = summaryDays;
    }

    public IReadOnlyList<DraftRow> Rows { get; }

    /// <summary>The columns: Monday to Friday, and a weekend day only when it holds something.</summary>
    public IReadOnlyList<DateOnly> Days { get; }

    public IReadOnlyDictionary<DateOnly, decimal> DayTotals { get; }

    public decimal GrandTotal => DayTotals.Values.Sum();

    /// <summary>Hours of the ignored tasks per day: counted in the totals (the work was done), not sent to Forecast.</summary>
    public decimal IgnoredHours(DateOnly day) => Rows.Where(r => r.Ignored).Sum(r => r.Hours.GetValueOrDefault(day));

    public decimal IgnoredTotal => Rows.Where(r => r.Ignored).Sum(r => r.Total);

    public bool HasIgnored => Rows.Any(r => r.Ignored);

    /// <summary>Work with no task yet (not in the totals).</summary>
    public IReadOnlyDictionary<DateOnly, decimal> Unclassified { get; }

    public decimal UnclassifiedTotal => Unclassified.Values.Sum();

    /// <summary>The daily objective; null when the week's objective is unknown.</summary>
    public IReadOnlyDictionary<DateOnly, decimal>? Targets { get; }

    public decimal? TargetTotal => Targets?.Values.Sum();

    /// <summary>Time Forecast holds without a project, per day (known after a simulation; not in the totals).</summary>
    public IReadOnlyDictionary<DateOnly, decimal> NonProject { get; }

    public decimal NonProjectTotal => NonProject.Values.Sum();

    public bool HasUnclassified => UnclassifiedTotal > 0;

    public bool HasNonProject => NonProjectTotal > 0;

    public CellGapRef? GapOf(int taskId, DateOnly day) => cellGaps.GetValueOrDefault((taskId, day));

    /// <summary>The day against its objective, with the tolerance of the analysis.</summary>
    public ObjectiveFlag FlagOf(DateOnly day) =>
        !summaryDays.TryGetValue(day, out var summary) ? ObjectiveFlag.Unknown
        : summary.WithinTolerance ? ObjectiveFlag.Within
        : summary.DeltaHours < 0 ? ObjectiveFlag.Below
        : ObjectiveFlag.Above;

    /// <param name="isOpen">Whether a gap still counts (an ignored gap does not mark its cell); null = all do.</param>
    /// <param name="ignored">Tasks left out of the fill; their rows are marked. Null: none.</param>
    public static DraftGrid Build(
        TimesheetDraft draft, IReadOnlyDictionary<int, ForecastTask> tasks, DateOnly monday, SubmissionAnalysis? analysis, Func<SubmissionGap, bool>? isOpen = null,
        IReadOnlySet<int>? ignored = null)
    {
        var weekDays = Enumerable.Range(0, 7).Select(monday.AddDays).ToList();
        var automatic = SubmissionTaskScope.AutoExcluded(tasks.Values);
        var byCell = draft.Entries
            .GroupBy(e => (e.ForecastTaskId, e.Date))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Hours));

        var rows = byCell.Keys.Select(k => k.ForecastTaskId).Distinct()
            .Select(id =>
            {
                var hours = weekDays.ToDictionary(d => d, d => byCell.GetValueOrDefault((id, d)));
                var name = tasks.TryGetValue(id, out var task) ? task.DisplayName : $"Tâche #{id} inconnue";
                var isIgnored = ignored?.Contains(id) == true;
                return new DraftRow(id, name, hours, hours.Values.Sum())
                {
                    Ignored = isIgnored,
                    Automatic = isIgnored && automatic.Contains(id),
                    Flagged = automatic.Contains(id),
                };
            })
            .Where(r => r.Total > 0)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var summaryDays = analysis?.Summary.Days ?? [];
        var unclassified = weekDays.ToDictionary(d => d, d => summaryDays.FirstOrDefault(s => s.Date == d)?.UnclassifiedHours ?? 0m);
        var nonProject = weekDays.ToDictionary(d => d, d => summaryDays.FirstOrDefault(s => s.Date == d)?.ForecastNonProjectHours ?? 0m);
        IReadOnlyDictionary<DateOnly, decimal>? targets = summaryDays.Count == 0
            ? null
            : weekDays.ToDictionary(d => d, d => summaryDays.FirstOrDefault(s => s.Date == d)?.TargetHours ?? 0m);

        var days = weekDays
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                        || rows.Any(r => r.Hours[d] > 0) || unclassified[d] > 0 || nonProject[d] > 0)
            .ToList();
        var dayTotals = weekDays.ToDictionary(d => d, d => rows.Sum(r => r.Hours[d]));

        return new DraftGrid(
            rows, days, dayTotals, unclassified, targets, nonProject, CellGaps(analysis, isOpen ?? (_ => true)),
            summaryDays.ToDictionary(s => s.Date));
    }

    private static Dictionary<(int, DateOnly), CellGapRef> CellGaps(SubmissionAnalysis? analysis, Func<SubmissionGap, bool> isOpen)
    {
        var map = new Dictionary<(int, DateOnly), CellGapRef>();
        if (analysis is null)
            return map;

        // Gaps come most severe first: the first one met for a cell is the one it points at.
        foreach (var gap in analysis.Gaps.Where(isOpen))
        {
            if (gap.Cell is null)
                continue;
            foreach (var cell in gap.RelatedCells.Prepend(gap.Cell))
                map.TryAdd((cell.ForecastTaskId, cell.Date), new CellGapRef(gap.Id, gap.Severity, gap.Title));
        }

        return map;
    }
}
