namespace TimeSheetHelper.App.Services;

/// <summary>
/// Keeps the Forecast days of the current week warm in <see cref="IDaySubmissionService"/> (shared by every window of the tray
/// application): one read at start, then every <see cref="Interval"/>. Never asks for a login; any failure waits for the next turn.
/// </summary>
public sealed class ForecastRefresher(
    IDaySubmissionService submissions,
    ForecastRunner runner,
    TimeProvider timeProvider,
    ILogger<ForecastRefresher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(Interval, timeProvider);
        do
        {
            await RefreshOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    internal async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
            await runner.RunAsync("rafraîchissement de la semaine", ct => submissions.SyncWeekAsync(today, ct), cancellationToken);
        }
        catch (ForecastBusyException)
        {
            // An operation of the user runs: the next turn will do.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogInformation("Periodic Forecast refresh skipped ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
