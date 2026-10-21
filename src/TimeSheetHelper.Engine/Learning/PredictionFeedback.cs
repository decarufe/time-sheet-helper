using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Engine.Learning;

/// <summary>
/// Learns from the tasks the user confirms or assigns: each confirmation counts once per activity group found in the slots,
/// and the prediction of that group's activities is then split between its rule/group and the confirmed tasks.
/// </summary>
public sealed class PredictionFeedback(IDbContextFactory<TimeSheetDbContext> dbFactory, ActivityStore activityStore, ActivityResolver resolver, TimeProvider timeProvider)
{
    /// <summary>Pseudo-count of a rule's task: a rule keeps winning until another task was confirmed more than twice.</summary>
    public const double RulePrior = 2;

    /// <summary>Pseudo-count of an unclassified group: a single assignment only shows the task as an alternative (the group stays
    /// unclassified, so group assignment keeps working); from two confirmations on the task is predicted.</summary>
    public const double GroupPrior = 1.5;

    /// <summary>
    /// Candidates an activity votes for and the share of its weight each one gets.
    /// Without feedback the activity keeps its single candidate with full weight.
    /// </summary>
    public static IReadOnlyList<(Candidate Candidate, double Weight)> Split(Candidate candidate, bool fromRule, IReadOnlyDictionary<int, int>? counts)
    {
        if (counts is null || counts.Count == 0)
            return [(candidate, 1.0)];

        var votes = new Dictionary<Candidate, double> { [candidate] = fromRule ? RulePrior : GroupPrior };
        foreach (var (taskId, count) in counts.Where(c => c.Value > 0))
        {
            var key = Candidate.ForTask(taskId);
            votes[key] = votes.GetValueOrDefault(key) + count;
        }

        var total = votes.Values.Sum();
        return votes.Select(v => (v.Key, v.Value / total)).ToList();
    }

    /// <summary>Confirmation counts per group key and task.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>> LoadAsync(TimeSheetDbContext db, CancellationToken cancellationToken = default)
    {
        var rows = await db.AssignmentFeedback.AsNoTracking().ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.GroupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, int>)g.ToDictionary(r => r.ForecastTaskId, r => r.Count), StringComparer.Ordinal);
    }

    /// <summary>
    /// Records that the user put <paramref name="slotStarts"/> on <paramref name="forecastTaskId"/>: +1 for every distinct
    /// dominant group of these slots (the group with the most work time in each slot). Ignores the built-in "Ignoré" task.
    /// </summary>
    public Task RecordAsync(IReadOnlyCollection<DateTime> slotStarts, int forecastTaskId, CancellationToken cancellationToken = default) =>
        AdjustAsync(slotStarts, forecastTaskId, +1, cancellationToken);

    /// <summary>Withdraws a previous <see cref="RecordAsync"/> when the user clears, removes or reassigns these slots.</summary>
    public Task ForgetAsync(IReadOnlyCollection<DateTime> slotStarts, int forecastTaskId, CancellationToken cancellationToken = default) =>
        AdjustAsync(slotStarts, forecastTaskId, -1, cancellationToken);

    private async Task AdjustAsync(IReadOnlyCollection<DateTime> slotStarts, int forecastTaskId, int delta, CancellationToken cancellationToken)
    {
        var starts = slotStarts.Select(s => DateTime.SpecifyKind(s, DateTimeKind.Utc)).Distinct().Order().ToList();
        if (starts.Count == 0)
            return;
        var (first, last) = (starts[0], starts[^1] + EngineOptions.SlotLength);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ForecastTasks.AnyAsync(t => t.Id == forecastTaskId && t.ExternalId == Core.Forecast.ForecastTask.IgnoredExternalId, cancellationToken))
            return;

        var activities = await activityStore.GetRangeAsync(first - WeekProposalBuilder.LookBehind, last + WeekProposalBuilder.LookAhead, cancellationToken);
        var rules = await db.Rules.AsNoTracking().ToListAsync(cancellationToken);
        var resolved = resolver.Resolve(activities, rules, feedback: new Dictionary<string, IReadOnlyDictionary<int, int>>())
            .Where(r => r.Role != ActivityRole.Mask && r.GroupKey is not null)
            .ToList();

        var groupKeys = starts
            .Select(s => DominantGroup(resolved, new Interval(s, s + EngineOptions.SlotLength)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groupKeys.Count == 0)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await db.AssignmentFeedback
            .Where(f => f.ForecastTaskId == forecastTaskId && groupKeys.Contains(f.GroupKey))
            .ToDictionaryAsync(f => f.GroupKey, StringComparer.Ordinal, cancellationToken);
        foreach (var key in groupKeys)
        {
            if (!existing.TryGetValue(key, out var row))
            {
                if (delta < 0)
                    continue;
                row = new AssignmentFeedback { GroupKey = key, ForecastTaskId = forecastTaskId };
                db.AssignmentFeedback.Add(row);
            }
            row.Count += delta;
            row.LastUtc = now;
            if (row.Count <= 0)
                db.AssignmentFeedback.Remove(row);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? DominantGroup(IReadOnlyList<ResolvedActivity> resolved, Interval slot) =>
        resolved
            .Where(r => r.Window.Overlaps(slot))
            .GroupBy(r => r.GroupKey!, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Time: g.Sum(r => r.Window.Clip(slot).Length.Ticks)))
            .Where(g => g.Time > 0)
            .OrderByDescending(g => g.Time)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
}