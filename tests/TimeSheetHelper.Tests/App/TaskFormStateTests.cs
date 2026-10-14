using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

public sealed class TaskFormStateTests
{
    private static readonly ForecastProjectMatch Intranet = new(1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence", "RUNNING");
    private static readonly ForecastProjectMatch Portail = new(2000, "P2000", "Portail client", "Nexus", "RUNNING");
    private static readonly ForecastTaskMatch Build = new("T35647", "Build du tableau de bord", null, 1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence");
    private static readonly ForecastTaskMatch Deploy = new("T40000", "Déploiement", null, 2000, "P2000", "Portail client", null);

    private static ForecastAutoFillResult Found(ForecastProjectMatch? project, ForecastTaskMatch? task, params string[] warnings) =>
        new(project, task, null, warnings);

    // ----- number normalisation

    [Theory]
    [InlineData("1078", "P1078")]
    [InlineData("p1078", "P1078")]
    [InlineData(" P1078 ", "P1078")]
    [InlineData("P-225", "P-225")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("abc", "ABC")]
    public void Project_numbers_are_written_in_full(string? typed, string expected) =>
        Assert.Equal(expected, TaskFormState.NormalizeProjectNumber(typed));

    [Theory]
    [InlineData("35647", "T35647")]
    [InlineData("t35647", "T35647")]
    [InlineData("T35647", "T35647")]
    [InlineData(" ", "")]
    public void Task_numbers_are_written_in_full(string typed, string expected) =>
        Assert.Equal(expected, TaskFormState.NormalizeTaskNumber(typed));

    [Fact]
    public void Normalizing_the_codes_rewrites_both_fields()
    {
        var state = new TaskFormState { ProjectCode = "1078", TaskCode = "t35647" };

        state.NormalizeCodes();

        Assert.Equal("P1078", state.ProjectCode);
        Assert.Equal("T35647", state.TaskCode);
    }

    // ----- auto-fill: the rule

    [Fact]
    public void Auto_fill_fills_every_empty_field()
    {
        var state = new TaskFormState { ProjectCode = "1078", TaskCode = "35647" };

        var outcome = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.True(outcome.Changed);
        Assert.Empty(outcome.Notes);
        Assert.Equal("Intranet Centre de Marque", state.Project);
        Assert.Equal("P1078", state.ProjectCode);
        Assert.Equal("Build du tableau de bord", state.Task);
        Assert.Equal("T35647", state.TaskCode);
    }

    [Fact]
    public void Auto_fill_never_overwrites_a_name_typed_by_hand_and_says_so()
    {
        var state = new TaskFormState { Project = "Mon projet", Task = "Ma tâche", ProjectCode = "P1078", TaskCode = "T35647" };

        var outcome = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.False(outcome.Changed);
        Assert.Equal("Mon projet", state.Project);
        Assert.Equal("Ma tâche", state.Task);
        Assert.Equal(2, outcome.Notes.Count);
        Assert.Contains("Mon projet", outcome.Notes[0]);
        Assert.Contains("Intranet Centre de Marque", outcome.Notes[0]);
        Assert.Contains("Ma tâche", outcome.Notes[1]);
    }

    [Fact]
    public void Auto_fill_fills_only_the_empty_field_and_keeps_the_typed_one()
    {
        var state = new TaskFormState { Task = "Ma tâche", TaskCode = "T35647" };

        var outcome = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.True(outcome.Changed);
        Assert.Equal("Ma tâche", state.Task);
        Assert.Equal("Intranet Centre de Marque", state.Project);
        Assert.Equal("P1078", state.ProjectCode);
    }

    [Fact]
    public void Auto_fill_replaces_what_the_previous_auto_fill_put_there()
    {
        var state = new TaskFormState { ProjectCode = "P1078" };
        state.ApplyAutoFill(Found(Intranet, null));
        Assert.Equal("Intranet Centre de Marque", state.Project);

        state.ProjectCode = "P2000";
        var outcome = state.ApplyAutoFill(Found(Portail, null));

        Assert.True(outcome.Changed);
        Assert.Empty(outcome.Notes);
        Assert.Equal("Portail client", state.Project);
        Assert.Equal("P2000", state.ProjectCode);
    }

    [Fact]
    public void A_field_edited_after_an_auto_fill_is_no_longer_replaced()
    {
        var state = new TaskFormState { ProjectCode = "P1078" };
        state.ApplyAutoFill(Found(Intranet, null));
        state.Project = "Intranet (version courte)";

        state.ProjectCode = "P2000";
        var outcome = state.ApplyAutoFill(Found(Portail, null));

        Assert.Equal("Intranet (version courte)", state.Project);
        Assert.Contains(outcome.Notes, note => note.Contains("Intranet (version courte)"));
    }

    [Fact]
    public void A_number_typed_without_its_prefix_is_written_in_full_without_a_note()
    {
        var state = new TaskFormState { ProjectCode = "1078" };

        var outcome = state.ApplyAutoFill(Found(Intranet, null));

        Assert.Equal("P1078", state.ProjectCode);
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void A_project_typed_by_hand_is_kept_even_when_the_numbers_point_to_another_one()
    {
        var state = new TaskFormState { Project = "Mon projet", ProjectCode = "P5", TaskCode = "T35647" };

        var outcome = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.Equal("Mon projet", state.Project);
        Assert.Equal("P5", state.ProjectCode);
        Assert.Contains(outcome.Notes, note => note.Contains("Mon projet") && note.Contains("Intranet Centre de Marque"));
    }

    [Fact]
    public void Auto_fill_passes_the_forecast_warnings_on_and_fills_nothing_for_an_unknown_number()
    {
        var state = new TaskFormState { ProjectCode = "P9999" };

        var outcome = state.ApplyAutoFill(Found(null, null, "Le projet P9999 est introuvable dans Forecast."));

        Assert.False(outcome.Changed);
        Assert.Equal(["Le projet P9999 est introuvable dans Forecast."], outcome.Notes);
        Assert.Equal("", state.Project);
    }

    [Fact]
    public void A_task_alone_fills_its_project_too_and_scopes_the_task_search_to_it()
    {
        var state = new TaskFormState { TaskCode = "T35647" };

        state.ApplyAutoFill(Found(Intranet, Build));

        Assert.Equal("Intranet Centre de Marque", state.Project);
        Assert.Equal(1078, state.ProjectIdForSearch);
    }

    [Fact]
    public void Project_only_auto_fill_leaves_the_task_alone()
    {
        var state = new TaskFormState { ProjectCode = "P1078", Task = "Ma tâche" };

        state.ApplyAutoFill(Found(Intranet, null));

        Assert.Equal("Ma tâche", state.Task);
        Assert.Equal("", state.TaskCode);
    }

    [Fact]
    public void A_new_project_drops_the_task_that_forecast_had_filled_for_the_previous_one()
    {
        var state = new TaskFormState();
        state.ChooseTask(Build);

        state.ProjectCode = "P2000";
        state.ApplyAutoFill(Found(Portail, null));

        Assert.Equal("Portail client", state.Project);
        Assert.Equal("", state.Task);
        Assert.Equal("", state.TaskCode);
    }

    [Fact]
    public void A_new_project_keeps_a_task_the_user_typed()
    {
        var state = new TaskFormState { Task = "Ma tâche" };
        state.ApplyAutoFill(Found(Intranet, Build));
        state.Task = "Ma tâche à moi";

        state.ProjectCode = "P2000";
        state.ApplyAutoFill(Found(Portail, null));

        Assert.Equal("Ma tâche à moi", state.Task);
    }

    // ----- what is asked of Forecast

    [Fact]
    public void The_request_carries_both_numbers_in_full()
    {
        var state = new TaskFormState { ProjectCode = "1078", TaskCode = "35647" };

        var request = state.BuildAutoFillRequest(AutoFillTrigger.Submit);

        Assert.Equal(new AutoFillRequest("P1078", "T35647"), request);
        Assert.False(request.IsEmpty);
    }

    [Fact]
    public void Empty_numbers_give_an_empty_request()
    {
        var state = new TaskFormState { ProjectCode = " ", TaskCode = "" };

        Assert.True(state.BuildAutoFillRequest(AutoFillTrigger.Submit).IsEmpty);
    }

    [Fact]
    public void A_new_project_number_is_not_sent_with_the_task_number_forecast_filled_earlier()
    {
        var state = new TaskFormState();
        state.ChooseTask(Build);
        state.ProjectCode = "2000";

        var request = state.BuildAutoFillRequest(AutoFillTrigger.ProjectNumber);

        Assert.Equal(new AutoFillRequest("P2000", null), request);
    }

    [Fact]
    public void A_new_task_number_is_not_sent_with_the_project_number_forecast_filled_earlier()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);
        state.TaskCode = "40000";

        var request = state.BuildAutoFillRequest(AutoFillTrigger.TaskNumber);

        Assert.Equal(new AutoFillRequest(null, "T40000"), request);
    }

    [Fact]
    public void A_number_typed_by_hand_is_sent_along_with_the_one_just_edited()
    {
        var state = new TaskFormState { ProjectCode = "P1078", TaskCode = "T35647" };

        var request = state.BuildAutoFillRequest(AutoFillTrigger.TaskNumber);

        Assert.Equal(new AutoFillRequest("P1078", "T35647"), request);
    }

    [Fact]
    public void Two_requests_with_the_same_numbers_have_the_same_key()
    {
        Assert.Equal(new AutoFillRequest("P1078", null).Key, new AutoFillRequest("P1078", null).Key);
        Assert.NotEqual(new AutoFillRequest("P1078", null).Key, new AutoFillRequest(null, "P1078").Key);
    }

    // ----- picking a suggestion

    [Fact]
    public void Picking_a_project_sets_its_name_and_number_over_what_was_typed()
    {
        var state = new TaskFormState { Project = "intra", ProjectCode = "P5" };

        state.ChooseProject(Intranet);

        Assert.Equal("Intranet Centre de Marque", state.Project);
        Assert.Equal("P1078", state.ProjectCode);
        Assert.Equal(1078, state.ProjectIdForSearch);
    }

    [Fact]
    public void Picking_a_task_sets_its_name_number_and_project()
    {
        var state = new TaskFormState();

        var outcome = state.ChooseTask(Build);

        Assert.Empty(outcome.Notes);
        Assert.Equal("Build du tableau de bord", state.Task);
        Assert.Equal("T35647", state.TaskCode);
        Assert.Equal("Intranet Centre de Marque", state.Project);
        Assert.Equal("P1078", state.ProjectCode);
        Assert.Equal(1078, state.ProjectIdForSearch);
    }

    [Fact]
    public void Picking_a_task_keeps_a_project_typed_by_hand_and_says_so()
    {
        var state = new TaskFormState { Project = "Mon projet" };

        var outcome = state.ChooseTask(Build);

        Assert.Equal("Mon projet", state.Project);
        Assert.Equal("", state.ProjectCode);
        Assert.Null(state.ProjectIdForSearch);
        Assert.Contains(outcome.Notes, note => note.Contains("Mon projet") && note.Contains("T35647"));
    }

    [Fact]
    public void Picking_a_task_of_the_chosen_project_changes_nothing_for_the_project()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);

