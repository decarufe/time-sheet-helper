using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Windows.VsCode;

/// <summary>
/// Turns VS Code heartbeats into persisted segments. Pure logic, so it is unit-testable.
/// Only the focused window sends heartbeats, so all windows form a single stream of attention.
/// A segment continues while consecutive heartbeats arrive within <see cref="MaxGap"/> and keep the same
/// workspace + file + kind. Like <see cref="SegmentAggregator"/>, the same <see cref="ActivityEvent"/>
/// instance is returned each time it grows: the caller must upsert every returned event.
/// Not thread-safe; callers serialize access.
/// </summary>
public sealed class VsCodeSessionAggregator(TimeSpan maxGap)
{
    public const string EditingKind = "editing";
    public const string AiKind = "ai";

    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromSeconds(90);

    private SegmentKey? _key;
    private ActivityEvent? _current;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    public VsCodeSessionAggregator() : this(DefaultMaxGap)
    {
    }

    public TimeSpan MaxGap { get; } = maxGap;

    public ActivityEvent? Current => _current;

    /// <summary>Records one heartbeat; returns the events to upsert (zero-length segments are never returned).</summary>
    public IReadOnlyList<ActivityEvent> Observe(VsCodeHeartbeat heartbeat)
    {
        var at = DateTime.SpecifyKind(heartbeat.TimestampUtc, DateTimeKind.Utc);

        // Replayed or out-of-order heartbeats (offline queue) cannot reopen the past.
        if (at < _lastHeartbeatUtc)
            return [];

        var contiguous = _current is not null && at - _lastHeartbeatUtc <= MaxGap;
        _lastHeartbeatUtc = at;
        var written = new List<ActivityEvent>(2);

        if (!heartbeat.WindowFocused)
        {
            // Blur: the window lost the focus at "at"; the segment ends there.
            if (contiguous)
                Extend(at, written);
            Reset();
            return written;
        }

        var key = SegmentKey.From(heartbeat);
        if (contiguous && key == _key)
        {
            Extend(at, written);
            return written;
        }

        // Switching file/workspace/kind while active: the previous segment runs until now.
        if (contiguous)
            Extend(at, written);

        _key = key;
        _current = ToActivity(heartbeat, key, at);
        return written;
    }

    private void Extend(DateTime endUtc, List<ActivityEvent> written)
    {
        _current!.EndUtc = endUtc;
        if (_current.EndUtc > _current.StartUtc)
            written.Add(_current);
    }

    private void Reset()
    {
        _current = null;
        _key = null;
    }

    private static ActivityEvent ToActivity(VsCodeHeartbeat heartbeat, SegmentKey key, DateTime atUtc) => new()
    {
        Source = ActivitySource.VsCode,
        Kind = key.Kind,
        StartUtc = atUtc,
        EndUtc = atUtc,
        ProcessName = "Code",
        Folder = key.Folder,
        FilePath = key.File,
        Repo = GitRemote.Normalize(heartbeat.GitRemote),
        Title = string.IsNullOrWhiteSpace(heartbeat.GitBranch) ? null : heartbeat.GitBranch,
        Subject = heartbeat.WorkspaceName,
        ExternalId = $"{heartbeat.SessionId}@{atUtc:O}",
    };

    private sealed record SegmentKey(string? Folder, string? File, string Kind)
    {
        public static SegmentKey From(VsCodeHeartbeat heartbeat) => new(
            heartbeat.WorkspaceFolders is { Count: > 0 } folders ? folders[0] : null,
            string.IsNullOrEmpty(heartbeat.ActiveFile) ? null : heartbeat.ActiveFile,
            heartbeat.AiActivity ? AiKind : EditingKind);

        public bool Equals(SegmentKey? other) =>
            other is not null
            && string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase)
            && Kind == other.Kind;

        public override int GetHashCode() => HashCode.Combine(
            Folder?.ToUpperInvariant(), File?.ToUpperInvariant(), Kind);
    }
}
