using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// WP12 — G6: a conflict (Forecast holds other hours than the draft, entered outside the application) is never overwritten without an explicit
/// choice; each choice writes exactly what it says, and never a second registration.
/// </summary>
public sealed class ConflictResolutionTests : IDisposable
{
    private static readonly DateOnly Monday = DaySubmissionHost.Monday;

    private readonly DaySubmissionHost _host = new();

    private FakeForecastWriteApi Server => _host.Server;

    public void Dispose() => _host.Dispose();

    /// <summary>Forecast holds 1 h of development entered by hand, the draft wants 3 h; meetings are new.</summary>
    private async Task<FakeForecastWriteApi.Registration> ConflictAsync()
    {
        var entry = Server.Add(Monday, 1m, 3382);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m), (_host.Meetings, 1m));
        return entry;
    }

    private Dictionary<int, ConflictResolution> Choose(ConflictResolution resolution) => new() { [_host.Dev.Id] = resolution };

    [Fact]
    public async Task G6_Conflict_IsPlannedWithEveryChoice_AndNoOperation()
    {
        await ConflictAsync();

        var plan = await _host.Service().PlanAsync(Monday);

        Assert.Equal((DayGate.ResolveConflicts, DayStatus.Conflict), (plan.Gate, plan.Status));
        var cell = plan.Cells.Single(c => c.ForecastTaskId == _host.Dev.Id);
        Assert.Equal((CellState.Conflict, CellOperation.None), (cell.State, cell.Operation));
        Assert.Equal([ConflictResolution.Keep, ConflictResolution.AcceptForecast, ConflictResolution.Complement, ConflictResolution.Remove], cell.AllowedResolutions);
        Assert.Equal(0, Server.WriteCount);
    }

    [Fact]
    public async Task G6_Conflict_WithoutChoice_NeverOverwrites()
    {
        await ConflictAsync();

        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var batch = await _host.Service().SendReadyAsync(Monday);

        Assert.Equal((DayStatus.Conflict, 0), (result.Status, result.Written));
        Assert.Empty(batch.Sent);
        Assert.Contains(batch.SkippedDays, s => s.Date == Monday);
        Assert.Equal(0, Server.WriteCount);
        Assert.Equal([(3382, 60)], _host.ForecastOn(Monday));
        Assert.Equal(3m, await _host.DraftHoursAsync(Monday, _host.Dev));
    }

    [Fact]
    public async Task G6_Conflict_ChoiceNotOffered_IsRefused()
    {
        // Without the update tool, Complement is not offered: asking for it anyway must not write (nor create a second registration).
        Server.WithoutEditTools();
        await ConflictAsync();

        var result = await _host.Service().SendAsync(Monday, Choose(ConflictResolution.Complement));

        Assert.Equal(0, result.Written);
        Assert.Equal(0, Server.WriteCount);
        Assert.Equal([(3382, 60)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G6_Conflict_Keep_WritesNothing()
    {
        await ConflictAsync();

        var result = await _host.Service().SendAsync(Monday, Choose(ConflictResolution.Keep));

        Assert.Equal(1, result.Written);
        Assert.Equal(["create"], Server.Writes);
        Assert.Equal([(200, 60), (3382, 60)], _host.ForecastOn(Monday));
        Assert.Equal(3m, await _host.DraftHoursAsync(Monday, _host.Dev));
    }

    [Fact]
    public async Task G6_Conflict_AcceptForecast_UpdatesDraftOnly()
    {
        await ConflictAsync();

        var result = await _host.Service().SendAsync(Monday, Choose(ConflictResolution.AcceptForecast));
        var again = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal((DayStatus.Sent, 1), (result.Status, result.Written));
        Assert.Equal(["create"], Server.Writes);
        Assert.Equal(1m, await _host.DraftHoursAsync(Monday, _host.Dev));
        Assert.Equal([(200, 60), (3382, 60)], _host.ForecastOn(Monday));
        Assert.Equal((DayStatus.Sent, 0), (again.Status, again.Written));
    }

    [Fact]
    public async Task G6_Conflict_Complement_CreatesOnlyDelta()
    {
        var entry = await ConflictAsync();

        var result = await _host.Service().SendAsync(Monday, Choose(ConflictResolution.Complement));
        var again = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        // The registration is raised to the draft's 3 h (1 h + the 2 h delta): one update, never a second registration of the task.
        Assert.Equal((DayStatus.Sent, 2), (result.Status, result.Written));
        Assert.Equal((1, 1, 0), (Server.Creates, Server.Updates, Server.Deletes));
        Assert.Equal(180, Server.Entries.Single(e => e.Id == entry.Id).Minutes);
        Assert.Single(Server.Entries, e => e.TaskNumber == 3382);
        Assert.Equal([(200, 60), (3382, 180)], _host.ForecastOn(Monday));
        Assert.Equal(0, again.Written);
    }

    [Fact]
    public async Task G6_Conflict_Remove_DeletesAfterConfirmation()
    {
        var entry = await ConflictAsync();

        var withoutChoice = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        var removed = await _host.Service().SendAsync(Monday, Choose(ConflictResolution.Remove));

        Assert.Equal(0, withoutChoice.Written);
        Assert.Equal(2, removed.Written);
        Assert.Equal(["delete", "create"], Server.Writes);
        Assert.DoesNotContain(Server.Entries, e => e.Id == entry.Id);
        Assert.Equal([(200, 60)], _host.ForecastOn(Monday));
    }

    [Fact]
    public async Task G6_ChangedInForecastAfterSending_IsAConflictNotAnUpdate()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        Server.ChangeElsewhere(Server.Entries.Single().Id, 4m);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);
        var result = await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        Assert.Equal(CellState.Conflict, Assert.Single(plan.Cells).State);
        Assert.Equal(0, result.Written);
        Assert.Equal(1, Server.WriteCount);
        Assert.Equal([(3382, 240)], _host.ForecastOn(Monday));
    }
}
