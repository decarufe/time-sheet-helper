using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Engine;

/// <summary>Settings of the timesheet engine, bound from the "Engine" configuration section.</summary>
public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>Length of a timesheet slot. Forecast accepts quarter hours, so this is not meant to change.</summary>
    public static readonly TimeSpan SlotLength = TimeSpan.FromMinutes(15);

    /// <summary>IANA or Windows time zone id used to cut days. Null uses <see cref="TimeProvider.LocalTimeZone"/>.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>A slot with less activity than this (and no meeting time reaching it) is not work.</summary>
    public TimeSpan MinActivityPerSlot { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Expected hours per working day (Monday to Friday); days below are flagged. Set with "Engine:TargetHoursPerDay".</summary>
    public decimal TargetHoursPerDay { get; set; } = 8m;

    /// <summary>Weight multiplier of an accepted meeting, per second of overlap. High enough to beat any focus.</summary>
    public double MeetingWeight { get; set; } = 10;

    /// <summary>
    /// When a slot contains more active VS Code editing than this, the meeting stops overriding focus
    /// (the user was obviously coding during the meeting) and counts as ordinary activity.
    /// </summary>
    public TimeSpan MeetingYieldsToEditingAbove { get; set; } = TimeSpan.FromMinutes(8);

    /// <summary>Calendar events longer than this (all-day events, out-of-office blocks) are ignored.</summary>
    public TimeSpan MaxMeetingDuration { get; set; } = TimeSpan.FromHours(10);

    /// <summary>Calendar <see cref="ActivityEvent.Kind"/> values that are not attended meetings.</summary>
    public List<string> IgnoredCalendarKinds { get; set; } = [];

    /// <summary>Weight per second of overlap for presence sources (Focus, VsCode, ...). Key is the source name; overrides the source's own default.</summary>
    public Dictionary<string, double> SourceWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Weight per second of overlap for point-event evidence windows (commit, email, ...): a bonus, not a proof of work.</summary>
    public double EvidenceWeight { get; set; } = 0.5;

    /// <summary>
    /// Implicit duration before a point event (commit, sent email, ...). Key is "Source.kind" or "Source";
    /// the most specific key wins, then <see cref="DefaultEvidenceWindow"/>.
    /// </summary>
    public Dictionary<string, TimeSpan> EvidenceWindows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan DefaultEvidenceWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Regex rule evaluation timeout.</summary>
    public TimeSpan RegexTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Own e-mail domains, ignored when grouping by participant domain.</summary>
    public List<string> OwnDomains { get; set; } = [];

    public ClaudeOptions Claude { get; set; } = new();

    internal static readonly IReadOnlyDictionary<string, TimeSpan> DefaultEvidenceWindows = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
    {
        ["GitHub.commit"] = TimeSpan.FromMinutes(15),
        ["AzureDevOps.commit"] = TimeSpan.FromMinutes(15),
        ["LocalGit"] = TimeSpan.FromMinutes(15),
        ["GitHub.pr-review"] = TimeSpan.FromMinutes(20),
        ["AzureDevOps.pr-review"] = TimeSpan.FromMinutes(20),
        // Kind names must match what the collectors emit (see Collectors.DevOps).
        ["GitHub.pr-opened"] = TimeSpan.FromMinutes(15),
        ["AzureDevOps.pr-opened"] = TimeSpan.FromMinutes(15),
        ["GitHub.pr-comment"] = TimeSpan.FromMinutes(10),
        ["AzureDevOps.pr-comment"] = TimeSpan.FromMinutes(10),
        ["GitHub.pr-review-comment"] = TimeSpan.FromMinutes(10),
        ["GitHub.issue-comment"] = TimeSpan.FromMinutes(10),
        ["AzureDevOps.workitem-update"] = TimeSpan.FromMinutes(10),
        ["Email"] = TimeSpan.FromMinutes(10),
        ["Teams"] = TimeSpan.FromMinutes(5),
    };

    internal static readonly IReadOnlyList<string> DefaultIgnoredCalendarKinds =
        ["declined", "tentative", "cancelled", "free", "meeting-declined", "meeting-tentative", "meeting-cancelled", "all-day"];

    public double GetSourceWeight(ActivitySource source, SourceTraits? traits = null) =>
        SourceWeights.TryGetValue(source.ToString(), out var weight) ? weight
        : (traits ?? SourceCatalog.Default.Get(source)).DefaultWeight ?? 1.0;

    public TimeSpan GetEvidenceWindow(ActivitySource source, string? kind, SourceTraits? traits = null)
    {
        if (kind is not null)
        {
            var specific = $"{source}.{kind}";
            if (EvidenceWindows.TryGetValue(specific, out var w) || DefaultEvidenceWindows.TryGetValue(specific, out w))
                return w;
        }

        var general = source.ToString();
        if (EvidenceWindows.TryGetValue(general, out var g) || DefaultEvidenceWindows.TryGetValue(general, out g))
            return g;

        return (traits ?? SourceCatalog.Default.Get(source)).DefaultEvidenceWindow ?? DefaultEvidenceWindow;
    }

    public bool IsIgnoredCalendarKind(string? kind) =>
        kind is not null
        && (IgnoredCalendarKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)
            || DefaultIgnoredCalendarKinds.Contains(kind, StringComparer.OrdinalIgnoreCase));

    public TimeZoneInfo ResolveTimeZone(TimeProvider timeProvider) =>
        string.IsNullOrWhiteSpace(TimeZoneId) ? timeProvider.LocalTimeZone : TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

public sealed class ClaudeOptions
{
    /// <summary>Feature flag: no group metadata leaves the machine unless this is true.</summary>
    public bool Enabled { get; set; }

    public string Model { get; set; } = "claude-sonnet-5-5";

    public int MaxTokens { get; set; } = 2048;

    /// <summary>Groups shorter than this over the week are not worth a Claude call.</summary>
    public TimeSpan MinGroupDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Maximum number of sample titles / paths / subjects sent per group.</summary>
    public int MaxSamples { get; set; } = 8;

    /// <summary>Opt into server-side refusal fallback (beta). Claude API only.</summary>
    public bool UseServerSideFallback { get; set; } = true;
}
