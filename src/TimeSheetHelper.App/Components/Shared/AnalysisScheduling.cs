namespace TimeSheetHelper.App.Components.Shared;

/// <summary>
/// Makes sure only the latest of several analyses (or simulations) counts: starting one cancels the one before, and a result that comes
/// back after a newer one was started is recognised as out of date and dropped.
/// </summary>
public sealed class AnalysisSequencer : IDisposable
{
    public readonly record struct Ticket(int Version, CancellationToken Token);

    private readonly object gate = new();
    private CancellationTokenSource? current;
    private int version;
    private bool disposed;

    /// <summary>Starts a new run: the previous one is cancelled and its result will be refused by <see cref="IsCurrent"/>.</summary>
    public Ticket Begin()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Retire();
            current = new CancellationTokenSource();
            return new Ticket(++version, current.Token);
        }
    }

    /// <summary>True when no newer run was started (and nothing was cancelled) since <paramref name="ticket"/> was issued.</summary>
    public bool IsCurrent(Ticket ticket)
    {
        lock (gate)
            return !disposed && ticket.Version == version && !ticket.Token.IsCancellationRequested;
    }

    /// <summary>Cancels the running one, if any: its result is refused too.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            Retire();
            version++;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            Retire();
        }
    }

    private void Retire()
    {
        if (current is null)
            return;
        current.Cancel();
        current.Dispose();
        current = null;
    }
}
