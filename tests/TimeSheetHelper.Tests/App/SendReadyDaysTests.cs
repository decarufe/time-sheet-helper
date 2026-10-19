using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Tests.App;

/// <summary>WP12 — G9: "send the ready days" writes only the ready days, reports the others, and a second run writes nothing.</summary>
public sealed class SendReadyDaysTests : IDisposable
{
    private static readonly DateOnly Monday = DaySubmissionHost.Monday;
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);
    private static readonly DateOnly Thursday = Monday.AddDays(3);
    private static readonly DateOnly Friday = Monday.AddDays(4);

    private readonly DaySubmissionHost _host = new();

    private FakeForecastWriteApi Server => _host.Server;

    public void Dispose() => _host.Dispose();

    /// <summary>Monday and Friday ready, Tuesday in conflict, Wednesday approved in Forecast, Thursday only ignored hours.</summary>
    private async Task WeekAsync()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m));
        Server.Add(Tuesday, 1m, 3382);
        await _host.SetDraftAsync(Tuesday, (_host.Dev, 3m));
        Server.Add(Wednesday, 4m, 200, approvalStatus: "APPROVED");
        await _host.SetDraftAsync(Wednesday, (_host.Dev, 2m));
        await _host.SetDraftAsync(Thursday, (_host.Training, 7.5m));
        await _host.SetDraftAsync(Friday, (_host.Support, 1.5m));
    }

    [Fact]
    public async Task G9_SendReady_SkipsConflictAndLockedDays()
    {
        await WeekAsync();

        var result = await _host.Service().SendReadyAsync(Monday);

        Assert.Equal([Monday, Friday], result.Sent.Select(s => s.Date));
        Assert.All(result.Sent, s => Assert.Equal(DayStatus.Sent, s.Status));
        Assert.Equal([Tuesday, Wednesday], result.SkippedDays.Select(s => s.Date));
        Assert.All(result.SkippedDays, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        Assert.Equal(3, Server.Creates);
        Assert.Equal(3, Server.WriteCount);
        Assert.Equal([(200, 60), (3382, 120)], _host.ForecastOn(Monday));
        Assert.Equal([(3382, 60)], _host.ForecastOn(Tuesday));
        Assert.Equal([(200, 240)], _host.ForecastOn(Wednesday));
        Assert.Empty(_host.ForecastOn(Thursday));
        Assert.Equal([(5100, 90)], _host.ForecastOn(Friday));
    }

    [Fact]
    public async Task G9_SendReady_RerunIsIdempotent()
    {
        await WeekAsync();
        await _host.Service().SendReadyAsync(Monday);
        var writes = Server.WriteCount;

        var again = await _host.Service().SendReadyAsync(Monday);

        Assert.Empty(again.Sent);
        Assert.Equal(writes, Server.WriteCount);
        Assert.Contains(again.SkippedDays, s => s.Date == Monday);
        Assert.Contains(again.SkippedDays, s => s.Date == Friday);
        Assert.DoesNotContain(again.SkippedDays, s => s.Date == Thursday);
    }

    [Fact]
    public async Task G9_SendReady_ResumesAnInterruptedDayWithoutDuplicating()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m), (_host.Support, 1.5m));
        await _host.SetDraftAsync(Tuesday, (_host.Dev, 1m));
        Server.TimeoutOnWrite(2, applied: true);

        var first = await _host.Service().SendReadyAsync(Monday);
        var second = await _host.Service().SendReadyAsync(Monday);
        var third = await _host.Service().SendReadyAsync(Monday);

        Assert.Equal(DayStatus.Partial, first.Sent.Single(s => s.Date == Monday).Status);
        Assert.Equal(DayStatus.Sent, first.Sent.Single(s => s.Date == Tuesday).Status);
        Assert.Equal([Monday], second.Sent.Select(s => s.Date));
        Assert.Equal(DayStatus.Sent, second.Sent[0].Status);
        Assert.Empty(third.Sent);
        Assert.Equal(4, Server.Creates);
        Assert.Equal([(200, 60), (3382, 120), (5100, 90)], _host.ForecastOn(Monday));
        Assert.Equal([(3382, 60)], _host.ForecastOn(Tuesday));
    }
}
