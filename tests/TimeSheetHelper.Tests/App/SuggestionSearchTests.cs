using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

public sealed class SuggestionSearchTests
{
    private static readonly SuggestionOption Alpha = new("Alpha");
    private static readonly SuggestionOption Beta = new("Beta");

    private static SuggestionBatch Batch(params SuggestionOption[] options) => new(options);

    /// <summary>A debounce delay the test releases by hand; it ends as canceled when the search it belongs to is superseded.</summary>
    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource> _pending = [];

        public int Calls => _pending.Count;

        public Task Wait(TimeSpan span, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => gate.TrySetCanceled(token));
            _pending.Add(gate);
            return gate.Task;
        }

        public void Release(int index) => _pending[index].TrySetResult();
    }

    /// <summary>A search the test answers by hand, one call at a time.</summary>
    private sealed class ManualSearch
    {
        private readonly List<TaskCompletionSource<SuggestionBatch>> _pending = [];

        public List<string> Queries { get; } = [];

        public Task<SuggestionBatch> Run(string query, CancellationToken token)
        {
            Queries.Add(query);
            var gate = new TaskCompletionSource<SuggestionBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(gate);
            return gate.Task;
        }

        public void Answer(int index, SuggestionBatch batch) => _pending[index].SetResult(batch);
    }

    private static SuggestionSearch Create(Func<string, CancellationToken, Task<SuggestionBatch>> search, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(search, ForecastLookup.MinQueryLength, TimeSpan.FromMilliseconds(300), delay ?? ((_, _) => Task.CompletedTask));

    /// <summary>Waits for something that happens on another continuation (xUnit runs the test on its own synchronization context).</summary>
    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition(), "The expected call never happened.");
    }

    [Fact]
    public async Task Text_shorter_than_the_minimum_stays_closed_and_does_not_search()
    {
        var search = new ManualSearch();
        using var suggestions = Create(search.Run);

        await suggestions.InputAsync("a");
        await suggestions.InputAsync("  b ");
        await suggestions.InputAsync(null);

        Assert.Equal(SuggestionStatus.Closed, suggestions.Status);
        Assert.False(suggestions.IsOpen);
        Assert.Empty(search.Queries);
    }

    [Fact]
    public async Task The_search_waits_for_the_debounce_delay_and_shows_loading_meanwhile()
    {
        var delay = new ManualDelay();
        var search = new ManualSearch();
        using var suggestions = Create(search.Run, delay.Wait);

        var running = suggestions.InputAsync("intra");

        Assert.Equal(SuggestionStatus.Loading, suggestions.Status);
        Assert.True(suggestions.IsOpen);
        Assert.Empty(search.Queries);

        delay.Release(0);
        await Until(() => search.Queries.Count == 1);
        search.Answer(0, Batch(Alpha));
        await running;

        Assert.Equal(["intra"], search.Queries);
        Assert.Equal(SuggestionStatus.Results, suggestions.Status);
        Assert.Equal([Alpha], suggestions.Options);
    }

    [Fact]
    public async Task Typing_again_before_the_delay_ends_cancels_the_first_search_so_only_the_last_text_is_searched()
    {
        var delay = new ManualDelay();
        var search = new ManualSearch();
        using var suggestions = Create(search.Run, delay.Wait);

        var first = suggestions.InputAsync("in");
        var second = suggestions.InputAsync("int");
        var third = suggestions.InputAsync("intr");
        delay.Release(2);
        await Task.WhenAll(first, second);
        await Until(() => search.Queries.Count == 1);
        search.Answer(0, Batch(Beta));
        await third;

        Assert.Equal(["intr"], search.Queries);
        Assert.Equal([Beta], suggestions.Options);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_a_newer_search_is_ignored()
    {
        var search = new ManualSearch();
        using var suggestions = Create(search.Run);

        var slow = suggestions.InputAsync("in");
        var fast = suggestions.InputAsync("int");
        search.Answer(1, Batch(Beta));
        await fast;
        search.Answer(0, Batch(Alpha));
        await slow;

        Assert.Equal([Beta], suggestions.Options);
        Assert.Equal(SuggestionStatus.Results, suggestions.Status);
    }

    [Fact]
    public async Task Clearing_the_text_while_a_search_runs_drops_its_answer()
    {
        var search = new ManualSearch();
        using var suggestions = Create(search.Run);

        var running = suggestions.InputAsync("intra");
        await suggestions.InputAsync("");
        search.Answer(0, Batch(Alpha));
        await running;

        Assert.Equal(SuggestionStatus.Closed, suggestions.Status);
        Assert.Empty(suggestions.Options);
    }

    [Fact]
    public async Task Closing_the_list_drops_the_answer_of_a_running_search()
    {
        var search = new ManualSearch();
        using var suggestions = Create(search.Run);

        var running = suggestions.InputAsync("intra");
        suggestions.Close();
        search.Answer(0, Batch(Alpha));
        await running;

        Assert.False(suggestions.IsOpen);
    }

    [Fact]
    public async Task No_match_gives_the_empty_state()
    {
        using var suggestions = Create((_, _) => Task.FromResult(SuggestionBatch.Empty));

        await suggestions.InputAsync("zzz");

        Assert.Equal(SuggestionStatus.Empty, suggestions.Status);
        Assert.True(suggestions.IsOpen);
        Assert.Empty(suggestions.Options);
    }

    [Fact]
    public async Task A_forecast_error_without_results_gives_the_error_state_with_its_message()
    {
        using var suggestions = Create((_, _) => Task.FromResult(new SuggestionBatch([], false, "Connexion à Forecast requise.")));

        await suggestions.InputAsync("intra");

        Assert.Equal(SuggestionStatus.Error, suggestions.Status);
        Assert.Equal("Connexion à Forecast requise.", suggestions.Error);
    }

    [Fact]
    public async Task Local_results_are_kept_next_to_a_forecast_error()
    {
        using var suggestions = Create((_, _) => Task.FromResult(new SuggestionBatch([Alpha], false, "Connexion à Forecast requise.")));

        await suggestions.InputAsync("intra");

        Assert.Equal(SuggestionStatus.Results, suggestions.Status);
        Assert.Equal([Alpha], suggestions.Options);
        Assert.Equal("Connexion à Forecast requise.", suggestions.Error);
    }

    [Fact]
    public async Task More_matches_than_listed_are_reported()
    {
        using var suggestions = Create((_, _) => Task.FromResult(new SuggestionBatch([Alpha], HasMore: true)));

        await suggestions.InputAsync("intra");

        Assert.True(suggestions.HasMore);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_an_error_state_instead_of_escaping()
    {
        using var suggestions = Create((_, _) => throw new InvalidOperationException("boom"));

        await suggestions.InputAsync("intra");

        Assert.Equal(SuggestionStatus.Error, suggestions.Status);
        Assert.Equal("La recherche dans Forecast a échoué.", suggestions.Error);
    }

    [Fact]
    public async Task A_cancellation_of_the_search_itself_is_not_reported_as_an_error()
    {
        using var suggestions = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return SuggestionBatch.Empty;
        });

        var running = suggestions.InputAsync("intra");
        suggestions.Close();
        await running;

        Assert.Equal(SuggestionStatus.Closed, suggestions.Status);
        Assert.Null(suggestions.Error);
    }

    [Fact]
    public async Task The_highlight_moves_with_the_arrow_keys_and_wraps_around()
    {
        using var suggestions = Create((_, _) => Task.FromResult(Batch(Alpha, Beta)));
        await suggestions.InputAsync("intra");

        Assert.Equal(-1, suggestions.ActiveIndex);
        Assert.Null(suggestions.Active);

        suggestions.MoveActive(1);
        Assert.Same(Alpha, suggestions.Active);
        suggestions.MoveActive(1);
        Assert.Same(Beta, suggestions.Active);
        suggestions.MoveActive(1);
        Assert.Same(Alpha, suggestions.Active);
        suggestions.MoveActive(-1);
        Assert.Same(Beta, suggestions.Active);
    }

    [Fact]
    public async Task Arrow_up_from_nothing_goes_to_the_last_line()
    {
        using var suggestions = Create((_, _) => Task.FromResult(Batch(Alpha, Beta)));
        await suggestions.InputAsync("intra");

        suggestions.MoveActive(-1);

        Assert.Same(Beta, suggestions.Active);
    }

    [Fact]
    public async Task A_new_search_resets_the_highlight()
    {
        using var suggestions = Create((_, _) => Task.FromResult(Batch(Alpha, Beta)));
        await suggestions.InputAsync("intra");
        suggestions.MoveActive(1);

        await suggestions.InputAsync("intran");

        Assert.Equal(-1, suggestions.ActiveIndex);
    }

    [Fact]
    public async Task Close_hides_the_list_and_clears_it()
    {
        using var suggestions = Create((_, _) => Task.FromResult(Batch(Alpha)));
        await suggestions.InputAsync("intra");

        suggestions.Close();

        Assert.False(suggestions.IsOpen);
        Assert.Empty(suggestions.Options);
    }

    [Fact]
    public async Task Changes_are_announced_and_stop_after_dispose()
    {
        var changes = 0;
        var suggestions = Create((_, _) => Task.FromResult(Batch(Alpha)));
        suggestions.Changed += () => changes++;

        await suggestions.InputAsync("intra");
        Assert.Equal(2, changes);

        suggestions.Dispose();
        suggestions.MoveActive(1);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Disposing_while_a_search_runs_cancels_it_and_drops_its_answer()
    {
        var token = CancellationToken.None;
        var changes = 0;
        var suggestions = Create(async (_, t) =>
        {
            token = t;
            await Task.Delay(Timeout.Infinite, t);
            return Batch(Alpha);
        });
        suggestions.Changed += () => changes++;

        var running = suggestions.InputAsync("intra");
        await Until(() => token.CanBeCanceled);
        var changesBefore = changes;
        suggestions.Dispose();
        await running;

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(changesBefore, changes);
    }

    [Fact]
    public async Task Works_with_a_fake_forecast_lookup_through_the_suggestion_lines()
    {
        var lookup = new FakeForecastLookup();
        lookup.Projects.Add(new ForecastProjectMatch(1078, "P1078", "Intranet Centre de Marque", "Équipe Laurence", "RUNNING"));
        using var suggestions = Create(async (text, token) =>
            ForecastSuggestions.ProjectBatch(await lookup.SearchProjectsAsync(text, token), [], text, numberFirst: false));

        await suggestions.InputAsync("intranet");

        var line = Assert.Single(suggestions.Options);
        Assert.Equal("Intranet Centre de Marque", line.Primary);
        Assert.Equal("P1078 · Équipe Laurence · En cours", line.Secondary);
        Assert.Equal(["intranet"], lookup.ProjectQueries);
    }
}
