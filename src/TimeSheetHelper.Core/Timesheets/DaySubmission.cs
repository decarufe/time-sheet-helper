namespace TimeSheetHelper.Core.Timesheets;

/// <summary>Persisted submission status of one day. Values are stored as integers: never renumber.</summary>
public enum DayStatus
{
    NotAttempted = 0,
    InProgress = 1,
    Sent = 2,

    /// <summary>Back-filled from <see cref="TimesheetDraft.SubmittedUtc"/>: sent, but what was sent is unknown.</summary>
    SentUnknownFingerprint = 3,

    Modified = 4,
    Partial = 5,
    Conflict = 6,

    /// <summary>No longer produced (the Forecast lock was removed); rows still holding 7 are read as not locked.</summary>
    LegacyLocked = 7,
    Error = 8,
}

/// <summary>Submission state of one day of a week; unique on (<see cref="WeekStart"/>, <see cref="Date"/>).</summary>
public sealed class DaySubmission
{
    public int Id { get; set; }

    /// <summary>Monday of the week, same as <see cref="TimesheetDraft.WeekStart"/>.</summary>
    public DateOnly WeekStart { get; set; }

    public DateOnly Date { get; set; }
    public DayStatus Status { get; set; }

    /// <summary>Fingerprint of the day content last sent; null when unknown.</summary>
    public string? Fingerprint { get; set; }

    public DateTime? SentUtc { get; set; }

    /// <summary>Set if and only if <see cref="Status"/> is <see cref="DayStatus.InProgress"/>.</summary>
    public DateTime? InProgressSinceUtc { get; set; }

    public List<DaySubmissionCell> Cells { get; set; } = [];
}

/// <summary>What the app actually wrote to Forecast for one task on one day; unique on (<see cref="DaySubmissionId"/>, <see cref="ForecastTaskId"/>).</summary>
public sealed class DaySubmissionCell
{
    public int Id { get; set; }
    public int DaySubmissionId { get; set; }
    public int ForecastTaskId { get; set; }

    /// <summary>Always a multiple of 0.25.</summary>
    public decimal SentHours { get; set; }

    /// <summary>Forecast time entry id when known; required to update or delete the entry.</summary>
    public string? ForecastEntryId { get; set; }
}
