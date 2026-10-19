using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

public enum ForecastTaskStatusFilter { Active, Inactive, All }

public enum ForecastTaskSort { Project, Task, LastSeen }

public enum ForecastTaskGrouping { None, Project, Client, Status }

public sealed record ForecastTaskQuery(
    string? Search = null,
    ForecastTaskStatusFilter Status = ForecastTaskStatusFilter.Active,
    bool FavoritesOnly = false,
    ForecastTaskSort Sort = ForecastTaskSort.Project,
    bool Descending = false,
    ForecastTaskGrouping Grouping = ForecastTaskGrouping.None,
    bool ExcludedOnly = false);

/// <summary>One listed task; <see cref="GroupHeader"/> is set on the first row of each group.</summary>
public sealed record ForecastTaskRow(ForecastTask Task, string? GroupHeader);

public sealed record ForecastTaskPage(IReadOnlyList<ForecastTaskRow> Items, int Total);

public sealed record ForecastTaskCounts(int Total, int Active, int Favorites, int Excluded = 0);

public sealed record ForecastTaskOperationResult(bool Ok, string? Error = null)
{
    public static ForecastTaskOperationResult Success { get; } = new(true);
    public static ForecastTaskOperationResult Fail(string error) => new(false, error);
}

public sealed record ForecastTaskBlocked(int TaskId, string Name, int References);

public sealed record ForecastTaskDeleteReport(int Deleted, IReadOnlyList<ForecastTaskBlocked> Blocked)
{
    public string Summary => Blocked.Count == 0
        ? $"{Deleted} tâche(s) supprimée(s)."
        : $"{Deleted} tâche(s) supprimée(s), {Blocked.Count} conservée(s) car utilisée(s) par des règles ou des feuilles de temps : désactivez-les plutôt.";
}

/// <summary>Browse and maintain the local list of Forecast tasks (list, edit, delete, bulk actions, favorites).</summary>
public sealed class ForecastTaskCatalog(IDbContextFactory<TimeSheetDbContext> dbFactory)
{
    /// <summary>
    /// A page of the filtered, sorted and grouped list. The whole list is a few hundred rows, so filtering and sorting
    /// happen in memory: the culture-aware (accent-insensitive) order the user expects is not available in SQLite.
    /// </summary>
    public async Task<ForecastTaskPage> QueryAsync(
        ForecastTaskQuery query, int skip, int take, CancellationToken cancellationToken = default)
    {
        var ordered = await LoadOrderedAsync(query, cancellationToken);
        var rows = new List<ForecastTaskRow>(Math.Min(take, Math.Max(0, ordered.Count - skip)));
        for (var i = Math.Max(0, skip); i < ordered.Count && rows.Count < take; i++)
        {
            var key = GroupKey(ordered[i], query.Grouping);
            var startsGroup = query.Grouping != ForecastTaskGrouping.None
                && (i == 0 || GroupKey(ordered[i - 1], query.Grouping) != key);
            rows.Add(new ForecastTaskRow(ordered[i], startsGroup ? key : null));
        }

        return new ForecastTaskPage(rows, ordered.Count);
    }

    /// <summary>Ids of every task matching the query (select-all across pages).</summary>
    public async Task<IReadOnlyList<int>> QueryIdsAsync(ForecastTaskQuery query, CancellationToken cancellationToken = default) =>
        (await LoadOrderedAsync(query, cancellationToken)).Select(t => t.Id).ToList();

