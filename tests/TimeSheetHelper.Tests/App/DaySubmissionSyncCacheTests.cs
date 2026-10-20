using TimeSheetHelper.Core.Forecast;

namespace TimeSheetHelper.Tests.App;

/// <summary>The last read of Forecast is kept by the singleton day engine so every window shares it.</summary>
public sealed class DaySubmissionSyncCacheTests : IDisposable
{
    private static readonly DateOnly Monday = DaySubmissionHost.Monday;

    private readonly DaySubmissionHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_cached_plan_does_not_read_Forecast_again()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        var service = _host.Service();
        await service.PlanAsync(Monday);
        var reads = _host.Server.ReadCount;

        var plan = await service.PlanCachedAsync(Monday);

        Assert.NotNull(plan);
        Assert.Equal(reads, _host.Server.ReadCount);
    }

    [Fact]
    public async Task A_sync_stamps_the_last_sync_time_and_notifies_the_windows()
    {
        var service = _host.Service();
        var notified = 0;
        service.Synced += () => notified++;
        Assert.Null(service.LastSyncedUtc);

        var ok = await service.SyncWeekAsync(Monday);

        Assert.True(ok);
        Assert.NotNull(service.LastSyncedUtc);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task A_send_always_reads_Forecast_again()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        var service = _host.Service();
        await service.PlanAsync(Monday);
        var reads = _host.Server.ReadCount;

        await service.SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.True(_host.Server.ReadCount > reads);
    }
}
