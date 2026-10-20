using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.App;

/// <summary>Smoke tests of the day engine against the in-memory Forecast server (the full anti-duplicate battery is WP12).</summary>
public sealed class DaySubmissionServiceTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeForecastClient _server = new();

    // Forecast's registrations name their project "p740": the planner attributes them to the task through its project.
    private readonly ForecastTask _dev = new() { ExternalId = "T3382", ProjectName = "p740", TaskName = "Développement" };
    private readonly ForecastTask _review = new() { ExternalId = "T3383", ProjectName = "p740", TaskName = "Revue" };
    private readonly ForecastTask _meetings = new() { ExternalId = "T200", ProjectName = "p12", TaskName = "Réunions" };

    public DaySubmissionServiceTests()
    {
        _connection.Open();
        using var db = Db();
        db.Database.EnsureCreated();
        db.ForecastTasks.AddRange(_dev, _review, _meetings);
        db.SaveChanges();
        db.TimesheetDrafts.Add(new TimesheetDraft
        {
            WeekStart = Monday,
            Entries = [new TimesheetEntry { Date = Monday, ForecastTaskId = _dev.Id, Hours = 2m }],
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext Db() => new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private DaySubmissionService Service()
    {
        var factory = new Factory(Db);
        var writer = new ForecastTimesheetWriter(
            new ForecastApi(new FakeForecastConnector(_server), Options.Create(new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            factory, NullLogger<ForecastTimesheetWriter>.Instance);
        return new DaySubmissionService(factory, writer, new DayLock(factory, TimeProvider.System), TimeProvider.System,
            NullLogger<DaySubmissionService>.Instance);
    }

    [Fact]
    public async Task A_day_sent_twice_is_written_once()
    {
        var service = Service();

        var first = await service.SendAsync(Monday, new Dictionary<int, ConflictResolution>());
        var second = await service.SendAsync(Monday, new Dictionary<int, ConflictResolution>());

        Assert.Equal((DayStatus.Sent, 1), (first.Status, first.Written));
        Assert.Equal((DayStatus.Sent, 0), (second.Status, second.Written));
        Assert.Single(_server.Creates);
        Assert.Equal(120, Assert.Single(_server.Entries).Minutes);
        Assert.Equal(DayGate.NothingNew, (await service.PlanAsync(Monday)).Gate);

        await using var db = Db();
        var day = await db.DaySubmissions.Include(d => d.Cells).SingleAsync();
        Assert.Equal((DayStatus.Sent, (DateTime?)null), (day.Status, day.InProgressSinceUtc));
        Assert.Equal(DayFingerprint.Compute(Monday, [(_dev.Id, 2m)]), day.Fingerprint);
        Assert.Equal((_dev.Id, 2m), (Assert.Single(day.Cells).ForecastTaskId, day.Cells[0].SentHours));
        Assert.NotEmpty(await service.GetJournalAsync(Monday));
    }

    [Fact]
    public async Task An_incomplete_read_of_Forecast_writes_nothing()
    {
        _server.AlwaysHasMore = true;

        var result = await Service().SendAsync(Monday, new Dictionary<int, ConflictResolution>());

        Assert.Equal(0, result.Written);
        Assert.Empty(_server.Creates);
        Assert.NotEqual(DayStatus.InProgress, (await Db().DaySubmissions.SingleAsync()).Status);
    }

    private async Task SetDraftAsync(DateOnly date, params (ForecastTask Task, decimal Hours)[] cells)
    {
        await using var db = Db();
        var draft = await db.TimesheetDrafts.Include(d => d.Entries).SingleAsync();
        draft.Entries.RemoveAll(e => e.Date == date);
        draft.Entries.AddRange(cells.Select(c => new TimesheetEntry { Date = date, ForecastTaskId = c.Task.Id, Hours = c.Hours }));
        await db.SaveChangesAsync();
    }

    /// <summary>Offers the update and delete tools; the registrations are found by their position (the fake numbers them from 54000000).</summary>
    private void WithEditTools()
    {
        _server.Tools.Add(ToolInfo("update_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"},"minutes":{"type":"integer"}},"required":["time_entry_id","minutes"]}"""));
        _server.Tools.Add(ToolInfo("delete_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"}},"required":["time_entry_id"]}"""));
        _server.Intercept = (tool, args) =>
        {
            if (tool is not ("update_time_entry" or "delete_time_entry"))
                return null;
            var index = (int)(Convert.ToInt64(args["time_entry_id"], System.Globalization.CultureInfo.InvariantCulture) - 54000000);
            if (tool == "delete_time_entry")
                _server.Entries.RemoveAt(index);
            else
            {
                var entry = _server.Entries[index];
                _server.Entries[index] = (entry.ProjectId, entry.Date, Convert.ToInt32(args["minutes"], System.Globalization.CultureInfo.InvariantCulture), entry.PersonId);
            }
            using var doc = JsonDocument.Parse("""{"ok":true}""");
            return new ForecastToolResult(false, """{"ok":true}""", doc.RootElement.Clone());
        };
    }

    private static ForecastToolInfo ToolInfo(string name, string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return new ForecastToolInfo(name, null, doc.RootElement.Clone());
    }

    private static readonly IReadOnlyDictionary<int, ConflictResolution> NoResolutions = new Dictionary<int, ConflictResolution>();

    [Fact]
    public async Task Hours_of_an_ambiguous_project_are_kept_and_the_other_projects_are_still_created()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 300, FakeForecastClient.MyId));
        await SetDraftAsync(Monday, (_dev, 2m), (_review, 2m), (_meetings, 1m));

        var result = await Service().SendAsync(Monday, new Dictionary<int, ConflictResolution>
        {
            [_dev.Id] = ConflictResolution.Keep,
            [_review.Id] = ConflictResolution.Keep,
        });

        Assert.Equal(1, result.Written);
        Assert.Equal(200, Assert.Single(_server.Creates).GetProperty("task_id").GetInt64());
        Assert.Equal([(FakeForecastClient.DevProject, 300), (FakeForecastClient.MeetingsProject, 60)], _server.Entries.Select(e => (e.ProjectId, e.Minutes)));
    }

    [Fact]
    public async Task A_deleted_cell_is_not_recreated_by_the_creations_of_the_same_send()
    {
        WithEditTools();
        await Service().SendAsync(Monday, NoResolutions);
        await SetDraftAsync(Monday, (_meetings, 1m));

        var result = await Service().SendAsync(Monday, NoResolutions);

        Assert.Equal((DayStatus.Sent, 2), (result.Status, result.Written));
        Assert.Equal([3382L, 200L], _server.Creates.Select(c => c.GetProperty("task_id").GetInt64()));
        var remaining = Assert.Single(_server.Entries);
        Assert.Equal((FakeForecastClient.MeetingsProject, 60), (remaining.ProjectId, remaining.Minutes));
    }

    [Fact]
    public async Task A_modified_cell_does_not_conflict_with_the_creations_of_the_same_send()
    {
        WithEditTools();
        await Service().SendAsync(Monday, NoResolutions);
        await SetDraftAsync(Monday, (_dev, 3m), (_review, 1m));

        var result = await Service().SendAsync(Monday, NoResolutions);

        Assert.Equal(DayStatus.Sent, result.Status);
        Assert.DoesNotContain(result.Cells, c => c.Status == ForecastCellStatus.Mismatch);
        Assert.Equal([(FakeForecastClient.DevProject, 180), (FakeForecastClient.DevProject, 60)], _server.Entries.Select(e => (e.ProjectId, e.Minutes)));
    }

    [Fact]
    public async Task A_day_that_cannot_be_planned_does_not_stop_the_days_after_it()
    {
        await SetDraftAsync(Monday.AddDays(1), (_dev, 1m));
        _server.Intercept = (tool, args) =>
            tool == "get_time_analysis" && (string)args["start_date"]! == Monday.ToString("yyyy-MM-dd") && (string)args["end_date"]! == Monday.ToString("yyyy-MM-dd")
                ? throw new InvalidOperationException("boom")
                : null;

        var result = await Service().SendReadyAsync(Monday);

        Assert.Equal(Monday.AddDays(1), Assert.Single(result.Sent).Date);
        Assert.Contains(result.SkippedDays, s => s.Date == Monday);
        Assert.Single(_server.Entries);
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }
}
