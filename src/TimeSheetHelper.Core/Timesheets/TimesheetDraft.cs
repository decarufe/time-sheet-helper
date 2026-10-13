namespace TimeSheetHelper.Core.Timesheets;

public sealed class TimesheetDraft
{
    public int Id { get; set; }

    /// <summary>Monday of the week.</summary>
    public DateOnly WeekStart { get; set; }

    public DateTime? SubmittedUtc { get; set; }
    public List<TimesheetEntry> Entries { get; set; } = [];
}

public sealed class TimesheetEntry
{
    public int Id { get; set; }
    public int TimesheetDraftId { get; set; }
    public DateOnly Date { get; set; }
    public int ForecastTaskId { get; set; }

    /// <summary>Always a multiple of 0.25.</summary>
    public decimal Hours { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Manual assignment of a 15-minute slot by the user; overrides anything the engine computes.</summary>
public sealed class SlotOverride
{
    public int Id { get; set; }
    public DateTime SlotStartUtc { get; set; }

    /// <summary>Null means "not work time" (exclude the slot).</summary>
    public int? ForecastTaskId { get; set; }

    /// <summary>Comment given with the assignment; ends up in the notes of the timesheet entry.</summary>
    public string? Notes { get; set; }
}

/// <summary>Cached Claude classification of an activity group, keyed by a fingerprint of its metadata.</summary>
public sealed class ClassificationCacheEntry
{
    public int Id { get; set; }
    public required string Fingerprint { get; set; }
    public int? ForecastTaskId { get; set; }
    public double Confidence { get; set; }
    public string? Reason { get; set; }
    public string? SuggestedRuleJson { get; set; }
    public DateTime CreatedUtc { get; set; }
}
