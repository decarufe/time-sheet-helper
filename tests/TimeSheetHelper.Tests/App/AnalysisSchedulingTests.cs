using TimeSheetHelper.App.Components.Shared;

namespace TimeSheetHelper.Tests.App;

public sealed class AnalysisSequencerTests
{
    [Fact]
    public void A_new_run_cancels_the_previous_one_and_makes_its_result_out_of_date()
    {
        using var sequencer = new AnalysisSequencer();

        var first = sequencer.Begin();
        Assert.True(sequencer.IsCurrent(first));

        var second = sequencer.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(sequencer.IsCurrent(first));
        Assert.True(sequencer.IsCurrent(second));
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(second.Version > first.Version);
    }

    [Fact]
    public void Results_come_back_in_any_order_but_only_the_latest_is_accepted()
    {
        using var sequencer = new AnalysisSequencer();
        var accepted = new List<int>();

        var slow = sequencer.Begin();
        var fast = sequencer.Begin();

        // The fast (latest) run answers first, then the slow (older) one arrives late.
        if (sequencer.IsCurrent(fast))
            accepted.Add(fast.Version);
        if (sequencer.IsCurrent(slow))
            accepted.Add(slow.Version);

        Assert.Equal([fast.Version], accepted);
    }

    [Fact]
    public void Cancel_cancels_the_running_one_and_refuses_its_result()
    {
        using var sequencer = new AnalysisSequencer();
        var ticket = sequencer.Begin();

        sequencer.Cancel();

        Assert.True(ticket.Token.IsCancellationRequested);
        Assert.False(sequencer.IsCurrent(ticket));
    }

    [Fact]
    public void Cancel_without_a_running_one_is_harmless()
    {
        using var sequencer = new AnalysisSequencer();

        sequencer.Cancel();

        Assert.True(sequencer.IsCurrent(sequencer.Begin()));
    }

    [Fact]
    public void After_disposal_nothing_is_current_and_no_run_can_start()
    {
        var sequencer = new AnalysisSequencer();
        var ticket = sequencer.Begin();

        sequencer.Dispose();
        sequencer.Dispose();

        Assert.True(ticket.Token.IsCancellationRequested);
        Assert.False(sequencer.IsCurrent(ticket));
        Assert.Throws<ObjectDisposedException>(() => sequencer.Begin());
    }
}

public sealed class DebouncedActionTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static async Task<T> Within<T>(Task<T> task) =>
        await task.WaitAsync(Patience);

    [Fact]
    public async Task Requests_made_in_a_burst_run_the_action_only_once_after_the_quiet_period()
    {
        var runs = 0;
        var done = new TaskCompletionSource();
        using var debounced = new DebouncedAction(Delay, _ =>
        {
            Interlocked.Increment(ref runs);
            done.TrySetResult();
            return Task.CompletedTask;
        });

        debounced.Request();
        debounced.Request();
        debounced.Request();
        Assert.True(debounced.IsPending);

        await done.Task.WaitAsync(Patience);
        await Task.Delay(Delay * 3);

        Assert.Equal(1, runs);
        Assert.False(debounced.IsPending);
    }

    [Fact]
    public async Task The_action_does_not_run_before_the_delay()
    {
        var ran = false;
        using var debounced = new DebouncedAction(TimeSpan.FromMilliseconds(400), _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        debounced.Request();
        await Task.Delay(50);

        Assert.False(ran);
        debounced.Cancel();
    }

    [Fact]
    public async Task Cancel_drops_the_waiting_action()
    {
        var ran = false;
        using var debounced = new DebouncedAction(Delay, _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        debounced.Request();
        debounced.Cancel();
        await Task.Delay(Delay * 4);

        Assert.False(ran);
        Assert.False(debounced.IsPending);
    }

    [Fact]
    public async Task A_new_request_while_the_action_runs_cancels_it_and_runs_it_again()
    {
        var started = new SemaphoreSlim(0);
        var tokens = new List<CancellationToken>();
        var secondDone = new TaskCompletionSource();
        var calls = 0;
        using var debounced = new DebouncedAction(Delay, async token =>
        {
            lock (tokens)
                tokens.Add(token);
            var call = Interlocked.Increment(ref calls);
            started.Release();
            if (call == 1)
                await Task.Delay(Patience, token);
            else
                secondDone.TrySetResult();
        });

        debounced.Request();
        await started.WaitAsync(Patience);
        debounced.Request();
        await secondDone.Task.WaitAsync(Patience);

        Assert.Equal(2, calls);
        Assert.True(tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task Disposal_cancels_the_waiting_action_and_later_requests_do_nothing()
    {
        var ran = false;
        var debounced = new DebouncedAction(Delay, _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        debounced.Request();
        debounced.Dispose();
        debounced.Request();
        await Task.Delay(Delay * 4);

        Assert.False(ran);
        Assert.False(debounced.IsPending);
    }

    [Fact]
    public async Task An_exception_of_the_action_goes_to_the_error_handler_instead_of_being_lost()
    {
        var reported = new TaskCompletionSource<Exception>();
        using var debounced = new DebouncedAction(
            Delay, _ => throw new InvalidOperationException("boom"), ex => reported.TrySetResult(ex));

        debounced.Request();

        var error = await Within(reported.Task);
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task Cancelling_the_action_itself_is_not_reported_as_an_error()
    {
        var reported = false;
        var started = new SemaphoreSlim(0);
        using var debounced = new DebouncedAction(
            Delay,
            async token =>
            {
                started.Release();
                await Task.Delay(Patience, token);
            },
            _ => reported = true);

        debounced.Request();
        await started.WaitAsync(Patience);
        debounced.Cancel();
        await Task.Delay(Delay * 2);

        Assert.False(reported);
    }
}
