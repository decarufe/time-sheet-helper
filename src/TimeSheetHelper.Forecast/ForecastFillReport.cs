using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Forecast;

public enum ForecastSubmitMode
{
    /// <summary>Read what Forecast already holds and report what would be registered. Registers nothing.</summary>
    DryRun,

    /// <summary>
    /// Register the hours in Forecast through the MCP server (one time registration per task and day), then read the
    /// week back. It never sends the week for manager approval.
    /// </summary>
    Fill,
}

public enum ForecastCellStatus
{
    /// <summary>DryRun: the cell can be registered; <see cref="ForecastCellResult.Expected"/> would be added.</summary>
    Planned,
    Ok,
    Mismatch,

    /// <summary>The task is not known to Forecast under this code or name.</summary>
    RowNotFound,
    Error,

    /// <summary>What Forecast holds for the cell could not be established (read incomplete, outcome of a write unknown).</summary>
    Unknown,

    /// <summary>The hours of an existing registration were changed (and read back).</summary>
    Updated,

    /// <summary>An existing registration was removed (and is no longer read).</summary>
    Deleted,

    /// <summary>Forecast already held the requested state: nothing was written (the operation is idempotent).</summary>
    Skipped,
}

/// <summary>Whether the Forecast server offers a kind of write.</summary>
public enum ForecastCapability
{
    Available,
    Unavailable,
}

/// <summary>
/// The writes the Forecast server offers. Forecast-mcp registers time but cannot change the hours of a registration nor remove one:
/// <see cref="Update"/> and <see cref="Delete"/> are then <see cref="ForecastCapability.Unavailable"/> and <see cref="UnavailableReason"/> says why.
/// </summary>
public sealed record ForecastCapabilities(
    ForecastCapability Create, ForecastCapability Update, ForecastCapability Delete, string? UnavailableReason)
{
    /// <summary>Not connected yet: nothing is assumed available.</summary>
    public static ForecastCapabilities Unknown { get; } = new(
        ForecastCapability.Unavailable, ForecastCapability.Unavailable, ForecastCapability.Unavailable,
        "Capacités de Forecast inconnues : pas encore de connexion.");
}

/// <summary>Why a cell is in the state it is in, when its status alone does not say.</summary>
public enum ForecastCellIssue
{
    None,

    /// <summary>Several tasks of Forecast bear this name: the right one cannot be chosen.</summary>
    TaskAmbiguous,

    /// <summary>The draft refers to a task the local catalogue does not know (deleted).</summary>
    TaskUnknownLocally,

    /// <summary>A previous cell stopped the fill (timeout, network): this one was not tried.</summary>
    NotAttempted,

    WriteRefused,
    WriteFailed,

    /// <summary>The server could not say whether the registration was applied.</summary>
    OutcomeUnknown,

    /// <summary>The week could not be read whole: nothing can be compared.</summary>
    ReadIncomplete,
}

/// <summary>State of the registration of a cell in Forecast after a fill (or a simulation).</summary>
public enum ForecastEntryState
{
    /// <summary>Simulation: nothing was tried.</summary>
    NotApplicable,

    /// <summary>The hours are in Forecast (registered now, or already there).</summary>
    Entered,

    /// <summary>Nothing was registered for this cell.</summary>
    NotEntered,

    /// <summary>It is not known whether the hours are in Forecast.</summary>
    Unknown,
}

/// <summary>How precisely the cell was compared with what Forecast holds.</summary>
public enum ForecastComparisonLevel
{
    /// <summary>Nothing in Forecast to compare with.</summary>
    None,

    TaskAndDay,

    /// <summary>
    /// By project and day: the registrations of "get_time_analysis" carry an internal task id that matches no task number, so
    /// the hours of another task of the same project are indistinguishable.
    /// </summary>
    ProjectAndDay,
}

public enum ForecastFindingKind
{
    /// <summary>A warning of the plan step of a registration.</summary>
    PlanWarning,
    TaskInactive,
    TaskClosed,
    TaskBlocked,
    ProjectClosed,
    EstimateExceeded,

    /// <summary>Registrations of the day are submitted for approval.</summary>
    PendingApproval,
}

/// <summary>Something noticed about a cell that does not stop it (read-only checks, warnings of Forecast).</summary>
public sealed record ForecastCellFinding(ForecastFindingKind Kind, string Message);

public enum ForecastCellAction
{
    /// <summary>Nothing registered for this task and day: the hours are added.</summary>
    Type,

