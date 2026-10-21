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
            if (await db.Activities.AnyAsync(a => a.Id == activity.Id, cancellationToken))
            {
                db.Activities.Update(activity);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            // The user deleted this segment while the collector still holds it: store the rest as a new row
            // (the deleted slots stay masked by their tombstones).
            activity.Id = 0;
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

    /// <summary>Number of stored activities and an estimate of the bytes they occupy (text columns plus a fixed per-row overhead).</summary>
    public async Task<ActivityFootprint> GetFootprintAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.Activities.LongCountAsync(cancellationToken);
        if (count == 0)
            return new ActivityFootprint(0, 0);

        var textBytes = await db.Database.SqlQuery<long>($"""
            SELECT COALESCE(SUM(
                LENGTH(CAST(Source AS BLOB)) + LENGTH(CAST(COALESCE(Kind, '') AS BLOB)) + LENGTH(CAST(COALESCE(ProcessName, '') AS BLOB))
                + LENGTH(CAST(COALESCE(Title, '') AS BLOB)) + LENGTH(CAST(COALESCE(FilePath, '') AS BLOB)) + LENGTH(CAST(COALESCE(Folder, '') AS BLOB))
                + LENGTH(CAST(COALESCE(Url, '') AS BLOB)) + LENGTH(CAST(COALESCE(Repo, '') AS BLOB)) + LENGTH(CAST(COALESCE(Project, '') AS BLOB))
                + LENGTH(CAST(COALESCE(Subject, '') AS BLOB)) + LENGTH(CAST(COALESCE(Participants, '') AS BLOB)) + LENGTH(CAST(COALESCE(ExternalId, '') AS BLOB))
            ), 0) AS Value FROM Activities
            """).SingleAsync(cancellationToken);
        return new ActivityFootprint(count, textBytes + count * ActivityFootprint.RowOverheadBytes);
    }

    /// <summary>
    /// Erases stored activities that started before <paramref name="beforeUtc"/> (all of them when null) and returns how many were removed.
    /// Manual assignments, rules, drafts and submissions are not touched.
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(DateTime? beforeUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Activities.AsQueryable();
        if (beforeUtc is { } cutoff)
            query = query.Where(a => a.StartUtc < cutoff);
        return await query.ExecuteDeleteAsync(cancellationToken);
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
