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
