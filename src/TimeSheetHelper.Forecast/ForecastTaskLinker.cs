using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

public sealed record ForecastRelinkResult(int TaskId, bool Merged, string Message);

/// <summary>
/// Searches Forecast for the task a local <see cref="Core.Forecast.ForecastTask"/> stands for and points the local task at it,
/// so the simulation and the fill find it from then on.
/// </summary>
public sealed class ForecastTaskLinker(
    ForecastApi api,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    TimeProvider timeProvider,
    ILogger<ForecastTaskLinker> logger)
{
    /// <summary>Tasks of the whole company matching a name or a T-number.</summary>
    public async Task<IReadOnlyList<ForecastRemoteTask>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        await using var session = await api.OpenAsync(cancellationToken);
        var found = await session.FindTasksAsync(query.Trim(), cancellationToken);
        logger.LogInformation("Recherche Forecast « {Query} » : {Count} tâche(s).", query, found.Count);
        return found;
    }

    /// <summary>
    /// Gives the local task the identity of the Forecast one. When another local task already has that identity, the two are
    /// merged into it: rules, drafts and assignments follow, and the broken one is removed.
    /// </summary>
    public async Task<ForecastRelinkResult> RelinkAsync(int localTaskId, ForecastRemoteTask remote, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remote.Code))
            throw new InvalidOperationException($"La tâche « {remote.Title} » de Forecast n'a pas de numéro (T…) : elle ne peut pas être reliée.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var local = await db.ForecastTasks.FirstOrDefaultAsync(t => t.Id == localTaskId, cancellationToken)
            ?? throw new InvalidOperationException($"La tâche locale #{localTaskId} n'existe plus.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var project = string.IsNullOrWhiteSpace(remote.ProjectName) ? remote.ClientName ?? "(sans projet)" : remote.ProjectName;
        var target = await db.ForecastTasks.FirstOrDefaultAsync(t => t.ExternalId == remote.Code && t.Id != localTaskId, cancellationToken);
        var previous = local.DisplayName;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (target is null)
        {
            Apply(local, remote, project, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("« {Previous} » reliée à {Code}.", previous, remote.Code);
            return new ForecastRelinkResult(local.Id, false, $"« {previous} » est reliée à {remote.Code} : « {local.DisplayName} ».");
        }

        Apply(target, remote, project, now);
        await db.Rules.Where(r => r.ForecastTaskId == localTaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ForecastTaskId, target.Id), cancellationToken);
        await db.TimesheetEntries.Where(e => e.ForecastTaskId == localTaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ForecastTaskId, target.Id), cancellationToken);
        await db.SlotOverrides.Where(o => o.ForecastTaskId == localTaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ForecastTaskId, (int?)target.Id), cancellationToken);
        await db.ClassificationCache.Where(c => c.ForecastTaskId == localTaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ForecastTaskId, (int?)target.Id), cancellationToken);
        db.ForecastTasks.Remove(local);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("« {Previous} » fusionnée avec {Code}.", previous, remote.Code);
        return new ForecastRelinkResult(target.Id, true,
            $"« {previous} » est fusionnée avec la tâche {remote.Code} déjà connue (« {target.DisplayName} ») : règles et heures lui sont rattachées.");
    }

    private static void Apply(Core.Forecast.ForecastTask task, ForecastRemoteTask remote, string project, DateTime now)
    {
        task.ExternalId = remote.Code!;
        task.ProjectName = project;
        task.TaskName = remote.Title;
        task.ProjectCode = remote.ProjectCode ?? task.ProjectCode;
        task.ClientName = remote.ClientName ?? task.ClientName;
        task.IsActive = true;
        task.LastSeenUtc = now;
    }
}