    public async Task<ForecastTaskCounts> CountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tasks = db.ForecastTasks.Where(t => t.ExternalId != ForecastTask.IgnoredExternalId);
        return new ForecastTaskCounts(
            await tasks.CountAsync(cancellationToken),
            await tasks.CountAsync(t => t.IsActive, cancellationToken),
            await tasks.CountAsync(t => t.IsFavorite, cancellationToken),
            await tasks.CountAsync(t => t.ExcludeFromSubmission, cancellationToken));
    }

    public async Task<ForecastTask?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ForecastTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    /// <summary>
    /// Renames a task and its codes. The identity (ExternalId) follows the task code, or the "project/task" path
    /// when it has none; it must stay unique.
    /// </summary>
    public async Task<ForecastTaskOperationResult> UpdateAsync(
        int id, string? projectName, string? taskName, string? projectCode, string? taskCode,
        CancellationToken cancellationToken = default)
    {
        projectName = ForecastTaskListImport.CollapseSpaces(projectName ?? "");
        taskName = ForecastTaskListImport.CollapseSpaces(taskName ?? "");
        if (projectName.Length == 0)
            return ForecastTaskOperationResult.Fail("Indiquez le projet.");
        if (taskName.Length == 0)
            return ForecastTaskOperationResult.Fail("Indiquez le nom de la tâche.");

        var normalizedProjectCode = ForecastTaskImporter.NormalizeCode(projectCode, ForecastCodes.ProjectCode);
        var normalizedTaskCode = ForecastTaskImporter.NormalizeCode(taskCode, ForecastCodes.TaskCode);
        if (normalizedProjectCode == ForecastTaskImporter.InvalidCode)
            return ForecastTaskOperationResult.Fail("Code de projet invalide : attendu « P1078 » (ou laissez vide).");
        if (normalizedTaskCode == ForecastTaskImporter.InvalidCode)
            return ForecastTaskOperationResult.Fail("Code de tâche invalide : attendu « T35647 » (ou laissez vide).");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.ForecastTasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null)
            return ForecastTaskOperationResult.Fail("Cette tâche n'existe plus.");
        if (task.IsIgnored)
            return ForecastTaskOperationResult.Fail("La tâche « Ignoré » est intégrée et ne peut pas être modifiée.");

        var externalId = normalizedTaskCode ?? $"{projectName}/{taskName}";
        if (externalId != task.ExternalId
            && await db.ForecastTasks.AnyAsync(t => t.ExternalId == externalId && t.Id != id, cancellationToken))
            return ForecastTaskOperationResult.Fail($"Une autre tâche utilise déjà « {externalId} ».");

        task.ExternalId = externalId;
        task.ProjectName = projectName;
        task.TaskName = taskName;
        task.ProjectCode = normalizedProjectCode;
        await db.SaveChangesAsync(cancellationToken);
        return ForecastTaskOperationResult.Success;
    }

    public async Task<int> SetActiveAsync(IReadOnlyCollection<int> ids, bool active, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ForecastTasks
            .Where(t => ids.Contains(t.Id) && t.ExternalId != ForecastTask.IgnoredExternalId && t.IsActive != active)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, active), cancellationToken);
    }

    public async Task<int> SetFavoriteAsync(IReadOnlyCollection<int> ids, bool favorite, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ForecastTasks
            .Where(t => ids.Contains(t.Id) && t.ExternalId != ForecastTask.IgnoredExternalId && t.IsFavorite != favorite)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsFavorite, favorite), cancellationToken);
    }

    /// <summary>Flags (or unflags) tasks that cannot be entered in Forecast through the MCP server: their hours are left out of the submission.</summary>
    public async Task<int> SetExcludeFromSubmissionAsync(IReadOnlyCollection<int> ids, bool exclude, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ForecastTasks
            .Where(t => ids.Contains(t.Id) && t.ExternalId != ForecastTask.IgnoredExternalId && t.ExcludeFromSubmission != exclude)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExcludeFromSubmission, exclude), cancellationToken);
    }

    /// <summary>
    /// Deletes the tasks nothing refers to. A task used by a rule, a timesheet line, a slot override, a cached
    /// classification or a day submission (sent cell, journal line) is kept and reported, so the user can deactivate it instead.
    /// </summary>
    public async Task<ForecastTaskDeleteReport> DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return new ForecastTaskDeleteReport(0, []);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.ForecastTasks
            .Where(t => ids.Contains(t.Id) && t.ExternalId != ForecastTask.IgnoredExternalId)
            .ToListAsync(cancellationToken);
        var taskIds = tasks.Select(t => t.Id).ToList();

        var references = new Dictionary<int, int>();
        void Add(IEnumerable<int> used)
        {
            foreach (var taskId in used)
                references[taskId] = references.GetValueOrDefault(taskId) + 1;
        }

        Add(await db.Rules.Where(r => taskIds.Contains(r.ForecastTaskId)).Select(r => r.ForecastTaskId).ToListAsync(cancellationToken));
        Add(await db.TimesheetEntries.Where(e => taskIds.Contains(e.ForecastTaskId)).Select(e => e.ForecastTaskId).ToListAsync(cancellationToken));
        Add(await db.SlotOverrides.Where(o => o.ForecastTaskId != null && taskIds.Contains(o.ForecastTaskId.Value))
            .Select(o => o.ForecastTaskId!.Value).ToListAsync(cancellationToken));
        Add(await db.ClassificationCache.Where(c => c.ForecastTaskId != null && taskIds.Contains(c.ForecastTaskId.Value))
            .Select(c => c.ForecastTaskId!.Value).ToListAsync(cancellationToken));
        Add(await db.DaySubmissionCells.Where(c => taskIds.Contains(c.ForecastTaskId)).Select(c => c.ForecastTaskId).ToListAsync(cancellationToken));
        Add(await db.SubmissionJournal.Where(j => j.ForecastTaskId != null && taskIds.Contains(j.ForecastTaskId.Value))
            .Select(j => j.ForecastTaskId!.Value).ToListAsync(cancellationToken));

        var blocked = tasks.Where(t => references.ContainsKey(t.Id))
            .Select(t => new ForecastTaskBlocked(t.Id, t.DisplayName, references[t.Id]))
            .ToList();
        var deletable = tasks.Where(t => !references.ContainsKey(t.Id)).ToList();

        db.ForecastTasks.RemoveRange(deletable);
        await db.SaveChangesAsync(cancellationToken);
        return new ForecastTaskDeleteReport(deletable.Count, blocked);
    }

    private async Task<List<ForecastTask>> LoadOrderedAsync(ForecastTaskQuery query, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.ForecastTasks.AsNoTracking()
            .Where(t => t.ExternalId != ForecastTask.IgnoredExternalId)
            .ToListAsync(cancellationToken);

        IEnumerable<ForecastTask> filtered = tasks;
        filtered = query.Status switch
        {
            ForecastTaskStatusFilter.Active => filtered.Where(t => t.IsActive),
            ForecastTaskStatusFilter.Inactive => filtered.Where(t => !t.IsActive),
            _ => filtered,
        };
        if (query.FavoritesOnly)
            filtered = filtered.Where(t => t.IsFavorite);
        if (query.ExcludedOnly)
            filtered = filtered.Where(t => t.ExcludeFromSubmission);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            filtered = filtered.Where(t =>
                $"{t.ProjectCode} {t.ProjectName} {t.ClientName} {t.ExternalId} {t.TaskName}"
                    .Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ForecastTask> ordered = query.Grouping switch
        {
            ForecastTaskGrouping.Project => filtered.OrderBy(t => t.ProjectName, comparer),
            ForecastTaskGrouping.Client => filtered.OrderBy(t => t.ClientName ?? t.ProjectName, comparer),
            ForecastTaskGrouping.Status => filtered.OrderByDescending(t => t.IsActive),
            _ => filtered.OrderBy(_ => 0),
        };

        ordered = query.Sort switch
        {
            ForecastTaskSort.Task => Then(ordered, t => t.TaskName, comparer, query.Descending),
            ForecastTaskSort.LastSeen => Then(ordered, t => t.LastSeenUtc, Comparer<DateTime>.Default, query.Descending),
            _ => Then(ordered, t => t.ProjectName, comparer, query.Descending),
        };

        // Stable tie-breakers so paging never shows a row twice.
        return ordered.ThenBy(t => t.TaskName, comparer).ThenBy(t => t.Id).ToList();
    }

    private static IOrderedEnumerable<ForecastTask> Then<TKey>(
        IOrderedEnumerable<ForecastTask> source, Func<ForecastTask, TKey> key, IComparer<TKey> comparer, bool descending) =>
        descending ? source.ThenByDescending(key, comparer) : source.ThenBy(key, comparer);

    private static string GroupKey(ForecastTask task, ForecastTaskGrouping grouping) => grouping switch
    {
        ForecastTaskGrouping.Project => task.ProjectName,
        ForecastTaskGrouping.Client => task.ClientName ?? task.ProjectName,
        ForecastTaskGrouping.Status => task.IsActive ? "Actives" : "Désactivées",
        _ => "",
    };
}
