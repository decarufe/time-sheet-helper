using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Reads the tasks the user can log time on and caches them as <see cref="Core.Forecast.ForecastTask"/> rows.
///
/// Sources (UNVERIFIED, see report): the "My Work" page lists the tasks assigned to the user with their
/// company id ("T123"); the timesheet grid lists the tasks already on the current week. Both are scraped
/// and merged. The "add task" search is not used: it needs a query and only returns a filtered subset.
/// </summary>
public sealed class ForecastTaskReader(
    ForecastSession session,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<ForecastTaskReader> logger)
{
    /// <summary>
    /// Generic scraper: innermost row-like elements whose text contains a task code (or, with
    /// <c>requireCode=false</c>, every row of the first grid/table), returned as cell texts + link targets.
    /// </summary>
    private const string ScrapeRowsScript = """
        (requireCode) => {
          const codeRx = /\bT\d{1,7}\b/;
          const rowSel = '[role=row], tr, [role=listitem], li';
          let rows = [...document.querySelectorAll(rowSel)];
          if (requireCode) {
            rows = rows.filter(r => codeRx.test(r.innerText || '') || [...r.querySelectorAll('a[href]')].some(a => codeRx.test(a.getAttribute('href'))));
            rows = rows.filter(r => ![...r.querySelectorAll(rowSel)].some(c => rows.includes(c)));
          } else {
            const grid = document.querySelector('[role=grid], [role=table], table');
            rows = grid ? [...grid.querySelectorAll('[role=row], tr')].filter(r => !r.querySelector('[role=columnheader], th')) : [];
          }
          return rows.slice(0, 2000).map(r => {
            let cells = [...r.querySelectorAll('[role=cell], [role=gridcell], [role=rowheader], td')];
            if (cells.length === 0) cells = [...r.children];
            return {
              cells: cells.map(c => (c.innerText || '').replace(/\s+/g, ' ').trim()).filter(t => t.length > 0),
              hrefs: [...r.querySelectorAll('a[href]')].map(a => a.getAttribute('href')),
            };
          });
        }
        """;

    public async Task<ForecastTaskSyncResult> SyncTasksAsync(CancellationToken cancellationToken = default)
    {
        var page = await session.EnsureLoggedInAsync(cancellationToken);
        var parsed = new List<ParsedForecastTask>();

        // 1. My Work: tasks assigned to the user.
        try
        {
            await session.GotoAsync(page, session.Options.MyWorkPath);
            var rows = await ScrapeAsync(page, requireCode: true);
            logger.LogInformation("« Mon travail » : {Count} ligne(s) avec un identifiant de tâche.", rows.Count);
            parsed.AddRange(rows.Select(ForecastTaskSync.Parse).OfType<ParsedForecastTask>());
        }
        catch (PlaywrightException ex)
        {
            logger.LogWarning(ex, "Lecture de « Mon travail » impossible.");
        }

        // 2. Timesheet grid of the current week: tasks already used (may include tasks no longer assigned).
        try
        {
            await session.GotoAsync(page, session.Options.TimesheetsPath);
            var rows = await ScrapeAsync(page, requireCode: true);
            if (rows.Count == 0)
                rows = await ScrapeAsync(page, requireCode: false);
            logger.LogInformation("Feuille de temps : {Count} ligne(s).", rows.Count);
            parsed.AddRange(rows.Select(ForecastTaskSync.Parse).OfType<ParsedForecastTask>());
        }
        catch (PlaywrightException ex)
        {
            logger.LogWarning(ex, "Lecture de la feuille de temps impossible.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await ForecastTaskSync.UpsertAsync(db, parsed, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        logger.LogInformation("{Summary}", result.Summary);
        return result;
    }

    private static async Task<IReadOnlyList<RawForecastTaskRow>> ScrapeAsync(IPage page, bool requireCode)
    {
        var json = await page.EvaluateAsync<JsonElement>(ScrapeRowsScript, requireCode);
        var rows = new List<RawForecastTaskRow>();
        foreach (var row in json.EnumerateArray())
        {
            var cells = row.GetProperty("cells").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
            var hrefs = row.GetProperty("hrefs").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
            rows.Add(new RawForecastTaskRow(cells, hrefs));
        }

        return rows;
    }
}
