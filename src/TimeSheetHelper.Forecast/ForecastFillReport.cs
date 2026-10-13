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
}

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
    public bool IsOk => Status is ForecastCellStatus.Ok or ForecastCellStatus.Planned;
}

public sealed class ForecastFillReport
{
    public required DateOnly WeekStart { get; init; }
    public required ForecastSubmitMode Mode { get; init; }
    public List<ForecastCellResult> Cells { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool Success => Cells.All(c => c.IsOk) && Cells.Count > 0;

    public string Summary =>
        $"Semaine du {WeekStart:yyyy-MM-dd} ({(Mode == ForecastSubmitMode.DryRun ? "simulation" : "saisie")}) : "
        + $"{Cells.Count(c => c.IsOk)}/{Cells.Count} cellules OK"
        + (Cells.Any(c => !c.IsOk) ? $", {Cells.Count(c => !c.IsOk)} en erreur" : "")
        + (Warnings.Count > 0 ? $", {Warnings.Count} avertissement(s)" : "");
}

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
        var weekEnd = draft.WeekStart.AddDays(7);
        var cells = new List<ForecastPlannedCell>();

        foreach (var group in draft.Entries.GroupBy(e => (e.ForecastTaskId, e.Date)))
        {
            var (taskId, date) = group.Key;
            if (date < draft.WeekStart || date >= weekEnd)
            {
                warnings?.Add($"Entrée du {date:yyyy-MM-dd} hors de la semaine du {draft.WeekStart:yyyy-MM-dd} : ignorée.");
                continue;
            }

            if (!tasks.TryGetValue(taskId, out var task))
            {
                warnings?.Add($"Tâche Forecast #{taskId} inconnue : entrée du {date:yyyy-MM-dd} ignorée.");
                continue;
            }

            var hours = ForecastHours.RoundToQuarter(group.Sum(e => e.Hours));
            if (hours <= 0)
                continue;

            var notes = string.Join(" ; ", group
                .Select(e => e.Notes?.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
            cells.Add(new ForecastPlannedCell(task, date, hours, notes.Length > 0 ? notes : null));
        }

        return cells
            .OrderBy(c => c.Task.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Task.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Date)
            .ToList();
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
