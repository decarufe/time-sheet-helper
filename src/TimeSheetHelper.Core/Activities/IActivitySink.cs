namespace TimeSheetHelper.Core.Activities;

public interface IActivitySink
{
    Task WriteAsync(ActivityEvent activity, CancellationToken cancellationToken = default);
}
