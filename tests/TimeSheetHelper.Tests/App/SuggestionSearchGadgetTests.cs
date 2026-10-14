using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

/// <summary>Edge cases of <see cref="SuggestionSearch"/> that <see cref="SuggestionSearchTests"/> does not pin down: tokens, errors of stale searches, disposal while debouncing.</summary>
public sealed class SuggestionSearchGadgetTests
{
    private static readonly SuggestionOption Alpha = new("Alpha");

    private static TaskCompletionSource<SuggestionBatch> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SuggestionSearch Create(Func<string, CancellationToken, Task<SuggestionBatch>> search, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(search, ForecastLookup.MinQueryLength, TimeSpan.FromMilliseconds(300), delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task A_new_keystroke_cancels_the_token_of_the_search_in_progress_and_gives_the_new_one_a_live_token()
    {
        var tokens = new List<CancellationToken>();
        var gates = new List<TaskCompletionSource<SuggestionBatch>>();
        using var suggestions = Create((_, token) =>
        {
            tokens.Add(token);
            var gate = Gate();
            gates.Add(gate);
            return gate.Task;
        });

        var first = suggestions.InputAsync("in");
        var second = suggestions.InputAsync("int");

        Assert.Equal(2, tokens.Count);
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.False(tokens[1].IsCancellationRequested);

        gates[1].SetResult(new SuggestionBatch([Alpha]));
        gates[0].SetResult(SuggestionBatch.Empty);
        await Task.WhenAll(first, second);
        Assert.Equal([Alpha], suggestions.Options);
    }

    [Fact]
    public async Task The_failure_of_a_superseded_search_does_not_replace_the_newer_results()
    {
        var slow = Gate();
        var calls = 0;
        using var suggestions = Create((_, _) => ++calls == 1 ? slow.Task : Task.FromResult(new SuggestionBatch([Alpha])));

        var first = suggestions.InputAsync("in");
        await suggestions.InputAsync("int");
        slow.SetException(new InvalidOperationException("boom"));
        await first;

        Assert.Equal(SuggestionStatus.Results, suggestions.Status);
        Assert.Equal([Alpha], suggestions.Options);
        Assert.Null(suggestions.Error);
    }

    [Fact]
    public async Task Disposing_during_the_debounce_delay_never_runs_the_search()
    {
        var searched = 0;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suggestions = Create(
            (_, _) =>
            {
                searched++;
                return Task.FromResult(SuggestionBatch.Empty);
            },
            (_, token) =>
            {
                token.Register(() => pending.TrySetCanceled(token));
                return pending.Task;
            });

        var running = suggestions.InputAsync("intra");
        suggestions.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(5)); // a search left running would hang here instead of failing
        suggestions.Dispose();

        Assert.Equal(0, searched);
    }

    [Fact]
    public async Task The_text_is_trimmed_before_it_is_counted_and_searched()
    {
        var queries = new List<string>();
        using var suggestions = Create((text, _) =>
        {
            queries.Add(text);
            return Task.FromResult(SuggestionBatch.Empty);
        });

        await suggestions.InputAsync("  a  ");
        await suggestions.InputAsync("  intra  ");

        Assert.Equal(["intra"], queries);
    }

    [Fact]
    public async Task A_timeout_that_the_user_did_not_ask_for_is_an_error_not_a_silent_cancellation()
    {
        using var suggestions = Create((_, _) => throw new TaskCanceledException("timeout"));

        await suggestions.InputAsync("intra");

        Assert.Equal(SuggestionStatus.Error, suggestions.Status);
        Assert.NotNull(suggestions.Error);
    }

    [Fact]
    public async Task The_debounce_delay_given_is_the_one_waited_and_defaults_to_300_milliseconds()
    {
        var waited = new List<TimeSpan>();
        Task Record(TimeSpan span, CancellationToken token)
        {
            waited.Add(span);
            return Task.CompletedTask;
        }

        using var byDefault = new SuggestionSearch((_, _) => Task.FromResult(SuggestionBatch.Empty), 2, delay: Record);
        using var custom = new SuggestionSearch((_, _) => Task.FromResult(SuggestionBatch.Empty), 2, TimeSpan.FromSeconds(1), Record);

        await byDefault.InputAsync("intra");
        await custom.InputAsync("intra");

        Assert.Equal([TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)], waited);
    }

    [Fact]
    public async Task The_highlight_wraps_whatever_the_size_of_the_step()
    {
        var beta = new SuggestionOption("Beta");
        using var suggestions = Create((_, _) => Task.FromResult(new SuggestionBatch([Alpha, beta])));
        await suggestions.InputAsync("intra");
        suggestions.MoveActive(1);

        suggestions.MoveActive(-3);
        Assert.Same(beta, suggestions.Active);

        suggestions.MoveActive(5);
        Assert.Same(Alpha, suggestions.Active);
    }

    [Fact]
    public async Task Moving_the_highlight_on_a_list_without_lines_does_nothing_and_announces_nothing()
    {
        var changes = 0;
        using var suggestions = Create((_, _) => Task.FromResult(SuggestionBatch.Empty));
        await suggestions.InputAsync("zzz");
        suggestions.Changed += () => changes++;

        suggestions.MoveActive(1);
        suggestions.MoveActive(-1);

        Assert.Equal(-1, suggestions.ActiveIndex);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Closing_a_list_that_is_already_closed_announces_nothing()
    {
        var changes = 0;
        using var suggestions = Create((_, _) => Task.FromResult(SuggestionBatch.Empty));
        suggestions.Changed += () => changes++;

        suggestions.Close();

        Assert.Equal(SuggestionStatus.Closed, suggestions.Status);
        Assert.Equal(0, changes);
    }
}
