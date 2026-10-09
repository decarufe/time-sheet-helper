using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastWeekGridTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public void MapHeaders_ignores_non_day_columns()
    {
        string[] headers = ["Tâche", "lun. 5", "mar. 6", "mer. 7", "jeu. 8", "ven. 9", "sam. 10", "dim. 11", "Total"];

        var map = ForecastWeekGrid.MapHeaders(headers, Monday);

        Assert.Null(map[0]);
        Assert.Equal(Monday, map[1]);
        Assert.Equal(Monday.AddDays(6), map[7]);
        Assert.Null(map[8]);
        Assert.True(ForecastWeekGrid.IsShowingWeek(headers, Monday));
        Assert.False(ForecastWeekGrid.IsShowingWeek(headers, Monday.AddDays(7)));
    }

    [Fact]
    public void MapHeaders_handles_month_day_format_sequentially()
    {
        string[] headers = ["10/5", "10/6", "10/7", "10/8", "10/9"];

        var days = ForecastWeekGrid.DayColumns(headers, Monday);

        Assert.Equal(Enumerable.Range(0, 5).Select(Monday.AddDays), days);
    }

    [Fact]
    public void Sunday_first_week_still_matches_monday_draft()
    {
        string[] headers = ["Sun 4", "Mon 5", "Tue 6", "Wed 7", "Thu 8", "Fri 9", "Sat 10"];

        var days = ForecastWeekGrid.DayColumns(headers, Monday);

        Assert.Equal(6, days.Count);
        Assert.Equal(Monday, days[0]);
    }

    private static ForecastTask Task(string externalId, string project, string task) =>
        new() { ExternalId = externalId, ProjectName = project, TaskName = task };

    [Fact]
    public void FindRow_prefers_code_then_project_and_task()
    {
        string[] rows = ["Tâche lun. 5 mar. 6", "P-12 Acme Développement 1h", "P-13 Globex Développement", "T555 Support"];

        Assert.Equal(1, ForecastWeekGrid.FindRow(rows, Task("Acme/Développement", "Acme", "Developpement")));
        Assert.Equal(3, ForecastWeekGrid.FindRow(rows, Task("T555", "X", "Autre nom")));
    }

    [Fact]
    public void FindRow_returns_null_when_ambiguous_or_missing()
    {
        string[] rows = ["Acme Développement", "Globex Développement"];

        Assert.Null(ForecastWeekGrid.FindRow(rows, Task("x", "Initech", "Développement")));
        Assert.Null(ForecastWeekGrid.FindRow(rows, Task("y", "Acme", "Tests")));
    }
}
