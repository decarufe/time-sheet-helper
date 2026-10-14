using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

public sealed class DraftGridTests
{
    private static TimesheetDraft Draft(params (int Task, int Day, decimal Hours)[] entries) => new()
    {
        WeekStart = GapFx.Monday,
        Entries = entries.Select(e => new TimesheetEntry { ForecastTaskId = e.Task, Date = GapFx.Day(e.Day), Hours = e.Hours }).ToList(),
    };

    private static Dictionary<int, ForecastTask> Tasks(params ForecastTask[] tasks) => tasks.ToDictionary(t => t.Id);

    private static SubmissionDaySummary DaySummary(
        int day, decimal target = 8m, decimal draft = 8m, decimal unclassified = 0, decimal nonProject = 0, decimal tolerance = 0.25m) =>
        new(GapFx.Day(day), draft, target, draft - target, Math.Abs(draft - target) <= tolerance, unclassified, 0, nonProject);

    private static SubmissionAnalysis WithDays(IReadOnlyList<SubmissionGap> gaps, params SubmissionDaySummary[] days) =>
        GapFx.Analysis(gaps, GapFx.Summary(days: days));

    [Fact]
    public void Entries_are_summed_per_task_and_day_and_rows_follow_the_task_name()
    {
        var draft = Draft((2, 0, 1m), (2, 0, 0.5m), (1, 1, 2m), (2, 2, 3m));
        var tasks = Tasks(GapFx.Task(1, "Zèbre", "A"), GapFx.Task(2, "Alpha", "A"));

        var grid = DraftGrid.Build(draft, tasks, GapFx.Monday, null);

        Assert.Equal(2, grid.Rows.Count);
        Assert.Equal(2, grid.Rows[0].TaskId);
        Assert.Equal(1.5m, grid.Rows[0].Hours[GapFx.Day(0)]);
        Assert.Equal(4.5m, grid.Rows[0].Total);
        Assert.Equal(1, grid.Rows[1].TaskId);
        Assert.Equal(2m, grid.Rows[1].Total);
        Assert.Equal(6.5m, grid.GrandTotal);
    }

    [Fact]
    public void Rows_without_hours_are_left_out_and_unknown_tasks_are_named_as_such()
    {
        var draft = Draft((1, 0, 0m), (9, 1, 1m));

        var grid = DraftGrid.Build(draft, Tasks(GapFx.Task(1, "Vide")), GapFx.Monday, null);

        var row = Assert.Single(grid.Rows);
        Assert.Equal("Tâche #9 inconnue", row.Name);
    }

    [Fact]
    public void Day_totals_add_up_the_classified_rows_of_each_day()
    {
        var draft = Draft((1, 0, 2m), (2, 0, 1.25m), (1, 1, 4m));

        var grid = DraftGrid.Build(draft, Tasks(GapFx.Task(1, "A"), GapFx.Task(2, "B")), GapFx.Monday, null);

        Assert.Equal(3.25m, grid.DayTotals[GapFx.Day(0)]);
        Assert.Equal(4m, grid.DayTotals[GapFx.Day(1)]);
        Assert.Equal(0m, grid.DayTotals[GapFx.Day(2)]);
    }

