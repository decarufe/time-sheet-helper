using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

/// <summary>Edge cases of <see cref="ForecastSuggestions"/> that <see cref="ForecastSuggestionsTests"/> does not pin down: the text read out under the list, blank text, accents.</summary>
public sealed class ForecastSuggestionsGadgetTests
{
    private static readonly ForecastProjectMatch Intranet = new(1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence", "RUNNING");
    private static readonly ForecastTaskMatch Build = new("T35647", "Build du tableau de bord", null, 1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence");

    private static async Task<SuggestionSearch> After(SuggestionBatch batch)
    {
        var search = new SuggestionSearch((_, _) => Task.FromResult(batch), ForecastLookup.MinQueryLength, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        await search.InputAsync("intra");
        return search;
    }

    [Theory]
    [InlineData(1, false, null, "1 suggestion.")]
    [InlineData(2, false, null, "2 suggestions.")]
    [InlineData(1, true, null, "1 suggestion — plus de résultats : précisez votre saisie.")]
    [InlineData(3, true, "Connexion à Forecast requise.", "3 suggestions — plus de résultats : précisez votre saisie. Connexion à Forecast requise.")]
    [InlineData(1, false, "Forecast est injoignable.", "1 suggestion. Forecast est injoignable.")]
    public async Task The_status_line_counts_the_lines_and_adds_what_is_missing_or_wrong(int count, bool hasMore, string? error, string expected)
    {
        var options = Enumerable.Range(1, count).Select(i => new SuggestionOption($"Ligne {i}")).ToList();
        using var search = await After(new SuggestionBatch(options, hasMore, error));

        Assert.Equal(expected, ForecastSuggestions.StatusText(search));
    }

    [Fact]
    public async Task The_status_line_says_forecast_is_being_asked_while_a_search_runs()
    {
        var gate = new TaskCompletionSource<SuggestionBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var search = new SuggestionSearch((_, _) => gate.Task, ForecastLookup.MinQueryLength, TimeSpan.Zero, (_, _) => Task.CompletedTask);

        var running = search.InputAsync("intra");

        Assert.Equal("Recherche dans Forecast…", ForecastSuggestions.StatusText(search));
        gate.SetResult(SuggestionBatch.Empty);
        await running;
    }

    [Fact]
    public async Task The_status_line_gives_a_generic_message_when_the_search_itself_failed()
    {
        using var search = new SuggestionSearch((_, _) => throw new InvalidOperationException("boom"), ForecastLookup.MinQueryLength, TimeSpan.Zero, (_, _) => Task.CompletedTask);

        await search.InputAsync("intra");

        Assert.Equal("La recherche dans Forecast a échoué.", ForecastSuggestions.StatusText(search));
    }

    [Fact]
    public void Blank_text_lists_no_known_project_instead_of_all_of_them()
    {
        var result = new ForecastLookupResult<ForecastProjectMatch>([Intranet], 1);

        var batch = ForecastSuggestions.ProjectBatch(result, ["Projet interne", "Autre"], "   ", numberFirst: false);

        Assert.Equal(["Intranet Centre de Marque"], batch.Options.Select(o => o.Primary));
    }

    [Fact]
    public void A_known_project_is_not_repeated_when_forecast_writes_its_name_with_other_accents_or_case()
    {
        var result = new ForecastLookupResult<ForecastProjectMatch>([Intranet], 1);

        var batch = ForecastSuggestions.ProjectBatch(result, ["INTRANET CENTRE DE MARQUE", "Intranet Centre de Marqué"], "intranet", numberFirst: false);

        Assert.Same(Intranet, Assert.Single(batch.Options).Tag);
    }

    [Fact]
    public void A_task_batch_keeps_the_forecast_error_and_the_more_results_flag()
    {
        var truncated = ForecastSuggestions.TaskBatch(new ForecastLookupResult<ForecastTaskMatch>([Build], 12), numberFirst: false);
        var failed = ForecastSuggestions.TaskBatch(ForecastLookupResult<ForecastTaskMatch>.Failure("Connexion requise."), numberFirst: true);

        Assert.True(truncated.HasMore);
        Assert.Null(truncated.Error);
        Assert.Empty(failed.Options);
        Assert.Equal("Connexion requise.", failed.Error);
    }
}
