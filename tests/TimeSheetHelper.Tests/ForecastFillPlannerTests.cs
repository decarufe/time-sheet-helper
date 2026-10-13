using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastFillPlannerTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static readonly ForecastTask Dev = new() { Id = 1, ExternalId = "T100", ProjectName = "Acme", TaskName = "Développement" };
    private static readonly ForecastTask Meetings = new() { Id = 2, ExternalId = "T200", ProjectName = "Interne", TaskName = "Réunions" };

    private static readonly Dictionary<int, ForecastTask> Tasks = new() { [1] = Dev, [2] = Meetings };

    private static TimesheetEntry Entry(int taskId, int day, decimal hours) =>
        new() { ForecastTaskId = taskId, Date = Monday.AddDays(day), Hours = hours };

    [Fact]
    public void Plan_sums_entries_per_task_and_day_and_rounds()
    {
        var draft = new TimesheetDraft
        {
            WeekStart = Monday,
            Entries = [Entry(1, 0, 1.5m), Entry(1, 0, 0.6m), Entry(2, 0, 1m), Entry(1, 1, 3m)],
        };

        var plan = ForecastFillPlanner.Plan(draft, Tasks);

        Assert.Equal(3, plan.Count);
        Assert.Equal(2m, plan.Single(c => c.Task == Dev && c.Date == Monday).Hours);
        Assert.Equal(3m, plan.Single(c => c.Task == Dev && c.Date == Monday.AddDays(1)).Hours);
        Assert.Equal(1m, plan.Single(c => c.Task == Meetings).Hours);
    }

    [Fact]
    public void Plan_drops_zero_cells_and_reports_out_of_week_or_unknown_tasks()
    {
        var draft = new TimesheetDraft
        {
            WeekStart = Monday,
            Entries = [Entry(1, 0, 0.1m), Entry(1, 7, 2m), Entry(99, 2, 1m), Entry(2, 6, 1m)],
        };
        var warnings = new List<string>();

        var plan = ForecastFillPlanner.Plan(draft, Tasks, warnings);

        var cell = Assert.Single(plan);
        Assert.Equal(Monday.AddDays(6), cell.Date);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Plan_joins_distinct_notes_of_a_cell()
    {
        var first = Entry(1, 0, 1m);
        first.Notes = "Revue de PR";
        var second = Entry(1, 0, 1m);
        second.Notes = "revue de pr";
        var third = Entry(1, 0, 1m);
        third.Notes = "Correctif";
        var draft = new TimesheetDraft { WeekStart = Monday, Entries = [first, second, third, Entry(2, 0, 1m)] };

        var plan = ForecastFillPlanner.Plan(draft, Tasks);

        Assert.Equal("Revue de PR ; Correctif", plan.Single(c => c.Task == Dev).Notes);
        Assert.Null(plan.Single(c => c.Task == Meetings).Notes);
    }

    [Theory]
    [InlineData(1.5, 1.5, ForecastCellStatus.Ok)]
    [InlineData(1.5, 1.505, ForecastCellStatus.Ok)]
    [InlineData(1.5, 1.0, ForecastCellStatus.Mismatch)]
    [InlineData(1.5, 0.0, ForecastCellStatus.Mismatch)]
    [InlineData(0, 0, ForecastCellStatus.Ok)]
    [InlineData(0, 2, ForecastCellStatus.Mismatch)]
    public void Compare_checks_hours_within_tolerance(double expected, double actual, ForecastCellStatus status)
    {
        var result = ForecastFillPlanner.Compare((decimal)expected, (decimal)actual);
        Assert.Equal(status, result.Status);
        Assert.Equal((decimal)actual, result.Actual);
        Assert.Equal(status == ForecastCellStatus.Ok, result.Message is null);
    }

    [Fact]
    public void Report_success_requires_all_cells_ok()
    {
        var report = new ForecastFillReport { WeekStart = Monday, Mode = ForecastSubmitMode.Fill };
        Assert.False(report.Success);

        report.Cells.Add(new ForecastCellResult(1, "Acme / Dev", Monday, 1m, 1m, ForecastCellStatus.Ok));
        Assert.True(report.Success);

        report.Cells.Add(new ForecastCellResult(1, "Acme / Dev", Monday.AddDays(1), 1m, 0m, ForecastCellStatus.Mismatch));
        Assert.False(report.Success);
        Assert.Contains("1/2", report.Summary);
    }

    [Theory]
    [InlineData(0, ForecastCellAction.Type)]
    [InlineData(1.5, ForecastCellAction.AlreadyDone)]
    [InlineData(1.499, ForecastCellAction.AlreadyDone)]
    [InlineData(2, ForecastCellAction.Conflict)]
    [InlineData(0.25, ForecastCellAction.Conflict)]
    public void Only_empty_cells_are_added_to(double current, ForecastCellAction expected) =>
        Assert.Equal(expected, ForecastFillPlanner.Decide(1.5m, (decimal)current).Action);
}