    [Fact]
    public void The_columns_are_the_working_days_and_a_weekend_day_only_when_it_holds_something()
    {
        var tasks = Tasks(GapFx.Task(1, "A"));

        var weekdays = DraftGrid.Build(Draft((1, 0, 1m)), tasks, GapFx.Monday, null);
        var saturday = DraftGrid.Build(Draft((1, 0, 1m), (1, 5, 1m)), tasks, GapFx.Monday, null);
        var sundayUnclassified = DraftGrid.Build(
            Draft((1, 0, 1m)), tasks, GapFx.Monday, WithDays([], DaySummary(6, target: 0, draft: 0, unclassified: 0.5m)));
        var sundayNonProject = DraftGrid.Build(
            Draft((1, 0, 1m)), tasks, GapFx.Monday, WithDays([], DaySummary(6, target: 0, draft: 0, nonProject: 1m)));

        Assert.Equal(5, weekdays.Days.Count);
        Assert.Equal(DayOfWeek.Monday, weekdays.Days[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, weekdays.Days[^1].DayOfWeek);
        Assert.Equal(6, saturday.Days.Count);
        Assert.Equal(DayOfWeek.Saturday, saturday.Days[^1].DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, sundayUnclassified.Days[^1].DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, sundayNonProject.Days[^1].DayOfWeek);
    }

    [Fact]
    public void Without_an_analysis_there_is_no_objective_no_unclassified_work_and_no_flag()
    {
        var grid = DraftGrid.Build(Draft((1, 0, 1m)), Tasks(GapFx.Task(1, "A")), GapFx.Monday, null);

        Assert.Null(grid.Targets);
        Assert.Null(grid.TargetTotal);
        Assert.False(grid.HasUnclassified);
        Assert.False(grid.HasNonProject);
        Assert.Equal(0m, grid.UnclassifiedTotal);
        Assert.Equal(ObjectiveFlag.Unknown, grid.FlagOf(GapFx.Day(0)));
    }

    [Fact]
    public void The_objective_the_unclassified_work_and_the_time_without_project_come_from_the_analysis_and_stay_out_of_the_total()
    {
        var days = new[]
        {
            DaySummary(0, target: 8m, draft: 6m, unclassified: 1.5m, nonProject: 0.5m),
            DaySummary(1, target: 8m, draft: 8m, unclassified: 0.25m),
            DaySummary(2, target: 8m, draft: 8m),
            DaySummary(3, target: 8m, draft: 8m),
            DaySummary(4, target: 8m, draft: 8m),
        };

        var grid = DraftGrid.Build(Draft((1, 0, 6m)), Tasks(GapFx.Task(1, "A")), GapFx.Monday, WithDays([], days));

        Assert.Equal(1.75m, grid.UnclassifiedTotal);
        Assert.True(grid.HasUnclassified);
        Assert.Equal(1.5m, grid.Unclassified[GapFx.Day(0)]);
        Assert.Equal(0.5m, grid.NonProjectTotal);
        Assert.True(grid.HasNonProject);
        Assert.Equal(40m, grid.TargetTotal);
        Assert.Equal(8m, grid.Targets![GapFx.Day(3)]);
        Assert.Equal(0m, grid.Targets[GapFx.Day(6)]);
        Assert.Equal(6m, grid.GrandTotal);
    }

    [Fact]
    public void Each_day_is_flagged_below_within_or_above_the_objective_with_the_tolerance_of_the_analysis()
    {
        var days = new[]
        {
            DaySummary(0, draft: 6m),
            DaySummary(1, draft: 7.9m),
            DaySummary(2, draft: 8m),
            DaySummary(3, draft: 9m),
        };

        var grid = DraftGrid.Build(Draft((1, 0, 1m)), Tasks(GapFx.Task(1, "A")), GapFx.Monday, WithDays([], days));

        Assert.Equal(ObjectiveFlag.Below, grid.FlagOf(GapFx.Day(0)));
        Assert.Equal(ObjectiveFlag.Within, grid.FlagOf(GapFx.Day(1)));
        Assert.Equal(ObjectiveFlag.Within, grid.FlagOf(GapFx.Day(2)));
        Assert.Equal(ObjectiveFlag.Above, grid.FlagOf(GapFx.Day(3)));
        Assert.Equal(ObjectiveFlag.Unknown, grid.FlagOf(GapFx.Day(4)));
    }

    [Fact]
    public void A_cell_points_at_the_most_severe_gap_that_talks_about_it()
    {
        var blocking = GapFx.Gap(SubmissionGapType.TaskNotFound, SubmissionGapSeverity.Blocking, taskId: 1, day: 0);
        var verify = GapFx.Gap(SubmissionGapType.ForecastWarning, SubmissionGapSeverity.ToVerify, taskId: 1, day: 0);
        var analysis = GapFx.Analysis([blocking, verify]);

        var grid = DraftGrid.Build(Draft((1, 0, 1m)), Tasks(GapFx.Task(1, "A")), GapFx.Monday, analysis);

        var reference = grid.GapOf(1, GapFx.Day(0));
        Assert.NotNull(reference);
        Assert.Equal(blocking.Id, reference.GapId);
        Assert.Equal(SubmissionGapSeverity.Blocking, reference.Severity);
        Assert.Equal(blocking.Title, reference.Title);
        Assert.Null(grid.GapOf(1, GapFx.Day(1)));
        Assert.Null(grid.GapOf(2, GapFx.Day(0)));
    }

    [Fact]
    public void The_related_cells_of_a_gap_are_marked_too()
    {
        var gap = GapFx.Gap(SubmissionGapType.ForecastHigher, taskId: 1, day: 0, related: [new SubmissionGapCell(2, "Tâche 2", GapFx.Day(0))]);

        var grid = DraftGrid.Build(
            Draft((1, 0, 1m), (2, 0, 1m)), Tasks(GapFx.Task(1, "A"), GapFx.Task(2, "B")), GapFx.Monday, GapFx.Analysis([gap]));

        Assert.Equal(gap.Id, grid.GapOf(1, GapFx.Day(0))?.GapId);
        Assert.Equal(gap.Id, grid.GapOf(2, GapFx.Day(0))?.GapId);
    }

    [Fact]
    public void A_gap_that_is_no_longer_open_does_not_mark_its_cell()
    {
        var ignored = GapFx.Gap(SubmissionGapType.ForecastWarning, SubmissionGapSeverity.ToVerify, taskId: 1, day: 0);
        var open = GapFx.Gap(SubmissionGapType.EstimateExceeded, SubmissionGapSeverity.Info, taskId: 1, day: 0);

        var grid = DraftGrid.Build(
            Draft((1, 0, 1m)), Tasks(GapFx.Task(1, "A")), GapFx.Monday, GapFx.Analysis([ignored, open]), gap => gap.Id != ignored.Id);

        Assert.Equal(open.Id, grid.GapOf(1, GapFx.Day(0))?.GapId);
    }

    [Fact]
    public void Gaps_without_a_cell_mark_no_cell()
    {
        var day = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 0);

        var grid = DraftGrid.Build(Draft((1, 0, 1m)), Tasks(GapFx.Task(1, "A")), GapFx.Monday, GapFx.Analysis([day]));

        Assert.Null(grid.GapOf(1, GapFx.Day(0)));
    }
}
