using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Reads the tasks the user can log time on and caches them as <see cref="Core.Forecast.ForecastTask"/> rows.
/// Source (VERIFIED): the "My Timesheets" page, tabs "Find Tasks" (tasks assigned this week) and "Timesheets"
/// (rows that already have time), whose rows carry the task code in the title of the task cell ("T3382 - ...").
/// </summary>
public sealed class ForecastTaskReader(
    ForecastSession session,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<ForecastTaskReader> logger)
{
    public async Task<ForecastTaskSyncResult> SyncTasksAsync(CancellationToken cancellationToken = default)
    {
        var page = await session.EnsureLoggedInAsync(cancellationToken);
        await session.GotoAsync(page, session.Options.TimesheetsPath);

        var parsed = new List<ParsedForecastTask>();
        foreach (var (name, tab) in new[] { ("Find Tasks", ForecastSelectors.FindTasksTab(page)), ("Timesheets", ForecastSelectors.TimesheetTab(page)) })
        {
            try
            {
                var rows = await ReadTabAsync(page, tab);
                logger.LogInformation("Onglet « {Tab} » : {Count} ligne(s).", name, rows.Count);
                parsed.AddRange(rows.Select(ToTask));
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                logger.LogWarning(ex, "Lecture de l'onglet « {Tab} » impossible.", name);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await ForecastTaskSync.UpsertAsync(db, parsed, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        logger.LogInformation("{Summary}", result.Summary);
        return result;
    }

    internal static async Task<IReadOnlyList<ForecastGridRow>> ReadTabAsync(IPage page, ILocator tab)
    {
        if (await ForecastSession.IsVisibleAsync(tab))
        {
            await tab.First.ClickAsync();
            await ForecastSession.WaitForIdleAsync(page, 5000);
        }
        return (await ForecastTimesheetDom.ReadAsync(page)).Rows;
    }

    internal static ParsedForecastTask ToTask(ForecastGridRow row) => new(
        row.TaskCode ?? $"{row.ProjectName}/{row.TaskName}",
        row.ProjectName,
        row.ProjectCode,
        ClientName: null,
        row.TaskName);
}
