using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Grouping;

namespace TimeSheetHelper.Engine.Proposals;

public enum DayFlag
{
    Ok,

    /// <summary>Classified hours below the target: missing time or unclassified groups.</summary>
    UnderTarget,

    /// <summary>Classified hours above the target.</summary>
    OverTarget,
}

public sealed record DayTaskHours(int ForecastTaskId, decimal Hours);

/// <param name="Tasks">Hours per classified task (multiples of 0.25).</param>
/// <param name="TotalHours">Sum of <paramref name="Tasks"/>.</param>
/// <param name="UnclassifiedHours">Work time whose group has no task yet (not in the draft).</param>
public sealed record DayProposal(
    DateOnly Date,
    IReadOnlyList<DayTaskHours> Tasks,
    decimal TotalHours,
    decimal TargetHours,
    decimal UnclassifiedHours,
    DayFlag Flag)
{
    /// <summary>Positive when hours are missing to reach the target.</summary>
    public decimal Gap => TargetHours - TotalHours;
}

/// <summary>Unclassified group of the week with its time and the classifier suggestion, if any.</summary>
public sealed record GroupProposal(
    string Key,
    GroupKind Kind,
    int Minutes,
    ActivityGroup Metadata,
    ClassificationResult? Suggestion)
{
    public decimal Hours => Minutes / 60m;
}

public sealed record WeekProposal(
    DateOnly WeekStart,
    IReadOnlyList<DayProposal> Days,
    IReadOnlyList<GroupProposal> UnclassifiedGroups,
    IReadOnlyDictionary<DateOnly, IReadOnlyList<SlotAssignment>> Slots,
    IReadOnlyDictionary<int, ForecastTask> Tasks,
    bool IsSubmitted)
{
    public decimal TotalHours => Days.Sum(d => d.TotalHours);
    public decimal TargetHours => Days.Sum(d => d.TargetHours);
    public decimal UnclassifiedHours => Days.Sum(d => d.UnclassifiedHours);
}

/// <summary>Slots of one local day plus the activities they cite as evidence.</summary>
public sealed record DayTimeline(
    DateOnly Date,
    IReadOnlyList<SlotAssignment> Slots,
    IReadOnlyDictionary<long, ActivityEvent> Activities,
    IReadOnlyDictionary<int, ForecastTask> Tasks);

/// <summary>Outcome of writing a proposal to the week's draft.</summary>
/// <param name="AlreadySubmitted">True when the draft was submitted: it was left untouched.</param>
public sealed record DraftSaveResult(TimesheetDraft Draft, bool AlreadySubmitted);