    /// <summary>Forecast already holds the expected hours.</summary>
    AlreadyDone,

    /// <summary>Forecast holds other hours: left untouched.</summary>
    Conflict,
}

/// <summary>One (task, day) cell of the week.</summary>
public sealed record ForecastPlannedCell(ForecastTask Task, DateOnly Date, decimal Hours, string? Notes = null);

public sealed record ForecastCellResult(
    int ForecastTaskId,
    string TaskDisplayName,
    DateOnly Date,
    decimal Expected,
    decimal? Actual,
    ForecastCellStatus Status,
    string? Message = null)
{
    public bool IsOk => Status is ForecastCellStatus.Ok or ForecastCellStatus.Planned
        or ForecastCellStatus.Updated or ForecastCellStatus.Deleted or ForecastCellStatus.Skipped;

    public ForecastCellIssue Issue { get; init; }

    public ForecastEntryState EntryState { get; init; }

    public ForecastComparisonLevel Comparison { get; init; }

    /// <summary>Cells of one project and day share the hours Forecast holds for them: they share this key.</summary>
    public string? GroupKey { get; init; }

    /// <summary>Hours expected for the whole group when it has several cells (the comparison is made on the group).</summary>
    public decimal? GroupExpected { get; init; }

    /// <summary>The registrations of Forecast the cell was compared with.</summary>
    public IReadOnlyList<ForecastRemoteEntry> ExistingEntries { get; init; } = [];

    public IReadOnlyList<ForecastCellFinding> Findings { get; init; } = [];
}

public sealed class ForecastFillReport
{
    public required DateOnly WeekStart { get; init; }
    public required ForecastSubmitMode Mode { get; init; }
    public List<ForecastCellResult> Cells { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Forecast could not be asked (tool missing, error, timeout, network): every cell carries this message.</summary>
    public string? GlobalError { get; set; }

    /// <summary>The registrations of the week could not be read whole: no cell was compared, nothing was registered.</summary>
    public bool ReadTruncated { get; set; }

    public string? ReadDetail { get; set; }

    /// <summary>The fill stopped before the end (timeout, network, cancellation): see <see cref="InterruptionMessage"/>.</summary>
    public bool Interrupted { get; set; }

    public string? InterruptionMessage { get; set; }

    /// <summary>Checks that could not be made (tool missing, estimate in points, error): what the read-only checks left out.</summary>
    public List<string> PreflightLimits { get; } = [];

    /// <summary>
    /// Registrations of the week that Forecast holds without a project (internal or idle time). The application does not write them
    /// (the tool that registers time needs a project or a task): they are listed so that the page shows them as left out.
    /// </summary>
    public List<ForecastRemoteEntry> NonProjectEntries { get; } = [];

    /// <summary>Hours of the draft left out of this fill because their task is ignored (flagged, or ignored by the user for this submission): they are not sent.</summary>
    public List<SubmissionIgnoredCell> IgnoredCells { get; } = [];

    public bool Success => Cells.All(c => c.IsOk) && Cells.Count > 0;

    /// <summary>Some cells are in Forecast and others are not: running the fill again registers only what is missing.</summary>
    public bool IsPartial =>
        Mode == ForecastSubmitMode.Fill
        && Cells.Any(c => c.EntryState == ForecastEntryState.Entered)
        && Cells.Any(c => c.EntryState != ForecastEntryState.Entered);

    public string Summary =>
        $"Semaine du {WeekStart:yyyy-MM-dd} ({(Mode == ForecastSubmitMode.DryRun ? "simulation" : "saisie")}) : "
        + $"{Cells.Count(c => c.IsOk)}/{Cells.Count} cellules OK"
        + (Cells.Any(c => !c.IsOk) ? $", {Cells.Count(c => !c.IsOk)} en erreur" : "")
        + (Warnings.Count > 0 ? $", {Warnings.Count} avertissement(s)" : "")
        + (ReadTruncated ? ", lecture de Forecast incomplète" : "")
        + (Interrupted ? ", saisie interrompue" : "");
}

/// <summary>A draft entry the plan could not turn into a cell: its task is unknown locally.</summary>
public sealed record ForecastRejectedEntry(int ForecastTaskId, DateOnly Date, decimal Hours);

/// <summary>The cells to register, and the entries that could not become one.</summary>
public sealed record ForecastFillPlan(IReadOnlyList<ForecastPlannedCell> Cells, IReadOnlyList<ForecastRejectedEntry> Rejected);

/// <summary>Pure logic of the fill: what to register, and whether what Forecast holds matches.</summary>
public static class ForecastFillPlanner
{
    /// <summary>Tolerance when comparing hours read back from Forecast (rounding of minutes).</summary>
    public const decimal Tolerance = 0.01m;

