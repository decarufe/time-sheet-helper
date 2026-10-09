using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Types a <see cref="TimesheetDraft"/> into the Forecast weekly grid and reads it back.
///
/// Grid and cell popover are VERIFIED (see <see cref="ForecastTimesheetDom"/> and <see cref="ForecastSelectors.HourPopup"/>):
/// a day cell opens a popover whose "Add" button creates a time entry. Only empty cells are typed into; cells that
/// already hold other hours are reported, never changed (see <see cref="ForecastFillPlanner.Decide"/>). The read-back
/// reloads the page so that only values actually persisted by Forecast are reported as OK.
/// Sending the week for approval is never done here.
/// </summary>
public sealed class ForecastTimesheetWriter(
    ForecastSession session,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<ForecastTimesheetWriter> logger)
{
    private const int MaxWeekNavigationClicks = 104;

    public async Task<ForecastFillReport> FillWeekAsync(
        TimesheetDraft draft, ForecastSubmitMode mode, CancellationToken cancellationToken = default)
    {
        var report = new ForecastFillReport { WeekStart = draft.WeekStart, Mode = mode };
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(DataPaths.Root, "forecast-fill",
            $"{draft.WeekStart:yyyy-MM-dd}_{stamp}_{(mode == ForecastSubmitMode.DryRun ? "simulation" : "saisie")}");
        Directory.CreateDirectory(dir);
        report.OutputDirectory = dir;

        var plan = await PlanAsync(draft, report.Warnings, cancellationToken);
        if (plan.Count == 0)
        {
            report.Warnings.Add("Aucune heure à saisir dans ce brouillon.");
            return report;
        }

        var page = await session.EnsureLoggedInAsync(cancellationToken);
        await session.GotoAsync(page, session.Options.TimesheetsPath);
        if (!await NavigateToWeekAsync(page, draft.WeekStart, cancellationToken))
        {
            var shot = await ScreenshotAsync(page, dir, "semaine-introuvable", report);
            foreach (var cell in plan)
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, null, shot,
                    "Impossible d'afficher la semaine dans Forecast (navigation ou en-têtes à vérifier)."));
            return report;
        }

        await ScreenshotAsync(page, dir, "avant", report);

        var pending = new List<ForecastPlannedCell>();
        foreach (var taskCells in plan.GroupBy(c => c.Task.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = taskCells.First().Task;
            try
            {
                await WriteTaskAsync(page, task, taskCells.ToList(), mode, dir, report, pending);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                logger.LogWarning(ex, "Échec sur la tâche {Task}", task.DisplayName);
                var shot = await ScreenshotAsync(page, dir, $"erreur-{task.Id}", report);
                foreach (var cell in taskCells)
                    report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, null, shot, ex.Message.Split('\n')[0]));
            }
        }

        if (mode == ForecastSubmitMode.Fill && pending.Count > 0)
            await VerifyAsync(page, draft.WeekStart, pending, report, cancellationToken);

        await ScreenshotAsync(page, dir, "apres", report);
        logger.LogInformation("{Summary}", report.Summary);
        return report;
    }

    /// <summary>
    /// Sends the week for manager approval. Deliberately not implemented: the button was not found in the capture
    /// and submitting locks the week.
    /// </summary>
    public Task SubmitForApprovalAsync(DateOnly weekStart, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "TODO: soumission pour approbation non implémentée tant que les sélecteurs ne sont pas vérifiés.");

    private async Task<IReadOnlyList<ForecastPlannedCell>> PlanAsync(
        TimesheetDraft draft, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        var ids = draft.Entries.Select(e => e.ForecastTaskId).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.ForecastTasks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        return ForecastFillPlanner.Plan(draft, tasks, warnings);
    }

    private async Task WriteTaskAsync(
        IPage page, ForecastTask task, IReadOnlyList<ForecastPlannedCell> cells, ForecastSubmitMode mode,
        string dir, ForecastFillReport report, List<ForecastPlannedCell> pending)
    {
        var (row, headers) = await FindRowAsync(page, task);
        if (row is null)
        {
            var status = mode == ForecastSubmitMode.DryRun ? ForecastCellStatus.Planned : ForecastCellStatus.RowNotFound;
            foreach (var cell in cells)
                report.Cells.Add(Result(cell, status, null, null, null,
                    $"« {task.DisplayName} » n'est ni dans l'onglet Timesheets ni dans Find Tasks : ajoutez-la dans Forecast "
                    + "(Add Timesheet Entry) ou en favori, puis relancez."));
            return;
        }

        var days = ForecastTimesheetDom.HeaderDates(headers, report.WeekStart);
        foreach (var cell in cells)
        {
            var index = days.ToList().IndexOf(cell.Date);
            if (index < 0 || index >= row.Hours.Count)
            {
                report.Cells.Add(Result(cell, ForecastCellStatus.CellNotFound, null, null, null,
                    $"Pas de cellule pour le {cell.Date:ddd d MMM} (en-têtes : {string.Join(", ", headers)})."));
                continue;
            }

            var current = row.Hours[index];
            var (action, message) = ForecastFillPlanner.Decide(cell.Hours, current, Format(cell.Hours));
            if (mode == ForecastSubmitMode.DryRun || action != ForecastCellAction.Type)
            {
                var status = action switch
                {
                    ForecastCellAction.AlreadyDone => ForecastCellStatus.Ok,
                    ForecastCellAction.Conflict => ForecastCellStatus.Mismatch,
                    _ => ForecastCellStatus.Planned,
                };
                report.Cells.Add(Result(cell, status, Parse(current), current, null, message));
                continue;
            }

            await ForecastSelectors.DayCells(page, row.Index).Nth(index).ClickAsync();
            var input = ForecastSelectors.HourPopupInput(page);
            try
            {
                await input.WaitForAsync(new() { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
                var shot = await ScreenshotAsync(page, dir, $"popup-introuvable-{task.Id}", report);
                await page.Keyboard.PressAsync("Escape");
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, null, shot,
                    "Le popup de saisie ne s'est pas ouvert : relancez « forecast discover »."));
                continue;
            }

            await input.FillAsync(Format(cell.Hours));
            await ForecastSelectors.HourPopupAddButton(page).ClickAsync();
            await ForecastSelectors.HourPopup(page).WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5000 });
            await ForecastSession.WaitForIdleAsync(page, 5000);
            pending.Add(cell);
        }
    }

    /// <summary>Reloads the week and compares every written cell (and the other days of those rows) with the plan.</summary>
    private async Task VerifyAsync(
        IPage page, DateOnly weekStart, IReadOnlyList<ForecastPlannedCell> written,
        ForecastFillReport report, CancellationToken cancellationToken)
    {
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await ForecastSession.WaitForIdleAsync(page);
        if (!await NavigateToWeekAsync(page, weekStart, cancellationToken))
        {
            report.Warnings.Add("Relecture impossible : la semaine n'a pas pu être réaffichée.");
            foreach (var cell in written)
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, null, null, "Saisi mais non relu."));
            return;
        }

        foreach (var taskCells in written.GroupBy(c => c.Task.Id))
        {
            var task = taskCells.First().Task;
            var (row, headers) = await FindRowAsync(page, task);
            if (row is null)
            {
                foreach (var cell in taskCells)
                    report.Cells.Add(Result(cell, ForecastCellStatus.RowNotFound, null, null, null, "Ligne disparue après rechargement."));
                continue;
            }

            var days = ForecastTimesheetDom.HeaderDates(headers, weekStart);
            var expectedByDate = taskCells.ToDictionary(c => c.Date, c => c.Hours);
            for (var i = 0; i < Math.Min(days.Count, row.Hours.Count); i++)
            {
                if (days[i] is not { } date)
                    continue;
                // Cells already settled before typing (already done, conflict) are reported once.
                if (!expectedByDate.ContainsKey(date) && report.Cells.Any(c => c.ForecastTaskId == task.Id && c.Date == date))
                    continue;
                var text = row.Hours[i];
                var expected = expectedByDate.GetValueOrDefault(date);
                var cell = new ForecastPlannedCell(task, date, expected);
                var (status, actual, message) = ForecastFillPlanner.Compare(expected, text);

                if (expected == 0 && status == ForecastCellStatus.Ok)
                    continue; // Empty day, nothing planned: not worth reporting.
                if (expected == 0)
                    message = $"Valeur déjà présente dans Forecast et absente du brouillon (non effacée) : « {text} ».";

                report.Cells.Add(Result(cell, status, actual, text, null, message));
            }
        }
    }

    /// <summary>The task row, looked up in the "Timesheets" tab first, then in "Find Tasks".</summary>
    private static async Task<(ForecastGridRow? Row, IReadOnlyList<string> Headers)> FindRowAsync(IPage page, ForecastTask task)
    {
        IReadOnlyList<string> headers = [];
        foreach (var tab in new[] { ForecastSelectors.TimesheetTab(page), ForecastSelectors.FindTasksTab(page) })
        {
            if (await ForecastSession.IsVisibleAsync(tab))
            {
                await tab.First.ClickAsync();
                await ForecastSession.WaitForIdleAsync(page, 5000);
            }
            var snapshot = await ForecastTimesheetDom.ReadAsync(page);
            headers = snapshot.DayHeaders;
            if (ForecastTimesheetDom.FindRow(snapshot.Rows, task) is { } row)
                return (row, headers);
        }
        return (null, headers);
    }

    /// <summary>Shows the week of <paramref name="weekStart"/> with the arrows, checked against the day headers.</summary>
    private static async Task<bool> NavigateToWeekAsync(IPage page, DateOnly weekStart, CancellationToken cancellationToken)
    {
        var toggle = ForecastSelectors.WeekViewToggle(page);
        if (await ForecastSession.IsVisibleAsync(toggle))
        {
            await toggle.First.ClickAsync();
            await ForecastSession.WaitForIdleAsync(page, 3000);
        }

        for (var i = 0; i <= MaxWeekNavigationClicks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headers = (await ForecastTimesheetDom.ReadAsync(page)).DayHeaders;
            if (ForecastTimesheetDom.IsShowingWeek(headers, weekStart))
                return true;

            var shown = ForecastTimesheetDom.HeaderDates(headers, weekStart).OfType<DateOnly>().ToList();
            if (shown.Count == 0)
                return false;

            var button = shown[0] > weekStart ? ForecastSelectors.PreviousWeekButton(page) : ForecastSelectors.NextWeekButton(page);
            await button.First.ClickAsync();
            await ForecastSession.WaitForIdleAsync(page, 3000);
        }
        return false;
    }

    private string Format(decimal hours) => ForecastHours.Format(hours, session.Options.HourFormat);

    private static decimal? Parse(string? text) => ForecastHours.TryParse(text, out var h) ? h : null;

    private static ForecastCellResult Result(
        ForecastPlannedCell cell, ForecastCellStatus status, decimal? actual, string? actualText, string? screenshot, string? message) =>
        new(cell.Task.Id, cell.Task.DisplayName, cell.Date, cell.Hours, actual, actualText, status, screenshot, message);

    private static async Task<string?> ScreenshotAsync(IPage page, string dir, string name, ForecastFillReport report)
    {
        var path = Path.Combine(dir, $"{name}.png");
        try
        {
            await page.ScreenshotAsync(new() { Path = path, FullPage = true });
            report.Screenshots.Add(path);
            return path;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }
}
