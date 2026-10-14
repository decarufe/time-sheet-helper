using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

public sealed class ForecastSuggestionsTests
{
    private static readonly ForecastProjectMatch Intranet = new(1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence", "RUNNING");
    private static readonly ForecastTaskMatch Build = new("T35647", "Build du tableau de bord", null, 1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence");

    private static ForecastLookupResult<ForecastProjectMatch> Result(params ForecastProjectMatch[] projects) => new(projects, projects.Length);

    [Theory]
    [InlineData("RUNNING", "En cours")]
    [InlineData("running", "En cours")]
    [InlineData("PLANNING", "Planification")]
    [InlineData("HALTED", "En pause")]
    [InlineData("DONE", "Terminé")]
    [InlineData("OPPORTUNITY", "Opportunité")]
    [InlineData("AUTRE", "AUTRE")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Project_statuses_are_shown_in_french(string? status, string expected) =>
        Assert.Equal(expected, ForecastSuggestions.StatusLabel(status));

    [Fact]
    public void A_project_line_under_a_name_field_leads_with_the_name()
    {
        var option = ForecastSuggestions.ProjectOption(Intranet);

        Assert.Equal("Intranet Centre de Marque", option.Primary);
        Assert.Equal("P1078 · Équipe Laurence · En cours", option.Secondary);
        Assert.Same(Intranet, option.Tag);
    }

    [Fact]
    public void A_project_line_under_a_number_field_leads_with_the_number()
    {
        var option = ForecastSuggestions.ProjectOption(Intranet, numberFirst: true);

        Assert.Equal("P1078 · Intranet Centre de Marque", option.Primary);
        Assert.Equal("Équipe Laurence · En cours", option.Secondary);
    }

    [Fact]
    public void A_project_line_skips_what_forecast_did_not_give()
    {
        var option = ForecastSuggestions.ProjectOption(new ForecastProjectMatch(7, "P7", "Interne", null, null));

        Assert.Equal("P7", option.Secondary);
    }

    [Fact]
    public void A_task_line_names_its_project()
    {
        var byName = ForecastSuggestions.TaskOption(Build);
        var byNumber = ForecastSuggestions.TaskOption(Build, numberFirst: true);

        Assert.Equal("Build du tableau de bord", byName.Primary);
        Assert.Equal("T35647 · P1078 Intranet Centre de Marque · Équipe Laurence", byName.Secondary);
        Assert.Equal("T35647 · Build du tableau de bord", byNumber.Primary);
        Assert.Equal("P1078 Intranet Centre de Marque · Équipe Laurence", byNumber.Secondary);
        Assert.Same(Build, byName.Tag);
    }

    [Fact]
    public void A_task_without_a_project_has_a_shorter_line()
    {
        var option = ForecastSuggestions.TaskOption(new ForecastTaskMatch("T1", "Réunion", null, null, null, null, null));

        Assert.Equal("T1", option.Secondary);
    }

    [Fact]
    public void Known_projects_that_match_come_first_and_carry_their_name()
    {
        var batch = ForecastSuggestions.ProjectBatch(Result(Intranet), ["Projet interne", "Autre", "intranet legacy"], "intra", numberFirst: false);

        Assert.Equal(["intranet legacy", "Intranet Centre de Marque"], batch.Options.Select(o => o.Primary));
        Assert.Equal("intranet legacy", batch.Options[0].Tag);
        Assert.Equal("Projet déjà enregistré", batch.Options[0].Secondary);
    }

    [Fact]
    public void A_known_project_that_forecast_also_returned_is_listed_once_with_its_number()
    {
        var batch = ForecastSuggestions.ProjectBatch(Result(Intranet), ["Intranet Centre de Marque"], "intranet", numberFirst: false);

        var option = Assert.Single(batch.Options);
        Assert.Same(Intranet, option.Tag);
    }

    [Fact]
    public void Known_projects_are_matched_without_regard_to_case_or_accents_and_the_list_is_capped()
    {
        var known = new[] { "Équipe A", "Équipe B", "Équipe C", "Équipe D", "Zéro" };

        var batch = ForecastSuggestions.ProjectBatch(Result(), known, "EQUIPE", numberFirst: false);

        Assert.Equal(ForecastSuggestions.MaxKnownProjects, batch.Options.Count);
        Assert.All(batch.Options, option => Assert.StartsWith("Équipe", option.Primary));
    }

    [Fact]
    public void Known_projects_starting_with_the_text_come_before_the_ones_that_merely_contain_it()
    {
        var batch = ForecastSuggestions.ProjectBatch(Result(), ["Ancien portail", "Portail client"], "portail", numberFirst: false);

        Assert.Equal(["Portail client", "Ancien portail"], batch.Options.Select(o => o.Primary));
    }

    [Fact]
    public void A_number_search_lists_forecast_projects_only()
    {
        var batch = ForecastSuggestions.ProjectBatch(Result(Intranet), ["Projet 1078 interne"], "1078", numberFirst: true);

        var option = Assert.Single(batch.Options);
        Assert.Equal("P1078 · Intranet Centre de Marque", option.Primary);
    }

    [Fact]
    public void The_batch_keeps_the_forecast_error_and_the_more_results_flag()
    {
        var failed = ForecastSuggestions.ProjectBatch(ForecastLookupResult<ForecastProjectMatch>.Failure("Connexion requise."), ["Intranet interne"], "intra", numberFirst: false);
        var truncated = ForecastSuggestions.ProjectBatch(new ForecastLookupResult<ForecastProjectMatch>([Intranet], 25), [], "intra", numberFirst: false);

        Assert.Equal("Connexion requise.", failed.Error);
        Assert.Single(failed.Options);
        Assert.True(truncated.HasMore);
        Assert.Null(truncated.Error);
    }

    [Fact]
    public void A_task_batch_keeps_the_order_forecast_gave()
    {
        var other = new ForecastTaskMatch("T2", "Autre", null, 1078, "P1078", "Intranet Centre de Marque", null);

        var batch = ForecastSuggestions.TaskBatch(new ForecastLookupResult<ForecastTaskMatch>([Build, other], 2), numberFirst: false);

        Assert.Equal(["Build du tableau de bord", "Autre"], batch.Options.Select(o => o.Primary));
    }

    [Fact]
    public async Task The_status_line_reads_out_what_the_list_holds()
    {
        var lookup = new FakeForecastLookup();
        lookup.Projects.Add(Intranet);
        lookup.Projects.Add(new ForecastProjectMatch(1079, "P1079", "Intranet bis", null, null));
        using var search = new SuggestionSearch(
            async (text, token) => ForecastSuggestions.ProjectBatch(await lookup.SearchProjectsAsync(text, token), [], text, false),
            ForecastLookup.MinQueryLength, TimeSpan.Zero, (_, _) => Task.CompletedTask);

        Assert.Equal("", ForecastSuggestions.StatusText(search));

        await search.InputAsync("intranet");
        Assert.Equal("2 suggestions.", ForecastSuggestions.StatusText(search));

        await search.InputAsync("zzz");
        Assert.Equal("Aucun résultat dans Forecast.", ForecastSuggestions.StatusText(search));

        lookup.Error = "Connexion à Forecast requise.";
        await search.InputAsync("intranet");
        Assert.Equal("Connexion à Forecast requise.", ForecastSuggestions.StatusText(search));
    }

    [Fact]
    public async Task The_task_search_is_scoped_to_the_project_of_the_form_once_one_is_chosen()
    {
        var lookup = new FakeForecastLookup();
        lookup.Tasks.Add(Build);
        lookup.Tasks.Add(new ForecastTaskMatch("T40000", "Build du portail", null, 2000, "P2000", "Portail client", null));
        var state = new TaskFormState();

        var before = await lookup.SearchTasksAsync("build", state.ProjectIdForSearch);
        state.ChooseProject(Intranet);
        var after = await lookup.SearchTasksAsync("build", state.ProjectIdForSearch);

        Assert.Equal(2, before.Items.Count);
        Assert.Equal("T35647", Assert.Single(after.Items).Code);
        Assert.Null(lookup.TaskQueries[0].ProjectId);
        Assert.Equal(1078, lookup.TaskQueries[1].ProjectId);
    }
}