        var outcome = state.ChooseTask(Build);

        Assert.Empty(outcome.Notes);
        Assert.Equal("Intranet Centre de Marque", state.Project);
        Assert.Equal(1078, state.ProjectIdForSearch);
    }

    [Fact]
    public void Picking_another_project_drops_the_task_forecast_had_filled()
    {
        var state = new TaskFormState();
        state.ChooseTask(Build);

        state.ChooseProject(Portail);

        Assert.Equal("Portail client", state.Project);
        Assert.Equal("P2000", state.ProjectCode);
        Assert.Equal("", state.Task);
        Assert.Equal("", state.TaskCode);
    }

    [Fact]
    public void Picking_a_task_of_another_project_after_an_auto_fill_replaces_the_project_forecast_had_filled()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);

        state.ChooseTask(Deploy);

        Assert.Equal("Portail client", state.Project);
        Assert.Equal("P2000", state.ProjectCode);
        Assert.Equal(2000, state.ProjectIdForSearch);
    }

    [Fact]
    public void The_project_scope_is_lost_when_the_project_name_is_edited_by_hand()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);

        state.Project = "Autre chose";

        Assert.Null(state.ProjectIdForSearch);
    }

    [Fact]
    public void Picking_a_known_project_sets_only_its_name_and_drops_the_number_forecast_had_filled()
    {
        var state = new TaskFormState();
        state.ChooseTask(Build);

        state.ChooseKnownProject("Projet interne");

        Assert.Equal("Projet interne", state.Project);
        Assert.Equal("", state.ProjectCode);
        Assert.Equal("", state.Task);
        Assert.Null(state.ProjectIdForSearch);
    }

    [Fact]
    public void A_known_project_picked_by_the_user_is_not_replaced_by_a_later_auto_fill()
    {
        var state = new TaskFormState();
        state.ChooseKnownProject("Projet interne");

        state.TaskCode = "T35647";
        var outcome = state.ApplyAutoFill(Found(Intranet, Build));

        Assert.Equal("Projet interne", state.Project);
        Assert.Contains(outcome.Notes, note => note.Contains("Projet interne"));
    }

    // ----- editing a name detaches its number

    [Fact]
    public void Editing_an_auto_filled_project_name_drops_its_number_with_a_note()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);

        state.Project = "Intranet Centre";
        var note = state.OnProjectEdited();

        Assert.Equal("", state.ProjectCode);
        Assert.Contains("P1078", note);
    }

    [Fact]
    public void Editing_a_project_name_keeps_a_number_the_user_typed()
    {
        var state = new TaskFormState { ProjectCode = "P5" };

        state.Project = "Quelque chose";

        Assert.Null(state.OnProjectEdited());
        Assert.Equal("P5", state.ProjectCode);
    }

    [Fact]
    public void Leaving_the_auto_filled_name_as_it_is_keeps_its_number()
    {
        var state = new TaskFormState();
        state.ChooseProject(Intranet);

        Assert.Null(state.OnProjectEdited());
        Assert.Equal("P1078", state.ProjectCode);
    }

    [Fact]
    public void Editing_an_auto_filled_task_name_drops_its_number_with_a_note()
    {
        var state = new TaskFormState();
        state.ChooseTask(Build);

        state.Task = "Autre tâche";
        var note = state.OnTaskEdited();

        Assert.Equal("", state.TaskCode);
        Assert.Contains("T35647", note);
    }
}
