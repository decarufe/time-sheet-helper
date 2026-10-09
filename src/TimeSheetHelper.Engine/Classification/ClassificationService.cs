using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Engine.Classification;

/// <summary>
/// Classifies unclassified groups through <see cref="ITaskClassifier"/>, caching verdicts in the database by a
/// fingerprint of the group key and the task list version, so a group is sent at most once per task list.
/// </summary>
public sealed class ClassificationService(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ITaskClassifier classifier,
    IOptions<EngineOptions> options,
    TimeProvider timeProvider,
    ILogger<ClassificationService> logger)
{
    /// <summary>Version of the task list: changes when a task is added, renamed or (de)activated.</summary>
    public static string TaskListVersion(IEnumerable<ForecastTask> tasks)
    {
        var canonical = string.Join('\n', tasks
            .Where(t => t.IsActive)
            .OrderBy(t => t.Id)
            .Select(t => $"{t.Id}|{t.ClientName}|{t.ProjectName}|{t.TaskName}"));
        return Sha256(canonical);
    }

    public static string Fingerprint(string groupKey, string taskListVersion) => Sha256($"{groupKey}\n{taskListVersion}");

    /// <summary>Cached verdicts only (no classifier call), by group key.</summary>
    public async Task<IReadOnlyDictionary<string, ClassificationResult>> GetCachedAsync(
        IEnumerable<string> groupKeys,
        IReadOnlyList<ForecastTask> tasks,
        CancellationToken cancellationToken = default)
    {
        var version = TaskListVersion(tasks);
        var byFingerprint = groupKeys.Distinct().ToDictionary(k => Fingerprint(k, version));
        if (byFingerprint.Count == 0)
            return new Dictionary<string, ClassificationResult>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var fingerprints = byFingerprint.Keys.ToList();
        var rows = await db.ClassificationCache.AsNoTracking()
            .Where(c => fingerprints.Contains(c.Fingerprint))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => byFingerprint[r.Fingerprint], ToResult);
    }

    /// <summary>
    /// Verdicts for <paramref name="groups"/>: cached ones, plus fresh classifier calls for the missing groups
    /// long enough to be worth it (when the classifier is available).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ClassificationResult>> ClassifyAsync(
        IReadOnlyList<ActivityGroup> groups,
        IReadOnlyList<ForecastTask> tasks,
        IReadOnlyList<Rule> rules,
        CancellationToken cancellationToken = default)
    {
        var activeTasks = tasks.Where(t => t.IsActive).ToList();
        var results = new Dictionary<string, ClassificationResult>(
            await GetCachedAsync(groups.Select(g => g.Key), activeTasks, cancellationToken));

        if (!classifier.IsAvailable || activeTasks.Count == 0)
            return results;

        var minDuration = options.Value.Claude.MinGroupDuration;
        var version = TaskListVersion(activeTasks);
        foreach (var group in groups.Where(g => !results.ContainsKey(g.Key) && g.TotalDuration >= minDuration))
        {
            var verdict = await classifier.ClassifyAsync(group, activeTasks, rules, cancellationToken);
            if (verdict is null)
                continue; // Errors are not cached: the next rebuild retries.

            results[group.Key] = verdict;
            await StoreAsync(Fingerprint(group.Key, version), verdict, cancellationToken);
        }

        return results;
    }

    private async Task StoreAsync(string fingerprint, ClassificationResult verdict, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ClassificationCache.FirstOrDefaultAsync(c => c.Fingerprint == fingerprint, cancellationToken);
        if (entry is null)
        {
            entry = new ClassificationCacheEntry { Fingerprint = fingerprint };
            db.ClassificationCache.Add(entry);
        }

        entry.ForecastTaskId = verdict.TaskId;
        entry.Confidence = verdict.Confidence;
        entry.Reason = verdict.Reason;
        entry.SuggestedRuleJson = verdict.SuggestedRule is null ? null : JsonSerializer.Serialize(verdict.SuggestedRule);
        entry.CreatedUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Another rebuild stored the same fingerprint concurrently; its verdict is as good as ours.
            logger.LogDebug(ex, "Classification cache entry {Fingerprint} already stored.", fingerprint);
        }
    }

    private static ClassificationResult ToResult(ClassificationCacheEntry entry)
    {
        SuggestedRule? rule = null;
        if (!string.IsNullOrEmpty(entry.SuggestedRuleJson))
        {
            try
            {
                rule = JsonSerializer.Deserialize<SuggestedRule>(entry.SuggestedRuleJson);
            }
            catch (JsonException)
            {
                rule = null;
            }
        }
        return new ClassificationResult(entry.ForecastTaskId, entry.Confidence, entry.Reason, rule);
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
