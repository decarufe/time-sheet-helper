using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Diagnostic run used to fix <see cref="ForecastSelectors"/>: captures ARIA snapshots, HTML and screenshots of the
/// timesheet page and its tabs, the popup of a day cell, the "Add Timesheet Entry" form and its task search, and the
/// previous week, plus how many elements each selector matches and the URLs of the network calls (no bodies).
/// It never types nor saves anything in Forecast: popups and forms are only opened, then closed with Escape.
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
            await WriteSnapshotAsync(page, dir, "01-timesheets", weekStart, cancellationToken);
        });

        await StepAsync("02-tab-timesheets", Note, async () =>
        {
            await ClickIfVisibleAsync(page, ForecastSelectors.TimesheetTab(page));
            await CapturePageAsync(page, dir, "02-tab-timesheets");
            await WriteSnapshotAsync(page, dir, "02-tab-timesheets", weekStart, cancellationToken);
        });

        await StepAsync("03-tab-find-tasks", Note, async () =>
        {
            await ClickIfVisibleAsync(page, ForecastSelectors.FindTasksTab(page));
            await CapturePageAsync(page, dir, "03-tab-find-tasks");
            await WriteSnapshotAsync(page, dir, "03-tab-find-tasks", weekStart, cancellationToken);
        });

        await StepAsync("04-hour-popup", Note, async () =>
        {
            // Opens the popup of one day cell to learn its structure, then Escape: nothing is typed.
            var snapshot = await ForecastTimesheetDom.ReadAsync(page);
            if (snapshot.Rows.Count == 0)
            {
                Note("Aucune ligne dans l'onglet courant : popup non capturé.");
                return;
            }

            var row = snapshot.Rows[0];
            var dates = ForecastTimesheetDom.HeaderDates(snapshot.DayHeaders, weekStart);
            var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
            var day = Math.Max(0, dates.ToList().IndexOf(today));
            var before = row.Hours.ElementAtOrDefault(day);

            await ForecastSelectors.DayCells(page, row.Index).Nth(day).ClickAsync();
            await Task.Delay(1200, cancellationToken);
            await CapturePageAsync(page, dir, "04-hour-popup");
            await WriteProbesAsync(page, Path.Combine(dir, "04-hour-popup.probes.txt"));
            await File.WriteAllTextAsync(Path.Combine(dir, "04-hour-popup.focus.html"),
                await page.EvaluateAsync<string>(FocusAndLayersScript), cancellationToken);

            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(800, cancellationToken);
            var after = (await ForecastTimesheetDom.ReadAsync(page)).Rows.FirstOrDefault(r => r.Index == row.Index)?.Hours.ElementAtOrDefault(day);
            Note($"Popup : ligne « {row.TaskName} », colonne {day}, valeur avant « {before} », après Échap « {after} »"
                + (before == after ? " (inchangée)." : " — ATTENTION : la valeur a changé, vérifiez dans Forecast."));
        });

        await StepAsync("04b-hour-popup-filled", Note, async () =>
        {
            // Same as 04 on a cell that already has hours: shows how existing entries are edited. Nothing is typed.
            await ClickIfVisibleAsync(page, ForecastSelectors.TimesheetTab(page));
            var snapshot = await ForecastTimesheetDom.ReadAsync(page);
            var target = snapshot.Rows
                .SelectMany(r => r.Hours.Select((h, day) => (Row: r, Day: day, Text: h)))
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Text));
            if (target.Row is null)
            {
                Note("Aucune cellule remplie cette semaine : popup d'édition non capturé.");
                return;
            }

            await ForecastSelectors.DayCells(page, target.Row.Index).Nth(target.Day).ClickAsync();
            await Task.Delay(1200, cancellationToken);
            await CapturePageAsync(page, dir, "04b-hour-popup-filled");
            await File.WriteAllTextAsync(Path.Combine(dir, "04b-hour-popup-filled.focus.html"),
                await page.EvaluateAsync<string>(FocusAndLayersScript), cancellationToken);

            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(800, cancellationToken);
            var after = (await ForecastTimesheetDom.ReadAsync(page)).Rows.FirstOrDefault(r => r.Index == target.Row.Index)?.Hours.ElementAtOrDefault(target.Day);
            Note($"Popup (cellule remplie) : « {target.Row.TaskName} », colonne {target.Day}, avant « {target.Text} », après Échap « {after} »"
                + (target.Text == after ? " (inchangée)." : " — ATTENTION : la valeur a changé, vérifiez dans Forecast."));
        });

        await StepAsync("05-add-time-entry-dialog", Note, async () =>
        {
            if (!await ClickIfVisibleAsync(page, ForecastSelectors.AddTimeEntryButton(page)))
            {
                Note("Bouton « Add Timesheet Entry » introuvable.");
                return;
            }

            await CapturePageAsync(page, dir, "05-add-time-entry-dialog");
            // The task search only lists options: opening it creates nothing.
            if (await ClickIfVisibleAsync(page, ForecastSelectors.TimeEntrySearchButton(page)))
            {
                await CapturePageAsync(page, dir, "05-add-time-entry-search");
                await WriteProbesAsync(page, Path.Combine(dir, "05-add-time-entry-search.probes.txt"));
                await File.WriteAllTextAsync(Path.Combine(dir, "05-add-time-entry-search.focus.html"),
                    await page.EvaluateAsync<string>(FocusAndLayersScript), cancellationToken);
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(500, cancellationToken);
            }

            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(800, cancellationToken);
            if (await ForecastSession.IsVisibleAsync(ForecastSelectors.TimeEntryForm(page)))
                Note("Le formulaire est toujours ouvert après Échap : fermez-le sans enregistrer.");
        });

        await StepAsync("06-previous-week", Note, async () =>
        {
            if (!await ClickIfVisibleAsync(page, ForecastSelectors.PreviousWeekButton(page)))
            {
                Note("Bouton « semaine précédente » introuvable.");
                return;
            }

            await WriteSnapshotAsync(page, dir, "06-previous-week", weekStart.AddDays(-7), cancellationToken);
            await ClickIfVisibleAsync(page, ForecastSelectors.NextWeekButton(page));
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

    /// <summary>Focused element and popover layers (where Forecast renders popups), with their full HTML.</summary>
    private const string FocusAndLayersScript = """
        () => {
          const active = document.activeElement;
          const layers = [...document.querySelectorAll('[data-radix-popper-content-wrapper], [role=dialog], [role=listbox], [role=menu]')];
          return '<!-- activeElement -->\n' + (active ? active.outerHTML : '(aucun)')
            + '\n\n<!-- ancestors of activeElement -->\n' + (() => { const a = []; let e = active; for (let i = 0; e && i < 6; i++, e = e.parentElement) a.push(e.tagName + (e.id ? '#' + e.id : '') + (e.dataset?.cy ? '[data-cy=' + e.dataset.cy + ']' : '')); return a.join(' < '); })()
            + '\n\n<!-- layers -->\n' + layers.map(l => l.outerHTML).join('\n\n');
        }
        """;

    private static async Task<bool> ClickIfVisibleAsync(IPage page, ILocator locator)
    {
        if (!await ForecastSession.IsVisibleAsync(locator))
            return false;
        await locator.First.ClickAsync();
        await ForecastSession.WaitForIdleAsync(page, 5000);
        await Task.Delay(800);
        return true;
    }

    private static async Task WriteSnapshotAsync(IPage page, string dir, string name, DateOnly near, CancellationToken cancellationToken)
    {
        var snapshot = await ForecastTimesheetDom.ReadAsync(page);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.grid.json"), JsonSerializer.Serialize(new
        {
            snapshot.DayHeaders,
            headerDates = ForecastTimesheetDom.HeaderDates(snapshot.DayHeaders, near).Select(d => d?.ToString("yyyy-MM-dd")),
            snapshot.Rows,
        }, Json), cancellationToken);
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
          *.aria.yml    arbre d'accessibilité (rôles, noms)
          *.html        HTML complet de la page
          *.png         capture d'écran
          *.url.txt     URL et titre de la page
          *.probes.txt  nombre d'éléments trouvés par chaque sélecteur actuel + extrait HTML
          *.grid.json   lignes de la grille telles que lues par l'outil (tâches, codes, heures par jour)
          04-hour-popup.*        popup ouvert en cliquant une cellule d'heures (fermé par Échap, rien saisi)
          *.focus.html  élément actif et couches de popup (structure du champ de saisie)
        network.txt   URL des appels réseau (sans contenu) : utile si l'API GraphQL de Forecast est exploitable

        Attention : ces fichiers contiennent vos données Forecast (projets, heures). Ne les partagez qu'en connaissance de cause.

        Journal :
        {log}
        """;
}
