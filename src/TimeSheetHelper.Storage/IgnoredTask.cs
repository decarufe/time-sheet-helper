using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Forecast;

namespace TimeSheetHelper.Storage;

public static class IgnoredTask
{
    /// <summary>Creates the built-in "Ignoré" task when it is missing and returns it.</summary>
    public static async Task<ForecastTask> EnsureAsync(TimeSheetDbContext db, DateTime nowUtc, CancellationToken ct = default)
    {
        var existing = await db.ForecastTasks.FirstOrDefaultAsync(t => t.ExternalId == ForecastTask.IgnoredExternalId, ct);
        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                await db.SaveChangesAsync(ct);
            }
            return existing;
        }

        var task = new ForecastTask
        {
            ExternalId = ForecastTask.IgnoredExternalId,
            ProjectName = ForecastTask.IgnoredLabel,
            TaskName = ForecastTask.IgnoredLabel,
            IsActive = true,
            LastSeenUtc = nowUtc,
        };
        db.ForecastTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }
}
