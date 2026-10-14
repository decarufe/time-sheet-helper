namespace TimeSheetHelper.Forecast.Submission;

public enum SubmissionGapSeverity
{
    /// <summary>The fill must not start while it stands.</summary>
    Blocking,

    /// <summary>The user should look at it; the fill is allowed.</summary>
    ToVerify,

    Info,
}

public enum SubmissionGapType
{
    /// <summary>Work time of a day that no task has been given (not in the draft).</summary>
    UnclassifiedRemaining,

    DayBelowTarget,
    DayAboveTarget,

    /// <summary>The day holds registrations that Forecast approved or invoiced.</summary>
    CellLocked,

    /// <summary>The day holds registrations submitted for approval.</summary>
    CellPendingApproval,

    TaskNotFound,

    /// <summary>Several tasks of Forecast bear the name: the right one cannot be chosen.</summary>
    TaskAmbiguous,

    /// <summary>The draft refers to a task the local catalogue no longer has.</summary>
    TaskUnknownLocally,

    /// <summary>The local task is switched off but still carries hours.</summary>
    TaskInactive,

    TaskClosed,
    TaskBlocked,
    ProjectClosed,
    EstimateExceeded,

    /// <summary>Forecast holds more hours for the cell than the draft expects.</summary>
    ForecastHigher,

    /// <summary>Forecast holds fewer hours for the cell than the draft expects (but some).</summary>
    ForecastLower,

    /// <summary>The week could not be read whole from Forecast: the simulation is refused.</summary>
    ReadTruncated,

    /// <summary>Forecast failed or could not be asked (tool, network, timeout, refusal).</summary>
    ForecastError,

    AuthRequired,

    /// <summary>A registration of the fill has an unknown outcome: the week must be read again.</summary>
    CellEntryUnknown,

    /// <summary>The fill stopped before the end; a part of the week may be in Forecast.</summary>
    FillInterrupted,

    /// <summary>A warning of Forecast about a registration.</summary>
    ForecastWarning,

    /// <summary>Read-only checks that could not be made (tool missing, estimate in points...).</summary>
    PreflightUnavailable,

    /// <summary>
    /// Forecast holds time without a project (internal or idle time) that the application neither writes nor compares: the tool that
    /// registers time needs a project or a task, so it is left out of the submission and shown as such.
    /// </summary>
    ForecastTimeWithoutProject,
}

/// <summary>What the user can do about a gap. <see cref="SubmissionGapAction.IsAvailable"/> says whether the application can do it today.</summary>
public enum SubmissionGapResolution
{
    LinkTask,
    CreateTask,
    RefreshTasks,
    ReactivateTask,

    /// <summary>Give the unclassified work of the day a task (assignment cards of the week view).</summary>
    ReclassifySlots,

    /// <summary>Mark the slots as not work (reversible).</summary>
    MarkNotWork,

    /// <summary>Delete the slots (irreversible).</summary>
    Remove,

    /// <summary>Open the day of the week view to see its slots.</summary>
    ViewDaySlots,

    /// <summary>The draft is rebuilt from the slots (drops the entries of a deleted task).</summary>
    RecalculateDraft,

    OpenInForecast,

    /// <summary>Run the operation again.</summary>
    Retry,

    /// <summary>Read the week again from Forecast (simulation).</summary>
    ReadAgain,

    /// <summary>Run the fill again: only what is missing is registered.</summary>
    ResumeFill,

    SignIn,

    // Not available before the persisted draft adjustments (phase A2).
    ReplaceTask,
    IgnoreKnowingly,
    AlignDraftOnForecast,
    AddComplement,
    AcceptForecastHours,
}

public sealed record SubmissionGapAction(
    SubmissionGapResolution Resolution, bool IsRecommended = false, bool IsAvailable = true, string? UnavailableReason = null);

/// <summary>The (task, day) cell of the draft a gap is about.</summary>
public sealed record SubmissionGapCell(int ForecastTaskId, string TaskName, DateOnly Date);

/// <summary>A registration already in Forecast (identifier, note and approval state), shown with a conflict.</summary>
public sealed record SubmissionExistingEntry(
    string? EntryId, string? ProjectName, string? Notes, decimal Hours, string? ApprovalStatus, bool Invoiced);

/// <summary>Totals of the cell, the day and the week before and after the recommended resolution (nothing is changed to compute it).</summary>
public sealed record SubmissionGapPreview(
    string Description, decimal? CellBefore, decimal? CellAfter, decimal? DayBefore, decimal? DayAfter, decimal WeekBefore, decimal WeekAfter);

public sealed record SubmissionGap
{
    /// <summary>Stable across analyses of the same situation: "{type}|{task id}|{yyyyMMdd}" (empty parts when not applicable).</summary>
    public required string Id { get; init; }

    public required SubmissionGapType Type { get; init; }
    public required SubmissionGapSeverity Severity { get; init; }

    public DateOnly? Date { get; init; }

    public SubmissionGapCell? Cell { get; init; }

