using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// WP12 — the anti-duplicate guarantees of the day engine (plan §1.3), end to end: the draft in the database, the lock, the planner, the writer
/// and an in-memory Forecast that counts every write call. A guarantee holds when the write counter does not move.
/// </summary>
public sealed class DaySubmissionIdempotenceTests : IDisposable
{
    private static readonly DateOnly Monday = DaySubmissionHost.Monday;

    private readonly DaySubmissionHost _host = new();

    private FakeForecastWriteApi Server => _host.Server;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task G1_SecondSendWritesNothing()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1.5m));
        var service = _host.Service();
        var first = await service.SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var writesAfterFirst = Server.WriteCount;

        var second = await service.SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var third = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal((DayStatus.Sent, 2, 2), (first.Status, first.Written, writesAfterFirst));
        Assert.Equal((DayStatus.Sent, 0), (second.Status, second.Written));
        Assert.Equal((DayStatus.Sent, 0), (third.Status, third.Written));
        Assert.Equal(writesAfterFirst, Server.WriteCount);
        Assert.Equal([(200, 90), (3382, 120)], _host.ForecastOn(Monday));
        var day = await _host.DayAsync(Monday);
        Assert.Equal(DayFingerprint.Compute(Monday, [(_host.Dev.Id, 2m), (_host.Meetings.Id, 1.5m)]), day!.Fingerprint);
    }

    [Fact]
    public async Task G1_UnchangedDay_ShowsNothingToSend()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        var plan = await _host.Service().PlanAsync(Monday);

        Assert.Equal(DayGate.NothingNew, plan.Gate);
        Assert.False(plan.IsReady);
        Assert.Equal(DayStatus.Sent, plan.Status);
        var cell = Assert.Single(plan.Cells);
        Assert.Equal((CellState.Unchanged, CellOperation.None), (cell.State, cell.Operation));
        Assert.Equal(1, Server.WriteCount);
    }

    [Fact]
    public async Task G1_DayAlreadyInForecastBeforeAnySend_IsNotCreatedAgain()
    {
        Server.Add(Monday, 2m, 3382);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));

        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(0, result.Written);
        Assert.Equal(0, Server.WriteCount);
        Assert.Equal([(3382, 120)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G5_ModifiedDay_UpdatesExistingEntry()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var entryId = Assert.Single(Server.Entries).Id;
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.SendChanges, plan.Gate);
        Assert.Equal((CellState.Modified, CellOperation.Update), (plan.Cells[0].State, plan.Cells[0].Operation));
        Assert.Equal((DayStatus.Sent, 1), (result.Status, result.Written));
        Assert.Equal((1, 1, 0), (Server.Creates, Server.Updates, Server.Deletes));
        var entry = Assert.Single(Server.Entries);
        Assert.Equal((entryId, 180), (entry.Id, entry.Minutes));
        Assert.Equal(DayFingerprint.Compute(Monday, [(_host.Dev.Id, 3m)]), (await _host.DayAsync(Monday))!.Fingerprint);
    }

    [Fact]
    public async Task G5_ReducedToZero_DeletesTheEntryAndNeverCreates()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        await _host.SetDraftAsync(Monday, (_host.Meetings, 1m));

        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var again = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal((DayStatus.Sent, 1), (result.Status, result.Written));
        Assert.Equal(0, again.Written);
        Assert.Equal((2, 0, 1), (Server.Creates, Server.Updates, Server.Deletes));
        Assert.Equal([(200, 60)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G5_UpdateToolMissing_BlocksAndNeverCreates()
    {
        Server.WithoutEditTools();
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var batch = await _host.Service().SendReadyAsync(Monday);

        Assert.False(plan.IsReady);
        Assert.Equal(DayGate.ResolveConflicts, plan.Gate);
        var cell = Assert.Single(plan.Cells);
        Assert.Equal((CellState.Modified, CellOperation.None), (cell.State, cell.Operation));
        Assert.NotNull(cell.BlockedReason);
        Assert.Equal(0, result.Written);
        Assert.Empty(batch.Sent);
        Assert.Contains(batch.SkippedDays, s => s.Date == Monday);
        Assert.Equal(1, Server.WriteCount);
        Assert.Equal([(3382, 120)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G5_DeleteToolMissing_BlocksAndNeverRewrites()
    {
        Server.WithoutEditTools();
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        await _host.SetDraftAsync(Monday, (_host.Meetings, 1m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.ResolveConflicts, plan.Gate);
        Assert.Contains(plan.Cells, c => c.State == CellState.Deleted && c.Operation == CellOperation.None && c.BlockedReason is not null);
        Assert.Equal(0, result.Written);
        Assert.Equal(2, Server.WriteCount);
        Assert.Equal([(200, 60), (3382, 120)], _host.ForecastOn(Monday));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G3_InterruptedSend_ResumeDoesNotDuplicate(bool keptByForecast)
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m), (_host.Support, 1.5m));
        Server.TimeoutOnWrite(2, applied: keptByForecast);

        var interrupted = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var afterInterruption = await _host.DayAsync(Monday);
        var plan = await _host.Service().PlanAsync(Monday);
        var resumed = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var again = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayStatus.Partial, interrupted.Status);
        Assert.Equal(DayStatus.Partial, afterInterruption!.Status);
        Assert.Null(afterInterruption.InProgressSinceUtc);
        Assert.Equal(DayGate.Resume, plan.Gate);
        Assert.Equal(DayStatus.Sent, resumed.Status);
        Assert.Equal(0, again.Written);
        Assert.Equal(keptByForecast ? 3 : 4, Server.Creates);
        Assert.Equal([(200, 60), (3382, 120), (5100, 90)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G3_CancelledSend_ResumeCreatesOnlyWhatIsMissing()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m), (_host.Support, 1.5m));
        var service = _host.Service();
        var (reached, release) = Server.HoldWrite(2);

        var sending = service.SendAsync(Monday, DaySubmissionHost.NoResolutions);
        await reached;
        await service.CancelAsync(Monday);
        release();
        var cancelled = await sending;
        var resumed = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayStatus.Partial, cancelled.Status);
        Assert.Contains(cancelled.Cells, c => c.Issue == ForecastCellIssue.NotAttempted);
        Assert.Equal(DayStatus.Sent, resumed.Status);
        Assert.Equal([(200, 60), (3382, 120), (5100, 90)], _host.ForecastOn(Monday));
        Assert.Equal(Server.Creates, Server.Entries.Count + 1);
    }

    [Fact]
    public async Task G3_CrashedSend_StaleLockIsResumedFromForecast()
    {
        // The application died in the middle of the send: the day stayed "in progress" for more than 10 minutes, its first creation was made.
        Server.Add(Monday, 2m, 3382);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m));
        var crashedAt = _host.Time.GetUtcNow().UtcDateTime - DayLock.StaleAfter - TimeSpan.FromMinutes(1);
        await _host.SeedDayAsync(Monday, DayStatus.InProgress, crashedAt);

        var plan = await _host.Service().PlanAsync(Monday);
        var resumed = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.Resume, plan.Gate);
        Assert.Equal((DayStatus.Sent, 1), (resumed.Status, resumed.Written));
        Assert.Equal((1, 1), (Server.Creates, Server.WriteCount));
        Assert.Equal([(200, 60), (3382, 120)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G3_RecentLockOfAnotherProcess_WritesNothing()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.SeedDayAsync(Monday, DayStatus.InProgress, _host.Time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(1));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.InProgress, plan.Gate);
        Assert.Equal((DayStatus.InProgress, 0), (result.Status, result.Written));
        Assert.Equal(0, Server.WriteCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G4_IncompleteRead_WritesNothing(bool truncated)
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        Server.TruncatedRead = truncated;
        Server.ReadFails = !truncated;

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var batch = await _host.Service().SendReadyAsync(Monday);

        Assert.Equal(DayGate.ReadForecastFirst, plan.Gate);
        Assert.All(plan.Cells, c => Assert.Equal((CellState.NotAttempted, CellOperation.None), (c.State, c.Operation)));
        Assert.Equal(0, result.Written);
        Assert.Empty(batch.Sent);
        Assert.Contains(batch.SkippedDays, s => s.Date == Monday);
        Assert.Equal(0, Server.WriteCount);
        Assert.NotEqual(DayStatus.InProgress, (await _host.DayAsync(Monday))!.Status);
    }

    [Theory]
    [InlineData("APPROVED", false)]
    [InlineData(null, true)]
    public async Task ApprovedOrInvoicedDay_IsStillSent(string? approval, bool invoiced)
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var dev = Server.Entries.Single(e => e.TaskNumber == 3382);
        Server.Entries[Server.Entries.IndexOf(dev)] = dev with { ApprovalStatus = approval, Invoiced = invoiced };
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m), (_host.Support, 1m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.True(plan.IsReady);
        Assert.Equal((DayStatus.Sent, 1), (result.Status, result.Written));
        Assert.Equal([(200, 60), (3382, 120), (5100, 60)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G8_IgnoredTask_NeverWritten()
    {
        await _host.IgnoreForTheWeekAsync(_host.Support, true);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Training, 3m), (_host.Ignored, 1m), (_host.Support, 1m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var again = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(CellOperation.Create, plan.Cells.Single(c => c.ForecastTaskId == _host.Dev.Id).Operation);
        Assert.All(plan.Cells.Where(c => c.ForecastTaskId != _host.Dev.Id), c => Assert.Equal(CellOperation.None, c.Operation));
        Assert.Equal((DayStatus.Sent, 1), (result.Status, result.Written));
        Assert.Equal(0, again.Written);
        Assert.Equal(1, Server.WriteCount);
        Assert.Equal([(3382, 120)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G8_TaskIgnoredAfterSending_IsNeitherDeletedNorRewritten()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Support, 1m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        await _host.IgnoreForTheWeekAsync(_host.Support, true);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Support, 4m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(CellState.Ignored, plan.Cells.Single(c => c.ForecastTaskId == _host.Support.Id).State);
        Assert.Equal(0, result.Written);
        Assert.Equal(2, Server.WriteCount);
        Assert.Equal([(3382, 120), (5100, 60)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G11_MigratedDay_MatchingForecast_BecomesSentWithoutWriting()
    {
        // A day sent by the former weekly submission: migrated without cells nor fingerprint, Forecast holds exactly the draft.
        Server.Add(Monday, 2m, 3382);
        Server.Add(Monday, 1m, 200);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Meetings, 1m));
        await _host.SeedDayAsync(Monday, DayStatus.SentUnknownFingerprint);

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var again = await _host.Service().PlanAsync(Monday);

        Assert.Equal((DayGate.NothingNew, DayStatus.Sent), (plan.Gate, plan.Status));
        Assert.DoesNotContain(plan.Cells, c => c.State == CellState.New);
        Assert.Equal((DayStatus.Sent, 0), (result.Status, result.Written));
        Assert.Equal(0, Server.WriteCount);
        var day = await _host.DayAsync(Monday);
        Assert.Equal(DayStatus.Sent, day!.Status);
        Assert.Equal(DayFingerprint.Compute(Monday, [(_host.Dev.Id, 2m), (_host.Meetings.Id, 1m)]), day.Fingerprint);
        Assert.Equal(2, day.Cells.Count);
        Assert.Equal(DayGate.NothingNew, again.Gate);
    }
}
