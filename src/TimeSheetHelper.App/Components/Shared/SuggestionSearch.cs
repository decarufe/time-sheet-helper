namespace TimeSheetHelper.App.Components.Shared;

/// <summary>One line of a suggestion list. <paramref name="Tag"/> carries what the line stands for (a Forecast match, a known project name).</summary>
public sealed record SuggestionOption(string Primary, string? Secondary = null, object? Tag = null);

/// <summary>The answer to one search: the lines to list, whether more matched than are listed, and a readable error when Forecast could not be asked.</summary>
public sealed record SuggestionBatch(IReadOnlyList<SuggestionOption> Options, bool HasMore = false, string? Error = null)
{
    public static SuggestionBatch Empty { get; } = new([]);
}

public enum SuggestionStatus { Closed, Loading, Results, Empty, Error }

/// <summary>
/// The state of one "type to search" list: waits for a pause in the typing (debounce), runs one search at a time and drops the
/// answer of any search that a later keystroke made obsolete, so a slow answer can never replace a newer one.
/// Raises <see cref="Changed"/> after every state change; not thread safe (meant to run on the component's synchronization context).
/// </summary>
public sealed class SuggestionSearch : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private readonly Func<string, CancellationToken, Task<SuggestionBatch>> _search;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _debounce;
    private readonly int _minLength;
    private CancellationTokenSource? _cts;
    private int _version;
    private bool _disposed;

    public SuggestionSearch(
        Func<string, CancellationToken, Task<SuggestionBatch>> search,
        int minLength,
        TimeSpan? debounce = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _search = search;
        _minLength = minLength;
        _debounce = debounce ?? DefaultDebounce;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    public event Action? Changed;

    public SuggestionStatus Status { get; private set; } = SuggestionStatus.Closed;

    public IReadOnlyList<SuggestionOption> Options { get; private set; } = [];

    public bool HasMore { get; private set; }

    public string? Error { get; private set; }

    /// <summary>The highlighted line (keyboard), -1 for none.</summary>
    public int ActiveIndex { get; private set; } = -1;

    public bool IsOpen => Status != SuggestionStatus.Closed;

    public SuggestionOption? Active => ActiveIndex >= 0 && ActiveIndex < Options.Count ? Options[ActiveIndex] : null;

    /// <summary>
    /// The text changed: cancels the search in progress and, when the text is long enough, searches after the debounce delay.
    /// The task ends when this search is done or was superseded; it never throws for a failing search.
    /// </summary>
    public Task InputAsync(string? text)
    {
        var query = (text ?? "").Trim();
        var (version, token) = Restart();
        if (query.Length < _minLength)
        {
            Show(SuggestionStatus.Closed, [], false, null);
            return Task.CompletedTask;
        }

        Show(SuggestionStatus.Loading, [], false, null);
        return RunAsync(query, version, token);
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> lines, wrapping around; the first move goes to the first (or last) line.</summary>
    public void MoveActive(int delta)
    {
        if (Options.Count == 0)
            return;

        ActiveIndex = ActiveIndex < 0
            ? (delta > 0 ? 0 : Options.Count - 1)
            : (((ActiveIndex + delta) % Options.Count) + Options.Count) % Options.Count;
        RaiseChanged();
    }

    /// <summary>Hides the list and abandons any search in progress.</summary>
    public void Close()
    {
        Restart();
        if (Status != SuggestionStatus.Closed)
            Show(SuggestionStatus.Closed, [], false, null);
    }

    public void Dispose()
    {
        _disposed = true;
        _version++;
        Release();
    }

    private async Task RunAsync(string query, int version, CancellationToken token)
    {
        SuggestionBatch batch;
        try
        {
            await _delay(_debounce, token);
            batch = await _search(query, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (version == _version)
                Show(SuggestionStatus.Error, [], false, "La recherche dans Forecast a échoué.");
            return;
        }

        // A newer keystroke (or a close) has superseded this search while it was running: its answer is stale.
        if (version != _version)
            return;

        var status = batch.Options.Count > 0 ? SuggestionStatus.Results
            : batch.Error is not null ? SuggestionStatus.Error
            : SuggestionStatus.Empty;
        Show(status, batch.Options, batch.HasMore, batch.Error);
    }

    private (int Version, CancellationToken Token) Restart()
    {
        Release();
        _cts = new CancellationTokenSource();
        return (++_version, _cts.Token);
    }

    /// <summary>Cancels the search in progress and frees its source (the cancelled token stays valid for the task still holding it).</summary>
    private void Release()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void Show(SuggestionStatus status, IReadOnlyList<SuggestionOption> options, bool hasMore, string? error)
    {
        Status = status;
        Options = options;
        HasMore = hasMore;
        Error = error;
        ActiveIndex = -1;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (!_disposed)
            Changed?.Invoke();
    }
}
