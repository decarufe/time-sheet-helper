namespace TimeSheetHelper.Core.Activities;

/// <summary>
/// Normalized activity record. Every collector produces these; only metadata is kept (never message bodies).
/// Times are stored in UTC. <see cref="EndUtc"/> is null for point-in-time events (commit, sent email, ...).
/// </summary>
public sealed class ActivityEvent
{
    public long Id { get; set; }
    public ActivitySource Source { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }

    /// <summary>Source-specific kind, e.g. "commit", "pr-review", "meeting", "chat-message".</summary>
    public string? Kind { get; set; }

    public string? ProcessName { get; set; }
    public string? Title { get; set; }
    public string? FilePath { get; set; }
    public string? Folder { get; set; }
    public string? Url { get; set; }

    /// <summary>Repository identifier, e.g. "owner/repo" or an ADO repo name.</summary>
    public string? Repo { get; set; }

    /// <summary>Organization / client-level grouping (GitHub org, ADO project, ...).</summary>
    public string? Project { get; set; }

    public string? Subject { get; set; }

    /// <summary>Semicolon-separated participants (emails or display names).</summary>
    public string? Participants { get; set; }

    /// <summary>Stable identifier from the source, used to deduplicate re-synced events.</summary>
    public string? ExternalId { get; set; }

    public TimeSpan Duration => EndUtc is { } end ? end - StartUtc : TimeSpan.Zero;
}
