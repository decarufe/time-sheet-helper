using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Storage;

public sealed class ActivityStoreHistory(ActivityStore store) : IActivityHistory
{
    public Task<IReadOnlyList<ActivityEvent>> GetRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        => store.GetRangeAsync(fromUtc, toUtc, cancellationToken);
}
