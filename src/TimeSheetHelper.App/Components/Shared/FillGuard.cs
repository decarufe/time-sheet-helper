using System.Globalization;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

public enum FillPreflight
{
    /// <summary>Everything the fill needs holds: it may write to Forecast.</summary>
    Allowed,

    /// <summary>The status does not allow the fill (no fresh simulation, blocking gap, operation running...): nothing to say, the button is disabled.</summary>
    NotAllowed,

    /// <summary>The draft is no longer the one that was simulated (changed, typed meanwhile, gone): the user must simulate again.</summary>
    DraftChanged,
}

/// <summary>
/// The rules that stand between the user and a write to the real Forecast timesheet, kept out of the Submit page so that they are tested:
/// whether the fill may start, whether the week may be marked as typed afterwards, and whether the result still belongs to the week on screen. Pure.
/// </summary>
public static class FillGuard
{
    public const string DraftChangedMessage = "Le brouillon a changé depuis la simulation : relancez la simulation avant de saisir.";

    /// <summary>Identity of the hours to type: a simulation only validates the exact same draft.</summary>
    public static string Fingerprint(TimesheetDraft draft) =>
        $"{draft.WeekStart:yyyy-MM-dd}|" + string.Join(";", draft.Entries
            .GroupBy(e => (e.ForecastTaskId, e.Date))
            .Select(g => (g.Key.ForecastTaskId, g.Key.Date, Hours: g.Sum(e => e.Hours)))
            .Where(c => c.Hours != 0)
            .OrderBy(c => c.ForecastTaskId).ThenBy(c => c.Date)
            .Select(c => $"{c.ForecastTaskId}@{c.Date:yyyyMMdd}={c.Hours.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>
    /// Whether the fill may start. <paramref name="canFill"/> is the status verdict; <paramref name="current"/> is the draft read from the
    /// store just before writing, which must be the exact draft the simulation validated.
    /// </summary>
    public static FillPreflight Preflight(bool canFill, TimesheetDraft? current, string? simulationFingerprint)
    {
        if (!canFill)
            return FillPreflight.NotAllowed;
        if (current is null || current.SubmittedUtc is not null || simulationFingerprint is null || Fingerprint(current) != simulationFingerprint)
            return FillPreflight.DraftChanged;
        return FillPreflight.Allowed;
    }

    /// <summary>
    /// Whether every planned cell of <paramref name="days"/> was verified in <paramref name="report"/>: a missing, partial or
    /// unverified cell leaves the scope open. <paramref name="days"/> null or empty: the whole week, as before — a fill scoped to
    /// fewer days than the draft plans can never satisfy this (the report then holds fewer cells than the whole-week plan), so the
    /// week is marked as typed only once every one of its days has been entered.
    /// </summary>
    /// <param name="ignoredTaskIds">Tasks left out of the fill, whose planned cells the report does not hold. Null: the flagged ones, like the writer.</param>
    public static bool IsWeekFullyEntered(
        ForecastFillReport report, TimesheetDraft draft, IReadOnlyDictionary<int, ForecastTask> tasks,
        IReadOnlyCollection<DateOnly>? days = null, IReadOnlyCollection<int>? ignoredTaskIds = null)
    {
        var ignored = ignoredTaskIds?.ToHashSet() ?? SubmissionTaskScope.AutoExcluded(tasks.Values).ToHashSet();
        var plan = ForecastFillPlanner.Plan(draft, tasks).Where(c => !ignored.Contains(c.Task.Id)).ToList();
        var expectedCells = (days is { Count: > 0 } scope ? plan.Where(c => scope.Contains(c.Date)) : plan).Count();
        return report.Cells.Count > 0
               && report.Cells.Count == expectedCells
               && report.Cells.All(c => c.Status == ForecastCellStatus.Ok);
    }

    /// <summary>The result of an operation started on <paramref name="operationWeek"/> may be shown only if the page still shows that week.</summary>
    public static bool ResultApplies(DateOnly operationWeek, DateOnly shownWeek, bool disposed) =>
        !disposed && operationWeek == shownWeek;

    /// <summary>A simulation does not start under a fill that is writing, on a draft already typed, with no day checked (the writer would take that for the whole week), or on a page that is gone.</summary>
    public static bool CanSimulate(bool hasDraft, bool draftSubmitted, bool disposed, bool fillRunning, bool hasSelection = true) =>
        hasDraft && !draftSubmitted && !disposed && !fillRunning && hasSelection;
}
