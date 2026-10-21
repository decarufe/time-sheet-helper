using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using TimeSheetHelper.App.Updates;
using TimeSheetHelper.Core;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.App.About;

/// <summary>Gathers the "À propos" information and erases old activity on request.</summary>
public sealed class AboutService(ActivityStore activities, UpdateService updates, IOptions<UpdateOptions> options, TimeProvider clock)
{
    public async Task<AboutInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var footprint = await activities.GetFootprintAsync(cancellationToken);
        var folder = DataPaths.Root;
        var database = Path.Combine(folder, "data.db");
        return new AboutInfo(
            updates.CurrentVersion.ToString(),
            RuntimeInformation.FrameworkDescription,
            AboutFormat.RepositoryUrl(options.Value.Repository),
            AboutFormat.License,
            folder,
            database,
            DatabaseBytes(database),
            footprint.Count,
            footprint.ApproxBytes);
    }

    /// <summary>Erases activities older than <paramref name="days"/> days (everything when null) and returns how many were removed.</summary>
    public Task<int> ClearAsync(int? days, CancellationToken cancellationToken = default) =>
        activities.DeleteOlderThanAsync(days is { } d ? clock.GetUtcNow().UtcDateTime.AddDays(-d) : null, cancellationToken);

    // The write-ahead log holds recent data, so it counts toward what the database occupies.
    private static long DatabaseBytes(string database) =>
        new[] { database, database + "-wal" }
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
}
