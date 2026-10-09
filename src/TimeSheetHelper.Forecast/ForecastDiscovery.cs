using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Diagnostic run used to fix <see cref="ForecastSelectors"/>: captures ARIA snapshots, HTML and screenshots of the
/// timesheet page, the week grid, the "add time entry" dialog, the task search, the previous week and "My Work",
/// plus how many elements each selector matches and the URLs of the network calls (no bodies).
/// It never saves anything in Forecast: dialogs are closed with Cancel/Escape, the search box is only typed into.
/// </summary>
public sealed class ForecastDiscovery(ForecastSession session, TimeProvider timeProvider, ILogger<ForecastDiscovery> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Runs the discovery and returns the output folder.</summary>
    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(DataPaths.Root, "forecast-discovery", stamp);
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        var network = new List<string>();

        void Note(string message)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            logger.LogInformation("{Message}", message);
        }

        var page = await session.EnsureLoggedInAsync(cancellationToken);
        page.Request += (_, r) =>
        {
            if (r.ResourceType is "xhr" or "fetch" or "document" or "websocket")
                lock (network) network.Add($"{DateTime.Now:HH:mm:ss.fff} {r.Method} {r.ResourceType} {r.Url}");
        };

        var weekStart = MondayOf(DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime));

        await StepAsync("01-timesheets", Note, async () =>
        {
            await session.GotoAsync(page, session.Options.TimesheetsPath);
            await Task.Delay(1500, cancellationToken);
            await CapturePageAsync(page, dir, "01-timesheets");
            await WriteProbesAsync(page, Path.Combine(dir, "01-timesheets.probes.txt"));
        });

        await StepAsync("02-week-grid", Note, async () =>
        {
            var grid = ForecastSelectors.WeekGrid(page);
            if (!await ForecastSession.IsVisibleAsync(grid))
            {
                Note("Grille de la semaine introuvable avec ForecastSelectors.WeekGrid.");
                return;
            }

            await CaptureElementAsync(grid, dir, "02-week-grid");
            var headers = await ForecastSelectors.ColumnHeaders(grid).AllInnerTextsAsync();
            var rows = await ForecastSelectors.Rows(grid).AllInnerTextsAsync();
            var inputs = ForecastSelectors.HourInputs(grid);
            var inputValues = new List<string>();
            for (var i = 0; i < Math.Min(await inputs.CountAsync(), 100); i++)
                inputValues.Add(await ForecastTimesheetWriter.ReadCellAsync(inputs.Nth(i)) ?? "<non lisible>");

            await File.WriteAllTextAsync(Path.Combine(dir, "02-week-grid.json"), JsonSerializer.Serialize(new
            {
                weekStartAssumed = weekStart.ToString("yyyy-MM-dd"),
                headers,
                headerDates = ForecastWeekGrid.MapHeaders(headers, weekStart).Select(d => d?.ToString("yyyy-MM-dd")),
                isShowingCurrentWeek = ForecastWeekGrid.IsShowingWeek(headers, weekStart),
                rows,
                inputCount = inputValues.Count,
                inputValues,
            }, Json), cancellationToken);
        });

        await StepAsync("03-add-time-entry-dialog", Note, async () =>
        {
            var button = ForecastSelectors.AddTimeEntryButton(page);
            if (!await ForecastSession.IsVisibleAsync(button))
            {
                Note("Bouton « Add Time Entry » introuvable.");
                return;
            }

            await button.First.ClickAsync();
            await Task.Delay(1000, cancellationToken);
            await CapturePageAsync(page, dir, "03-add-time-entry-dialog");
            var dialog = ForecastSelectors.Dialog(page);
            if (await ForecastSession.IsVisibleAsync(dialog))
                await CaptureElementAsync(dialog.First, dir, "03-add-time-entry-dialog.dialog");
            await WriteProbesAsync(page, Path.Combine(dir, "03-add-time-entry-dialog.probes.txt"));
            await CloseDialogAsync(page);
        });

        await StepAsync("04-add-task-search", Note, async () =>
        {
            var addRow = ForecastSelectors.AddRowButton(page);
            if (await ForecastSession.IsVisibleAsync(addRow))
                await addRow.First.ClickAsync();

            var search = ForecastSelectors.TaskSearchInput(page);
            if (!await ForecastSession.IsVisibleAsync(search))
            {
                Note("Champ de recherche de tâche introuvable.");
                await CapturePageAsync(page, dir, "04-add-task-search");
                return;
            }

            // Safety: never type into something that lives inside the hours grid.
            if (await search.First.EvaluateAsync<bool>("e => !!e.closest('[role=grid], [role=table], table')"))
            {
                Note("Le champ de recherche trouvé est dans la grille : saisie annulée par sécurité.");
                await CapturePageAsync(page, dir, "04-add-task-search");
                return;
            }

            await search.First.ClickAsync();
            await Task.Delay(800, cancellationToken);
            await CapturePageAsync(page, dir, "04-add-task-search.focused");
            // Typing into a search box does not create anything; it shows what the dropdown looks like.
            await search.First.PressSequentiallyAsync("a", new() { Delay = 100 });
            await Task.Delay(1500, cancellationToken);
            await CapturePageAsync(page, dir, "04-add-task-search.typed");
            await WriteProbesAsync(page, Path.Combine(dir, "04-add-task-search.probes.txt"));
            await search.First.FillAsync("");
            await CloseDialogAsync(page);
        });

        await StepAsync("05-previous-week", Note, async () =>
        {
            await session.GotoAsync(page, session.Options.TimesheetsPath);
            var previous = ForecastSelectors.PreviousWeekButton(page);
            if (!await ForecastSession.IsVisibleAsync(previous))
            {
                Note("Bouton « semaine précédente » introuvable.");
                return;
            }

            await previous.First.ClickAsync();
            await ForecastSession.WaitForIdleAsync(page, 5000);
            await CapturePageAsync(page, dir, "05-previous-week");
            var headers = await ForecastSelectors.ColumnHeaders(ForecastSelectors.WeekGrid(page)).AllInnerTextsAsync();
            await File.WriteAllTextAsync(Path.Combine(dir, "05-previous-week.json"), JsonSerializer.Serialize(new
            {
                url = page.Url,
                headers,
                isShowingPreviousWeek = ForecastWeekGrid.IsShowingWeek(headers, weekStart.AddDays(-7)),
            }, Json), cancellationToken);
        });

        await StepAsync("06-my-work", Note, async () =>
        {
            await session.GotoAsync(page, session.Options.MyWorkPath);
            await Task.Delay(1500, cancellationToken);
            await CapturePageAsync(page, dir, "06-my-work");
        });

        await File.WriteAllLinesAsync(Path.Combine(dir, "network.txt"), network, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "README.txt"), BuildReadme(log.ToString()), cancellationToken);
        logger.LogInformation("Découverte terminée : {Dir}", dir);
        return dir;
    }

    private async Task StepAsync(string name, Action<string> note, Func<Task> step)
    {
        note($"Étape {name}...");
        try
        {
            await step();
            note($"Étape {name} : OK");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException)
        {
            note($"Étape {name} : ERREUR {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            logger.LogDebug(ex, "Discovery step {Step} failed", name);
        }
    }

    private static async Task CapturePageAsync(IPage page, string dir, string name)
    {
        var body = page.Locator("body");
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.aria.yml"), await body.AriaSnapshotAsync());
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.html"), await page.ContentAsync());
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.url.txt"), $"{page.Url}\n{await page.TitleAsync()}\n");
        await page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}.png"), FullPage = true });
    }

    private static async Task CaptureElementAsync(ILocator element, string dir, string name)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.aria.yml"), await element.AriaSnapshotAsync());
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.html"), await element.EvaluateAsync<string>("e => e.outerHTML"));
        await element.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{name}.png") });
    }

    /// <summary>For every selector: match count, visibility and a short excerpt of the first matches.</summary>
    private static async Task WriteProbesAsync(IPage page, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"URL : {page.Url}");
        foreach (var (name, locate) in ForecastSelectors.Probes)
        {
            try
            {
                var locator = locate(page);
                var count = await locator.CountAsync();
                sb.AppendLine($"== {name} : {count} élément(s)");
                for (var i = 0; i < Math.Min(count, 3); i++)
                {
                    var item = locator.Nth(i);
                    var visible = await item.IsVisibleAsync();
                    var html = await item.EvaluateAsync<string>("e => e.outerHTML", null, new() { Timeout = 2000 });
                    sb.AppendLine($"  [{i}] visible={visible} : {Truncate(html, 400)}");
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                sb.AppendLine($"== {name} : ERREUR {ex.Message.Split('\n')[0]}");
            }
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private static async Task CloseDialogAsync(IPage page)
    {
        var dialog = ForecastSelectors.Dialog(page);
        if (await ForecastSession.IsVisibleAsync(dialog))
        {
            var cancel = ForecastSelectors.DialogCancelButton(dialog.First);
            if (await ForecastSession.IsVisibleAsync(cancel))
                await cancel.First.ClickAsync();
        }

        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(500);
    }

    private static string Truncate(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    internal static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static string BuildReadme(string log) => $"""
        Découverte Forecast — Time Sheet Helper
        =======================================

        Ce dossier sert à corriger les sélecteurs (src/TimeSheetHelper.Forecast/ForecastSelectors.cs).
        Rien n'a été enregistré dans Forecast pendant cette capture.

        Fichiers par étape :
          *.aria.yml    arbre d'accessibilité (rôles, noms) : la source principale pour écrire des GetByRole/GetByText
          *.html        HTML complet (page) ou outerHTML (élément)
          *.png         capture d'écran
          *.url.txt     URL et titre de la page
          *.probes.txt  nombre d'éléments trouvés par chaque sélecteur actuel + extrait HTML
          02-week-grid.json      en-têtes, lignes et valeurs des cellules de la grille, dates déduites
          05-previous-week.json  en-têtes après « semaine précédente » (vérifie la navigation)
          network.txt   URL des appels réseau (sans contenu) : utile si l'API GraphQL de Forecast est exploitable

        Attention : ces fichiers contiennent vos données Forecast (projets, heures). Ne les partagez qu'en connaissance de cause.

        Journal :
        {log}
        """;
}