    /// <summary>
    /// Sums the draft entries per (task, day), rounds to the quarter hour and drops zero cells; the notes of the
    /// entries of a cell are joined. Entries outside the draft week or referencing an unknown task are reported in
    /// <paramref name="warnings"/>.
    /// </summary>
    public static IReadOnlyList<ForecastPlannedCell> Plan(
        TimesheetDraft draft, IReadOnlyDictionary<int, ForecastTask> tasks, ICollection<string>? warnings = null)
    {
        var plan = PlanDetailed(draft, tasks, warnings);
        foreach (var rejected in plan.Rejected)
            warnings?.Add($"Tâche Forecast #{rejected.ForecastTaskId} inconnue : entrée du {rejected.Date:yyyy-MM-dd} ignorée.");
        return plan.Cells;
    }

    /// <summary>
    /// Same as <see cref="Plan"/>, but the entries of a task the catalogue does not know are returned in
    /// <see cref="ForecastFillPlan.Rejected"/> instead of being dropped: a cell that silently vanishes is a gap, not a success.
    /// Entries outside the week are still only reported in <paramref name="warnings"/>.
    /// </summary>
    public static ForecastFillPlan PlanDetailed(
        TimesheetDraft draft, IReadOnlyDictionary<int, ForecastTask> tasks, ICollection<string>? warnings = null)
    {
        var weekEnd = draft.WeekStart.AddDays(7);
        var cells = new List<ForecastPlannedCell>();
        var rejected = new List<ForecastRejectedEntry>();

        foreach (var group in draft.Entries.GroupBy(e => (e.ForecastTaskId, e.Date)))
        {
            var (taskId, date) = group.Key;
            if (date < draft.WeekStart || date >= weekEnd)
            {
                warnings?.Add($"Entrée du {date:yyyy-MM-dd} hors de la semaine du {draft.WeekStart:yyyy-MM-dd} : ignorée.");
                continue;
            }

            var hours = ForecastHours.RoundToQuarter(group.Sum(e => e.Hours));
            if (!tasks.TryGetValue(taskId, out var task))
            {
                if (hours > 0)
                    rejected.Add(new ForecastRejectedEntry(taskId, date, hours));
                continue;
            }

            if (hours <= 0)
                continue;

            var notes = string.Join(" ; ", group
                .Select(e => e.Notes?.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
            cells.Add(new ForecastPlannedCell(task, date, hours, notes.Length > 0 ? notes : null));
        }

        return new ForecastFillPlan(
            cells
                .OrderBy(c => c.Task.ProjectName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Task.TaskName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Date)
                .ToList(),
            rejected.OrderBy(r => r.ForecastTaskId).ThenBy(r => r.Date).ToList());
    }

    /// <summary>
    /// What to do with a cell given the hours Forecast already holds for that task and day. Creating a registration
    /// adds to what exists, so a cell holding other hours is left untouched and reported: the user corrects it in Forecast.
    /// </summary>
    public static (ForecastCellAction Action, string Message) Decide(decimal expected, decimal current)
    {
        if (current <= 0)
            return (ForecastCellAction.Type, $"Rien d'enregistré → {expected:0.##} h seront ajoutées.");

        if (Math.Abs(current - expected) <= Tolerance)
            return (ForecastCellAction.AlreadyDone, $"Déjà {current:0.##} h dans Forecast : rien à faire.");

        return (ForecastCellAction.Conflict,
            $"Forecast contient déjà {current:0.##} h (prévu {expected:0.##} h) : non modifié, car un nouvel enregistrement s'ajouterait au lieu de remplacer. Corrigez dans Forecast.");
    }

    /// <summary>Compares the expected hours with what Forecast holds after the registration.</summary>
    public static (ForecastCellStatus Status, decimal? Actual, string? Message) Compare(decimal expected, decimal actual) =>
        Math.Abs(actual - expected) <= Tolerance
            ? (ForecastCellStatus.Ok, actual, null)
            : (ForecastCellStatus.Mismatch, actual, $"Attendu {expected:0.##} h, lu {actual:0.##} h.");
}
