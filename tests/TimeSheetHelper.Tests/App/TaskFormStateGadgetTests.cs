using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

/// <summary>Edge cases of the "nothing typed is overwritten" rule of <see cref="TaskFormState"/> that <see cref="TaskFormStateTests"/> does not pin down.</summary>
public sealed class TaskFormStateGadgetTests
{
    private static readonly ForecastProjectMatch Intranet = new(1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence", "RUNNING");
    private static readonly ForecastTaskMatch Build = new("T35647", "Build du tableau de bord", null, 1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence");

    private static ForecastAutoFillResult Found(ForecastProjectMatch? project, ForecastTaskMatch? task) => new(project, task, null, []);

    [Fact]
    public void A_project_number_typed_by_hand_is_kept_when_the_task_found_belongs_to_another_project_and_the_user_is_told()
    {
        var state = new TaskFormState { ProjectCode = "P5", TaskCode = "T35647" };

        var outcome = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.Equal("P5", state.ProjectCode);
        Assert.Contains(outcome.Notes, note => note.Contains("P5") && note.Contains("P1078"));
    }

    [Fact]
    public void Applying_the_same_answer_twice_changes_nothing_and_says_nothing_the_second_time()
    {
        var state = new TaskFormState { ProjectCode = "1078", TaskCode = "35647" };
        state.ApplyAutoFill(Found(Intranet, Build));

        var again = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.False(again.Changed);
        Assert.Empty(again.Notes);
        Assert.Equal("Intranet Centre de Marque", state.Project);
    }

    [Fact]
    public void A_name_typed_in_another_case_is_neither_replaced_nor_reported_as_a_conflict()
    {
        var state = new TaskFormState { Project = "intranet centre de marque", ProjectCode = "P1078" };

        var outcome = state.ApplyAutoFill(Found(Intranet, null));

        Assert.Equal("intranet centre de marque", state.Project);
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void A_project_typed_by_hand_is_not_tied_to_the_forecast_project_so_the_task_search_stays_unscoped()
    {
        var state = new TaskFormState { Project = "Mon projet", ProjectCode = "P1078" };

        state.ApplyAutoFill(Found(Intranet, null));

        Assert.Null(state.ProjectIdForSearch);
    }

    [Fact]
    public void Putting_the_auto_filled_name_back_restores_the_search_scope()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);

        state.Project = "autre";
        Assert.Null(state.ProjectIdForSearch);
        state.Project = "Intranet Centre de Marque";

        Assert.Equal(1078, state.ProjectIdForSearch);
    }

    [Fact]
    public void Picking_a_task_without_project_leaves_the_project_fields_alone()
    {
        var state = new TaskFormState { Project = "Mon projet", ProjectCode = "P5" };

        var outcome = state.ChooseTask(new ForecastTaskMatch("T200", "Réunions", null, null, null, null, null));

        Assert.Equal("Réunions", state.Task);
        Assert.Equal("Mon projet", state.Project);
        Assert.Equal("P5", state.ProjectCode);
        Assert.Null(state.ProjectIdForSearch);
        Assert.Empty(outcome.Notes);
    }
}
