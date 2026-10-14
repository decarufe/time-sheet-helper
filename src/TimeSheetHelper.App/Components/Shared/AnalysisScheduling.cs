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

/// <summary>
/// Runs an action once things have been quiet for <c>delay</c>: every <see cref="Request"/> restarts the wait, and cancels the action if
/// it is already running (the action receives a token for that). Used to re-run the simulation after the data changed, without
/// launching one per click.
/// </summary>
public sealed class DebouncedAction(TimeSpan delay, Func<CancellationToken, Task> action, Action<Exception>? onError = null) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? current;
    private bool disposed;

    /// <summary>True from <see cref="Request"/> until the action has finished (waiting or running).</summary>
    public bool IsPending { get; private set; }

    public void Request()
    {
        CancellationToken token;
        lock (gate)
        {
            if (disposed)
                return;
            Retire();
            current = new CancellationTokenSource();
            token = current.Token;
            IsPending = true;
        }

        _ = RunAsync(token);
    }

    /// <summary>Drops what is waiting (and cancels what is running).</summary>
    public void Cancel()
    {
        lock (gate)
        {
            Retire();
            IsPending = false;
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
            IsPending = false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            await action(token);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer request, or cancelled: nothing to report.
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            lock (gate)
            {
                if (current is not null && current.Token == token)
                    IsPending = false;
            }
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
