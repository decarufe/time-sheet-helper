using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>Builders of gaps, analyses and reports for the tests of the Submit page logic.</summary>
internal static class GapFx
{
    public static readonly DateOnly Monday = new(2026, 10, 12);

    public static DateOnly Day(int offset) => Monday.AddDays(offset);

    public static SubmissionGap Gap(
        SubmissionGapType type = SubmissionGapType.TaskNotFound,
        SubmissionGapSeverity severity = SubmissionGapSeverity.Blocking,
        int? taskId = 1,
        int day = 0,
        string? title = null,
        string message = "Message",
        IReadOnlyList<SubmissionGapAction>? actions = null,
        IReadOnlyList<SubmissionGapCell>? related = null,
        decimal? expected = null,
        decimal? forecast = null)
    {
        var date = Monday.AddDays(day);
        return new SubmissionGap
        {
            Id = $"{type}|{taskId}|{date:yyyyMMdd}",
            Type = type,
            Severity = severity,
            Date = date,
            Cell = taskId is { } id ? new SubmissionGapCell(id, $"Tâche {id}", date) : null,
            RelatedCells = related ?? [],
            Title = title ?? $"Écart {type}",
            Message = message,
            ExpectedHours = expected,
            ForecastHours = forecast,
            DeltaHours = expected is { } e && forecast is { } f ? f - e : null,
            Actions = actions ?? [],
        };
    }

    public static SubmissionGapAction Action(
        SubmissionGapResolution resolution, bool recommended = false, bool available = true, string? reason = null) =>
        new(resolution, recommended, available, reason);

    public static SubmissionSummary Summary(
        int blocking = 0,
        int toVerify = 0,
        int cells = 0,
        int cellsWithoutGap = 0,
        decimal weekHours = 0,
        decimal? target = null,
        bool? within = null,
        bool hasSimulation = false,
        bool canFill = false,
        decimal toleranceHours = 0.25m,
        decimal nonProject = 0,
        IReadOnlyList<SubmissionDaySummary>? days = null) =>
        new(blocking, toVerify, 0, cells, cellsWithoutGap, weekHours, target, target is { } t ? weekHours - t : null, within, 0, 0,
            toleranceHours, hasSimulation, true, canFill, days ?? [], nonProject);

    public static SubmissionAnalysis Analysis(IReadOnlyList<SubmissionGap> gaps, SubmissionSummary? summary = null) =>
        new(Monday, gaps, summary ?? Summary());

    public static ForecastTask Task(int id, string name, string project = "Projet", string externalId = "") =>
        new() { Id = id, ExternalId = externalId.Length > 0 ? externalId : $"T{id}", ProjectName = project, TaskName = name };

    public static ForecastCellResult Cell(int taskId, int day, ForecastCellStatus status = ForecastCellStatus.Planned, decimal expected = 1m) =>
        new(taskId, $"Tâche {taskId}", Day(day), expected, null, status);

    public static ForecastFillReport Report(params ForecastCellResult[] cells)
    {
        var report = new ForecastFillReport { WeekStart = Monday, Mode = ForecastSubmitMode.DryRun };
        report.Cells.AddRange(cells);
        return report;
    }
}
