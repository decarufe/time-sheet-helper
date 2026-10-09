using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

/// <summary>Based on the real "My Timesheets" capture of 2026-10-09 (English UI, week starting on Sunday).</summary>
public class ForecastTimesheetDomTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static readonly string[] Headers =
        ["Sunday 04 Oct", "Monday 05 Oct", "Tuesday 06 Oct", "Wednesday 07 Oct", "Thursday 08 Oct", "Friday 09 Oct", "Saturday 10 Oct"];

    private static readonly string[] NoHours = ["", "", "", "", "", "", ""];

    [Fact]
    public void Row_codes_come_from_title_attributes()
    {
        var row = ForecastTimesheetDom.ParseRow(0, "T3382 - Rencontre Bimensuelle Nexus", "Rencontre Bimensuelle Nexus",
            "P225 - Nexus - Interne", "Nexus - Interne", "Global", NoHours);

        Assert.Equal("T3382", row.TaskCode);
        Assert.Equal("Rencontre Bimensuelle Nexus", row.TaskName);
        Assert.Equal("P225", row.ProjectCode);
        Assert.Equal("Nexus - Interne", row.ProjectName);
        Assert.Equal("Global", row.Phase);
    }

    [Fact]
    public void Row_without_code_keeps_the_title_as_label()
    {
        var row = ForecastTimesheetDom.ParseRow(3, "Formation", "", null, "", "", NoHours);

        Assert.Null(row.TaskCode);
        Assert.Equal("Formation", row.TaskName);
        Assert.Equal("(sans projet)", row.ProjectName);
        Assert.Null(row.Phase);
    }

    [Fact]
    public void Headers_starting_on_sunday_map_to_dates()
    {
        var dates = ForecastTimesheetDom.HeaderDates(Headers, Monday);

        Assert.Equal(new DateOnly(2026, 10, 4), dates[0]);
        Assert.Equal(Monday, dates[1]);
        Assert.Equal(new DateOnly(2026, 10, 10), dates[6]);
        Assert.True(ForecastTimesheetDom.IsShowingWeek(Headers, Monday));
        Assert.False(ForecastTimesheetDom.IsShowingWeek(Headers, Monday.AddDays(7)));
    }

    [Fact]
    public void Headers_across_new_year_pick_the_closest_year()
    {
        string[] headers = ["Monday 29 Dec", "Tuesday 30 Dec", "Wednesday 31 Dec", "Thursday 01 Jan", "Friday 02 Jan"];

        var dates = ForecastTimesheetDom.HeaderDates(headers, new DateOnly(2025, 12, 29));

        Assert.Equal(new DateOnly(2025, 12, 31), dates[2]);
        Assert.Equal(new DateOnly(2026, 1, 1), dates[3]);
    }

    [Theory]
    [InlineData("lundi 06 juil.", 7)]
    [InlineData("mardi 02 juin", 6)]
    [InlineData("jeudi 13 août", 8)]
    [InlineData("vendredi 06 févr.", 2)]
    public void French_month_names_are_understood(string header, int month) =>
        Assert.Equal(month, ForecastTimesheetDom.HeaderDates([header], new DateOnly(2026, month, 1))[0]?.Month);

    [Fact]
    public void Unreadable_header_maps_to_null() =>
        Assert.Null(ForecastTimesheetDom.HeaderDates(["Notes"], Monday)[0]);

    [Fact]
    public void Row_is_found_by_task_code_then_by_names()
    {
        ForecastGridRow[] rows =
        [
            ForecastTimesheetDom.ParseRow(0, "T3382 - Rencontre", "Rencontre", "P225 - Nexus - Interne", "Nexus - Interne", "", NoHours),
            ForecastTimesheetDom.ParseRow(1, "T34809 - Communauté IA", "Communauté IA", "P740 - Génie Logiciel", "Génie Logiciel", "", NoHours),
        ];

        Assert.Equal(1, ForecastTimesheetDom.FindRow(rows, Task("T34809", "Autre projet", "Autre nom"))?.Index);
        Assert.Equal(1, ForecastTimesheetDom.FindRow(rows, Task("Génie Logiciel/Communauté IA", "Genie logiciel", "communaute ia"))?.Index);
        Assert.Null(ForecastTimesheetDom.FindRow(rows, Task("T999", "Nexus - Interne", "Autre")));
    }

    [Fact]
    public void Same_task_name_in_two_projects_without_code_is_ambiguous()
    {
        ForecastGridRow[] rows =
        [
            ForecastTimesheetDom.ParseRow(0, "Développement", "Développement", "Acme", "Acme", "", NoHours),
            ForecastTimesheetDom.ParseRow(1, "Développement", "Développement", "Acme", "Acme", "", NoHours),
        ];

        Assert.Null(ForecastTimesheetDom.FindRow(rows, Task("Acme/Développement", "Acme", "Développement")));
    }

    private static ForecastTask Task(string externalId, string project, string name) =>
        new() { ExternalId = externalId, ProjectName = project, TaskName = name };
}
