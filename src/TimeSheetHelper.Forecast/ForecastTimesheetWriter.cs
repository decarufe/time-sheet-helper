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
/// Persistence (UNVERIFIED): the Forecast help center describes typing hours directly into the grid cell of a task
/// and documents no "Save" button, i.e. each cell is saved as a time registration when it loses focus. Therefore
/// <see cref="ForecastSubmitMode.Fill"/> IS the submission of the hours. The read-back reloads the page so that only
/// values actually persisted by Forecast are reported as OK. Sending the week for manager approval is a separate,
/// explicit step (<see cref="SubmitForApprovalAsync"/>), never done here.
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
            await VerifyAsync(page, draft.WeekStart, pending, dir, report, cancellationToken);

        await ScreenshotAsync(page, dir, "apres", report);
        logger.LogInformation("{Summary}", report.Summary);
        return report;
    }

    /// <summary>
    /// Sends the week for manager approval. TODO: implement once the "Submit" button and its confirmation dialog
    /// have been verified with "forecast discover" (see <see cref="ForecastSelectors.SubmitForApprovalButton"/>).
    /// Deliberately not implemented: submitting locks the week and cannot be undone by the user.
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
        var grid = ForecastSelectors.WeekGrid(page);
        var row = await FindRowAsync(grid, task);

        if (row is null && mode == ForecastSubmitMode.DryRun)
        {
            foreach (var cell in cells)
                report.Cells.Add(Result(cell, ForecastCellStatus.Planned, null, null, null,
                    $"Ligne absente : serait ajoutée, puis « {Format(cell.Hours)} » saisi."));
            return;
        }

        if (row is null)
        {
            if (await AddTaskRowAsync(page, task))
                row = await FindRowAsync(ForecastSelectors.WeekGrid(page), task);

            if (row is null)
            {
                var shot = await ScreenshotAsync(page, dir, $"ligne-introuvable-{task.Id}", report);
                foreach (var cell in cells)
                    report.Cells.Add(Result(cell, ForecastCellStatus.RowNotFound, null, null, shot,
                        $"Ligne « {task.DisplayName} » introuvable et impossible à ajouter."));
                return;
            }
        }

        var days = ForecastWeekGrid.DayColumns(await ForecastSelectors.ColumnHeaders(grid).AllInnerTextsAsync(), report.WeekStart);
        var inputs = ForecastSelectors.HourInputs(row);
        var inputCount = await inputs.CountAsync();
        if (inputCount != days.Count)
            report.Warnings.Add($"« {task.DisplayName} » : {inputCount} champ(s) pour {days.Count} jour(s) affiché(s).");

        var rowShot = Path.Combine(dir, $"ligne-{task.Id}.png");
        foreach (var cell in cells)
        {
            var index = days.ToList().IndexOf(cell.Date);
            if (index < 0 || index >= inputCount)
            {
                report.Cells.Add(Result(cell, ForecastCellStatus.CellNotFound, null, null, null,
                    $"Pas de cellule pour le {cell.Date:ddd d MMM} (jour masqué ou en-têtes non reconnus)."));
                continue;
            }

            var input = inputs.Nth(index);
            var current = await ReadCellAsync(input);
            if (mode == ForecastSubmitMode.DryRun)
            {
                report.Cells.Add(Result(cell, ForecastCellStatus.Planned, Parse(current), current, rowShot,
                    $"Actuel « {current} » → à saisir « {Format(cell.Hours)} »."));
                continue;
            }

            await input.ClickAsync();
            await input.PressAsync("Control+A");
            await input.PressSequentiallyAsync(Format(cell.Hours), new() { Delay = 30 });
            // Blur commits the cell (autosave).
            await input.PressAsync("Tab");
            await ForecastSession.WaitForIdleAsync(page, 5000);
            pending.Add(cell);
        }

        try
        {
            await row.ScreenshotAsync(new() { Path = rowShot });
        }
        catch (PlaywrightException)
        {
            // Screenshot is best effort.
        }
    }

    /// <summary>Reloads the week and compares every written cell (and the other days of those rows) with the plan.</summary>
    private async Task VerifyAsync(
        IPage page, DateOnly weekStart, IReadOnlyList<ForecastPlannedCell> written, string dir,
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

        var grid = ForecastSelectors.WeekGrid(page);
        var days = ForecastWeekGrid.DayColumns(await ForecastSelectors.ColumnHeaders(grid).AllInnerTextsAsync(), weekStart);

        foreach (var taskCells in written.GroupBy(c => c.Task.Id))
        {
            var task = taskCells.First().Task;
            var row = await FindRowAsync(grid, task);
            var shot = Path.Combine(dir, $"relecture-{task.Id}.png");
            if (row is null)
            {
                foreach (var cell in taskCells)
                    report.Cells.Add(Result(cell, ForecastCellStatus.RowNotFound, null, null, null, "Ligne disparue après rechargement."));
                continue;
            }

            try
            {
                await row.ScreenshotAsync(new() { Path = shot });
            }
            catch (PlaywrightException)
            {
                shot = null;
            }

            var inputs = ForecastSelectors.HourInputs(row);
            var expectedByDate = taskCells.ToDictionary(c => c.Date, c => c.Hours);
            for (var i = 0; i < days.Count; i++)
            {
                var date = days[i];
                var text = await ReadCellAsync(inputs.Nth(i));
                var expected = expectedByDate.GetValueOrDefault(date);
                var cell = new ForecastPlannedCell(task, date, expected);
                var (status, actual, message) = ForecastFillPlanner.Compare(expected, text);

                if (expected == 0 && status == ForecastCellStatus.Ok)
                    continue; // Empty day, nothing planned: not worth reporting.
                if (expected == 0)
                    message = $"Valeur déjà présente dans Forecast et absente du brouillon (non effacée) : « {text} ».";

                report.Cells.Add(Result(cell, status, actual, text, shot, message));
            }
        }
    }

    /// <summary>
    /// Shows the week starting at <paramref name="weekStart"/>: "Today", then previous/next week clicks,
    /// verified with the column headers (day of month).
    /// </summary>
    private async Task<bool> NavigateToWeekAsync(IPage page, DateOnly weekStart, CancellationToken cancellationToken)
    {
        var toggle = ForecastSelectors.WeekViewToggle(page);
        if (await ForecastSession.IsVisibleAsync(toggle))
            await toggle.First.ClickAsync();

        if (await IsShowingAsync(page, weekStart))
            return true;

        var today = ForecastSelectors.TodayButton(page);
        if (await ForecastSession.IsVisibleAsync(today))
        {
            await today.First.ClickAsync();
            await ForecastSession.WaitForIdleAsync(page, 5000);
        }

        var currentMonday = ForecastDiscovery.MondayOf(DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime));
        var weeks = (weekStart.DayNumber - currentMonday.DayNumber) / 7;
        var button = weeks < 0 ? ForecastSelectors.PreviousWeekButton(page) : ForecastSelectors.NextWeekButton(page);

        for (var i = 0; i < Math.Min(Math.Abs(weeks), MaxWeekNavigationClicks); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await button.First.ClickAsync();
            await ForecastSession.WaitForIdleAsync(page, 3000);
        }

        return await IsShowingAsync(page, weekStart);
    }

    private static async Task<bool> IsShowingAsync(IPage page, DateOnly weekStart)
    {
        var grid = ForecastSelectors.WeekGrid(page);
        if (!await ForecastSession.IsVisibleAsync(grid))
            return false;
        var headers = await ForecastSelectors.ColumnHeaders(grid).AllInnerTextsAsync();
        return ForecastWeekGrid.IsShowingWeek(headers, weekStart);
    }

    private static async Task<ILocator?> FindRowAsync(ILocator grid, ForecastTask task)
    {
        var rows = ForecastSelectors.Rows(grid);
        var index = ForecastWeekGrid.FindRow(await rows.AllInnerTextsAsync(), task);
        return index is null ? null : rows.Nth(index.Value);
    }

    /// <summary>Adds the task to the week through the task search (UNVERIFIED flow: "Add task" then pick the option).</summary>
    private async Task<bool> AddTaskRowAsync(IPage page, ForecastTask task)
    {
        var addRow = ForecastSelectors.AddRowButton(page);
        if (await ForecastSession.IsVisibleAsync(addRow))
            await addRow.First.ClickAsync();

        var search = ForecastSelectors.TaskSearchInput(page);
        if (!await ForecastSession.IsVisibleAsync(search))
            return false;

        await search.First.FillAsync(task.TaskName);
        await ForecastSession.WaitForIdleAsync(page, 5000);

        var options = ForecastSelectors.SearchOptions(page);
        var index = ForecastWeekGrid.FindRow(await options.AllInnerTextsAsync(), task);
        if (index is null)
        {
            logger.LogWarning("Aucune option de recherche ne correspond à {Task}.", task.DisplayName);
            await page.Keyboard.PressAsync("Escape");
            return false;
        }

        await options.Nth(index.Value).ClickAsync();
        await ForecastSession.WaitForIdleAsync(page, 5000);
        return true;
    }

    /// <summary>Text of an hour cell (input value or editable text), null when unreadable.</summary>
    internal static async Task<string?> ReadCellAsync(ILocator cell)
    {
        try
        {
            var isInput = await cell.EvaluateAsync<bool>("e => e instanceof HTMLInputElement || e instanceof HTMLTextAreaElement",
                null, new() { Timeout = 2000 });
            return isInput
                ? await cell.InputValueAsync(new() { Timeout = 2000 })
                : await cell.InnerTextAsync(new() { Timeout = 2000 });
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return null;
        }
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
