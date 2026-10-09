using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Storage;

public sealed class ActivityStore(IDbContextFactory<TimeSheetDbContext> dbFactory) : IActivitySink
{
    public async Task WriteAsync(ActivityEvent activity, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Collectors hand back the same instance as a segment grows; once it has an Id it is an update.
        if (activity.Id != 0)
        {
            db.Activities.Update(activity);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (activity.ExternalId is not null)
        {
            var existing = await db.Activities.FirstOrDefaultAsync(
                a => a.Source == activity.Source && a.ExternalId == activity.ExternalId, cancellationToken);
            if (existing is not null)
            {
                activity.Id = existing.Id;
                db.Entry(existing).CurrentValues.SetValues(activity);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
        }

        db.Activities.Add(activity);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Activities overlapping [fromUtc, toUtc), ordered by start.</summary>
    public async Task<IReadOnlyList<ActivityEvent>> GetRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Activities.AsNoTracking()
            .Where(a => a.StartUtc < toUtc && (a.EndUtc ?? a.StartUtc) >= fromUtc)
            .OrderBy(a => a.StartUtc)
            .ToListAsync(cancellationToken);
    }
}
