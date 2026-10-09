using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Windows;

/// <summary>
/// Turns a stream of focus snapshots into persisted segments. Pure logic (no Win32), so it is unit-testable.
/// Segments shorter than <see cref="MinDuration"/> are dropped, and a segment that resumes the previous
/// activity within <see cref="MergeGap"/> extends it instead of creating a new row.
/// Every returned event must be upserted by the caller: the same instance comes back each time it grows.
/// </summary>
public sealed class SegmentAggregator(TimeSpan minDuration, TimeSpan mergeGap)
{
    private FocusSnapshot? _current;
    private DateTime _currentStartUtc;
    private FocusSnapshot? _lastKey;
    private ActivityEvent? _last;

    public TimeSpan MinDuration { get; } = minDuration;
    public TimeSpan MergeGap { get; } = mergeGap;

    public FocusSnapshot? Current => _current;
    public DateTime CurrentStartUtc => _currentStartUtc;

    /// <summary>Records the snapshot observed at <paramref name="atUtc"/>; returns events to upsert.</summary>
    public IReadOnlyList<ActivityEvent> Observe(FocusSnapshot snapshot, DateTime atUtc)
    {
        if (_current is not null && _current.SameActivityAs(snapshot))
            return [];

        var written = CloseCurrent(atUtc);
        if (snapshot.State != FocusState.Paused)
        {
            _current = snapshot;
            _currentStartUtc = atUtc;
        }
        return written;
    }

    /// <summary>Persists the in-progress segment without ending it, so a crash loses at most one checkpoint interval.</summary>
    public IReadOnlyList<ActivityEvent> Checkpoint(DateTime nowUtc)
    {
        if (_current is null || nowUtc - _currentStartUtc < MinDuration)
            return [];

        var snapshot = _current;
        var written = CloseCurrent(nowUtc);
        _current = snapshot;
        _currentStartUtc = nowUtc;
        return written;
    }

    public IReadOnlyList<ActivityEvent> Flush(DateTime nowUtc) => CloseCurrent(nowUtc);

    private IReadOnlyList<ActivityEvent> CloseCurrent(DateTime endUtc)
    {
        var snapshot = _current;
        _current = null;
        if (snapshot is null)
            return [];

        // Idle is detected after the fact, so the end can precede the start of the open segment.
        if (endUtc < _currentStartUtc)
            endUtc = _currentStartUtc;

        if (_last is not null && _lastKey!.SameActivityAs(snapshot) && _currentStartUtc - _last.EndUtc!.Value <= MergeGap)
        {
            _last.EndUtc = endUtc;
            return [_last];
        }

        if (endUtc - _currentStartUtc < MinDuration)
            return [];

        _last = snapshot.ToActivity(_currentStartUtc);
        _last.EndUtc = endUtc;
        _lastKey = snapshot;
        return [_last];
    }
}
