namespace TimeSheetHelper.Core.Timesheets;

public enum JournalKind { Planned, Sent, Updated, Deleted, Skipped, Resolved, Interrupted, Resumed, Cancelled, Failed, Migrated }

public enum JournalOutcome { Ok, Partial, Failed, Unknown }

/// <summary>
/// One line of the submission journal of a day. <see cref="Message"/> holds fixed French text with numbers, dates and task ids only:
/// never a token, secret, e-mail body, activity content or raw Forecast response.
/// </summary>
public sealed class SubmissionJournalEntry
{
    public const int MessageMaxLength = 500;

    public int Id { get; set; }
    public int DaySubmissionId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public JournalKind Kind { get; set; }
    public JournalOutcome Outcome { get; set; }
    public int? ForecastTaskId { get; set; }
    public decimal? Hours { get; set; }
    public string Message { get; set; } = "";
}
