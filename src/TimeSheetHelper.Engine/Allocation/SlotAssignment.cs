namespace TimeSheetHelper.Engine.Allocation;

public enum SlotKind
{
    /// <summary>Not enough activity: not work time.</summary>
    Empty,

    /// <summary>Work inferred from focus, editor and evidence activity.</summary>
    Activity,

    /// <summary>An accepted meeting decided the slot.</summary>
    Meeting,

    /// <summary>The user assigned the slot to a task.</summary>
    Override,

    /// <summary>The user marked the slot as not work.</summary>
    ExcludedByOverride,

    /// <summary>The slot goes to the "Ignoré" task (by rule or by the user): kept out of the timesheet.</summary>
    Ignored,
}

/// <summary>A candidate considered for a slot, with its share of the slot's total weight.</summary>
public sealed record SlotCandidate(int? TaskId, string? GroupKey, double Weight, double Share);

/// <summary>Result of allocating one 15-minute slot.</summary>
/// <param name="TaskId">Forecast task, when the winner is classified (rule or override).</param>
/// <param name="GroupKey">Unclassified group, when the winner has no rule yet.</param>
/// <param name="Confidence">0..1, share of the winning candidate (1 for overrides).</param>
/// <param name="Alternatives">Other candidates, best first.</param>
/// <param name="Evidence">Ids of the activities that weighed in the slot.</param>
/// <param name="ActiveTime">Time covered by active work or meetings in the slot.</param>
/// <param name="Notes">Comment the user gave when assigning the slot (overrides only).</param>
public sealed record SlotAssignment(
    DateTime SlotStartUtc,
    SlotKind Kind,
    int? TaskId,
    string? GroupKey,
    double Confidence,
    IReadOnlyList<SlotCandidate> Alternatives,
    IReadOnlyList<long> Evidence,
    TimeSpan ActiveTime,
    string? Notes = null)
{
    public DateTime SlotEndUtc => SlotStartUtc + EngineOptions.SlotLength;
    public bool IsWork => Kind is SlotKind.Activity or SlotKind.Meeting or SlotKind.Override;
    public bool IsClassified => IsWork && TaskId is not null;
}
