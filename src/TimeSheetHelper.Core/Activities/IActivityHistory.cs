namespace TimeSheetHelper.Core.Activities;

/// <summary>Read access to already-recorded activities (used to discover repositories the user worked in).</summary>
public interface IActivityHistory
{
    Task<IReadOnlyList<ActivityEvent>> GetRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
}
