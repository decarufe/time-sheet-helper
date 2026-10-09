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

    [Theory]
    [InlineData(1.5, "1h 30m", ForecastCellStatus.Ok)]
    [InlineData(1.5, "1,5", ForecastCellStatus.Ok)]
    [InlineData(1.5, "1.5", ForecastCellStatus.Ok)]
    [InlineData(1.5, "1h", ForecastCellStatus.Mismatch)]
    [InlineData(1.5, "", ForecastCellStatus.Mismatch)]
    [InlineData(0, "", ForecastCellStatus.Ok)]
    [InlineData(0, "2h", ForecastCellStatus.Mismatch)]
    [InlineData(1, "n/a", ForecastCellStatus.Mismatch)]
    public void Compare_parses_grid_text(double expected, string actual, ForecastCellStatus status)
    {
        var result = ForecastFillPlanner.Compare((decimal)expected, actual);
        Assert.Equal(status, result.Status);
        Assert.Equal(status == ForecastCellStatus.Ok, result.Message is null);
    }

    [Fact]
    public void Report_success_requires_all_cells_ok()
    {
        var report = new ForecastFillReport { WeekStart = Monday, Mode = ForecastSubmitMode.Fill };
        Assert.False(report.Success);

        report.Cells.Add(new ForecastCellResult(1, "Acme / Dev", Monday, 1m, 1m, "1h", ForecastCellStatus.Ok));
        Assert.True(report.Success);

        report.Cells.Add(new ForecastCellResult(1, "Acme / Dev", Monday.AddDays(1), 1m, 0m, "", ForecastCellStatus.Mismatch));
        Assert.False(report.Success);
        Assert.Contains("1/2", report.Summary);
    }
}
