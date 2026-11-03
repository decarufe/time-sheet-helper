using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Forecast;

namespace TimeSheetHelper.Tests.App;

public sealed class TaskTooltipsTests
{
    [Fact]
    public void Full_details_give_a_task_line_and_a_project_line()
    {
        var text = TaskTooltips.For("T35647", "Full task name", "P1078", "Project name", "Client");

        Assert.Equal("T35647 · Full task name\nP1078 · Project name · Client", text);
    }

    [Fact]
    public void Missing_parts_are_left_out()
    {
        Assert.Equal("Full task name\nProject name", TaskTooltips.For(null, "Full task name", " ", "Project name", null));
        Assert.Equal("T1 · Task", TaskTooltips.For("T1", "Task", null, null, null));
        Assert.Equal("P2 · Project", TaskTooltips.For(null, null, "P2", "Project", null));
    }

    [Fact]
    public void Nothing_known_gives_no_tooltip()
    {
        Assert.Null(TaskTooltips.For(null, " ", null, "", null));
        Assert.Null(TaskTooltips.For((ForecastTask?)null));
    }

    [Fact]
    public void A_task_uses_its_code_only_when_the_external_id_is_one()
    {
        var coded = new ForecastTask { ExternalId = "T35647", TaskName = "Build", ProjectName = "Intranet", ProjectCode = "P1078", ClientName = "Acme" };
        var fallback = new ForecastTask { ExternalId = "Intranet/Build", TaskName = "Build", ProjectName = "Intranet" };

        Assert.Equal("T35647 · Build\nP1078 · Intranet · Acme", TaskTooltips.For(coded));
        Assert.Equal("Build\nIntranet", TaskTooltips.For(fallback));
    }

    [Fact]
    public void The_ignored_task_has_a_short_text()
    {
        var ignored = new ForecastTask { ExternalId = ForecastTask.IgnoredExternalId, TaskName = "Ignoré", ProjectName = "Ignoré" };

        Assert.Equal(TaskTooltips.IgnoredText, TaskTooltips.For(ignored));
    }
}
