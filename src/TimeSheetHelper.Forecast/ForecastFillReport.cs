using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Forecast;

public enum ForecastSubmitMode
{
    /// <summary>Locate rows and cells, take screenshots and report what would be typed. Types nothing.</summary>
    DryRun,

    /// <summary>
    /// Type the values into the grid. Forecast saves each cell as a time registration when it loses focus
    /// (UNVERIFIED, no "Save" button is documented): Fill therefore IS the submission of the hours.
    /// It never sends the week for manager approval.
    /// </summary>
    Fill,
}

public enum ForecastCellStatus
{
    /// <summary>DryRun: the cell was located; <see cref="ForecastCellResult.Expected"/> would be typed.</summary>
    Planned,
    Ok,
    Mismatch,
    RowNotFound,
    CellNotFound,
    Error,
}

/// <summary>One (task, day) cell of the week.</summary>
public sealed record ForecastPlannedCell(ForecastTask Task, DateOnly Date, decimal Hours);

public sealed record ForecastCellResult(
    int ForecastTaskId,
    string TaskDisplayName,
    DateOnly Date,
    decimal Expected,
    decimal? Actual,
    string? ActualText,
    ForecastCellStatus Status,
    string? ScreenshotPath = null,
    string? Message = null)
{
    public bool IsOk => Status is ForecastCellStatus.Ok or ForecastCellStatus.Planned;
}

public sealed class ForecastFillReport
{
    public required DateOnly WeekStart { get; init; }
    public required ForecastSubmitMode Mode { get; init; }
    public List<ForecastCellResult> Cells { get; } = [];
    public List<string> Screenshots { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? OutputDirectory { get; set; }

    public bool Success => Cells.All(c => c.IsOk) && Cells.Count > 0;

    public string Summary =>
        $"Semaine du {WeekStart:yyyy-MM-dd} ({(Mode == ForecastSubmitMode.DryRun ? "simulation" : "saisie")}) : "
        + $"{Cells.Count(c => c.IsOk)}/{Cells.Count} cellules OK"
        + (Cells.Any(c => !c.IsOk) ? $", {Cells.Count(c => !c.IsOk)} en erreur" : "")
        + (Warnings.Count > 0 ? $", {Warnings.Count} avertissement(s)" : "");
}

/// <summary>Pure logic of the fill: what to type, and whether what the grid shows matches.</summary>
public static class ForecastFillPlanner
{
    /// <summary>Tolerance when comparing hours read back from the grid (display rounding).</summary>
    public const decimal Tolerance = 0.01m;

    /// <summary>
    /// Sums the draft entries per (task, day), rounds to the quarter hour and drops zero cells.
    /// Entries outside the draft week or referencing an unknown task are reported in <paramref name="warnings"/>.
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
            if (hours > 0)
                cells.Add(new ForecastPlannedCell(task, date, hours));
        }

        return cells
            .OrderBy(c => c.Task.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Task.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Date)
            .ToList();
    }

    /// <summary>Compares the expected hours with the text read back from the grid.</summary>
    public static (ForecastCellStatus Status, decimal? Actual, string? Message) Compare(decimal expected, string? actualText)
    {
        if (!ForecastHours.TryParse(actualText, out var actual))
            return (ForecastCellStatus.Mismatch, null, $"Valeur illisible dans la grille : « {actualText} ».");

        return Math.Abs(actual - expected) <= Tolerance
            ? (ForecastCellStatus.Ok, actual, null)
            : (ForecastCellStatus.Mismatch, actual, $"Attendu {expected:0.##} h, lu {actual:0.##} h.");
    }
}
