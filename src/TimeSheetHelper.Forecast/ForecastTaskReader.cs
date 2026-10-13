using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>Reads the tasks Forecast offers through its MCP server and caches them as <see cref="Core.Forecast.ForecastTask"/> rows.</summary>
public sealed class ForecastTaskReader(
    ForecastApi api,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<ForecastTaskReader> logger)
{
    public async Task<ForecastTaskSyncResult> SyncTasksAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ForecastRemoteTask> remote;
        await using (var session = await api.OpenAsync(cancellationToken))
            remote = await session.ListTasksAsync(cancellationToken);

        logger.LogInformation("Forecast MCP : {Count} tâche(s) lue(s).", remote.Count);
        var parsed = remote.Select(ToTask).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var result = await ForecastTaskSync.UpsertAsync(
            db, parsed, timeProvider.GetUtcNow().UtcDateTime, deactivateMissing: false, cancellationToken: cancellationToken);
        logger.LogInformation("{Summary}", result.Summary);
        return result;
    }

    internal static ParsedForecastTask ToTask(ForecastRemoteTask task)
    {
        var project = string.IsNullOrWhiteSpace(task.ProjectName) ? task.ClientName ?? "(sans projet)" : task.ProjectName;
        return new ParsedForecastTask(task.Code ?? $"{project}/{task.Title}", project, task.ProjectCode, task.ClientName, task.Title);
    }
}