    /// <summary>Other cells of the same project and day judged together with <see cref="Cell"/> (several tasks share the hours Forecast holds).</summary>
    public IReadOnlyList<SubmissionGapCell> RelatedCells { get; init; } = [];

    public required string Title { get; init; }

    /// <summary>Readable French explanation: what is wrong and why.</summary>
    public required string Message { get; init; }

    /// <summary>Hours the draft holds for the cell (for the group of cells when there are several).</summary>
    public decimal? ExpectedHours { get; init; }

    /// <summary>Hours Forecast holds for it.</summary>
    public decimal? ForecastHours { get; init; }

    /// <summary>Hours after a persisted adjustment of the draft: always null before phase A2.</summary>
    public decimal? AdjustedHours { get; init; }

    /// <summary>Forecast minus expected: positive when Forecast holds more.</summary>
    public decimal? DeltaHours { get; init; }

    public ForecastComparisonLevel Comparison { get; init; }

    /// <summary>The comparison was made by project and day, not by task and day (the hours of another task of the project may be mixed in).</summary>
    public bool IsApproximate => Comparison == ForecastComparisonLevel.ProjectAndDay;

    public IReadOnlyList<SubmissionExistingEntry> ExistingEntries { get; init; } = [];

    /// <summary>The resolutions that apply to this type of gap, the recommended one marked.</summary>
    public IReadOnlyList<SubmissionGapAction> Actions { get; init; } = [];

    public SubmissionGapPreview? Preview { get; init; }

    public bool IsBlocking => Severity == SubmissionGapSeverity.Blocking;
}

/// <param name="CellCount">Cells of the draft in the simulation or fill report.</param>
/// <param name="CellsWithoutGapCount">Cells with nothing to say (ready, or already in Forecast).</param>
/// <param name="WeekTargetHours">Sum of the daily objectives; null when the objective is unknown (no context given).</param>
/// <param name="WeekWithinTolerance">The week total is within the tolerance of the objective; null when unknown.</param>
/// <param name="UnclassifiedHours">Work not yet given a task (not in the draft).</param>
/// <param name="ExcludedHours">Work marked as not work (not in the draft).</param>
/// <param name="HasSimulation">A simulation or fill report was analysed.</param>
/// <param name="ReadComplete">Every registration of the week could be read from Forecast.</param>
/// <param name="CanFill">A simulation exists, the read is complete and nothing blocks. The page still checks the draft was not changed since.</param>
/// <param name="Days">One row per day of the week: draft hours against the objective (empty when no context was given).</param>
/// <param name="ForecastNonProjectHours">Hours Forecast holds without a project (internal or idle time), left out of the submission.</param>
public sealed record SubmissionSummary(
    int BlockingCount,
    int ToVerifyCount,
    int InfoCount,
    int CellCount,
    int CellsWithoutGapCount,
    decimal WeekHours,
    decimal? WeekTargetHours,
    decimal? WeekDeltaHours,
    bool? WeekWithinTolerance,
    decimal UnclassifiedHours,
    decimal ExcludedHours,
    decimal ToleranceHours,
    bool HasSimulation,
    bool ReadComplete,
    bool CanFill,
    IReadOnlyList<SubmissionDaySummary> Days,
    decimal ForecastNonProjectHours);

/// <summary>A day of the week: hours of the draft against the objective, within the tolerance or not.</summary>
public sealed record SubmissionDaySummary(
    DateOnly Date,
    decimal DraftHours,
    decimal TargetHours,
    decimal DeltaHours,
    bool WithinTolerance,
    decimal UnclassifiedHours,
    decimal ExcludedHours,
    decimal ForecastNonProjectHours);

/// <summary>The gaps of a week (blocking first, then to verify, then info; by day inside a severity) and their summary.</summary>
public sealed record SubmissionAnalysis(DateOnly WeekStart, IReadOnlyList<SubmissionGap> Gaps, SubmissionSummary Summary)
{
    public IEnumerable<SubmissionGap> Blocking => Gaps.Where(g => g.Severity == SubmissionGapSeverity.Blocking);
    public IEnumerable<SubmissionGap> ToVerify => Gaps.Where(g => g.Severity == SubmissionGapSeverity.ToVerify);
    public IEnumerable<SubmissionGap> Info => Gaps.Where(g => g.Severity == SubmissionGapSeverity.Info);
}

/// <summary>One day of the week as the draft sees it: its objective, the work not yet classified and the work set aside.</summary>
public sealed record SubmissionDayContext(DateOnly Date, decimal TargetHours, decimal UnclassifiedHours = 0, decimal ExcludedHours = 0);

/// <summary>
/// What the page knows about the week besides the draft, handed over by the application (the engine's week proposal): the daily
/// objective (Engine:TargetHoursPerDay, 8 h by default, zero on the weekend), the unclassified work and the work set aside.
/// </summary>
public sealed record SubmissionDraftContext(DateOnly WeekStart, IReadOnlyList<SubmissionDayContext> Days);
