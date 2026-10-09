using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Collectors.DevOps.Common;

/// <summary>Read access to already-recorded activities (used to discover repositories the user worked in).</summary>
public interface IActivityHistory
{
    Task<IReadOnlyList<ActivityEvent>> GetRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
}

public sealed class ActivityStoreHistory(ActivityStore store) : IActivityHistory
{
    public Task<IReadOnlyList<ActivityEvent>> GetRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        => store.GetRangeAsync(fromUtc, toUtc, cancellationToken);
}
