using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>Engine draft → Forecast writer: a slot deleted in the day view must never be registered in Forecast.</summary>
public sealed class SlotRemovalForecastTests : IAsyncLifetime
{
    private EngineTestHost _host = null!;
    private WeekService _sut = null!;
    private FakeForecastClient _server = null!;
    private FakeForecastConnector _connector = null!;
    private ForecastTask _dev = null!;
    private ForecastTask _review = null!;

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost();
        _sut = _host.Get<WeekService>();
        _server = new FakeForecastClient();
        _connector = new FakeForecastConnector(_server);

        // External ids match the tasks served by the fake Forecast (T3382 / T3383).
        await using (var db = _host.Db())
        {
            _dev = new ForecastTask { ExternalId = "T3382", ProjectName = "Portail", ClientName = "Acme", TaskName = "Développement" };
            _review = new ForecastTask { ExternalId = "T3383", ProjectName = "Portail", TaskName = "Revue" };
            db.ForecastTasks.AddRange(_dev, _review);
            await db.SaveChangesAsync();
        }
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "contoso/api", _dev.Id);
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "fabrikam/web", _review.Id);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private ForecastTimesheetWriter Writer() =>
        new(new ForecastApi(_connector, Options.Create(new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            _host.Get<IDbContextFactory<TimeSheetDbContext>>(), NullLogger<ForecastTimesheetWriter>.Instance);

    private static ActivityEvent Code(DateTime start, DateTime end, string repo) =>
        Fx.Act(ActivitySource.VsCode, start, end, a => { a.Repo = repo; a.Title = "Program.cs - " + repo; a.ProcessName = "Code"; });

    [Fact]
    public async Task A_removed_slot_is_left_out_of_the_hours_registered_in_forecast_even_when_the_draft_was_built_before()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        Assert.Equal(1m, Assert.Single((await _sut.RebuildDraftAsync(Fx.Monday)).Draft.Entries).Hours);

        await _sut.RemoveSlotsAsync([Fx.Mon(9, 15)]);
        var draft = (await _sut.RebuildDraftAsync(Fx.Monday)).Draft;
        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        var created = Assert.Single(_server.Creates);
        Assert.Equal((3382, 45), (created.GetProperty("task_id").GetInt64(), created.GetProperty("minutes").GetInt32()));
        Assert.Equal(45, _server.Entries.Sum(e => e.Minutes));
    }

    [Fact]
    public async Task A_task_whose_slots_were_all_removed_is_not_registered_but_the_others_are()
    {
        await _host.AddActivitiesAsync(
            Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"),
            Code(Fx.Mon(11), Fx.Mon(12), "fabrikam/web"));
        await _sut.RemoveSlotsAsync([Fx.Mon(11), Fx.Mon(11, 15), Fx.Mon(11, 30), Fx.Mon(11, 45)]);

        var draft = (await _sut.RebuildDraftAsync(Fx.Monday)).Draft;
        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        Assert.Equal(_dev.Id, Assert.Single(report.Cells).ForecastTaskId);
        var created = Assert.Single(_server.Creates);
        Assert.Equal((3382, 60), (created.GetProperty("task_id").GetInt64(), created.GetProperty("minutes").GetInt32()));
    }

    [Fact]
    public async Task When_every_slot_is_removed_nothing_is_sent_to_forecast()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15), Fx.Mon(9, 30), Fx.Mon(9, 45)]);

        var draft = (await _sut.RebuildDraftAsync(Fx.Monday)).Draft;
        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.Empty(draft.Entries);
        Assert.Empty(report.Cells);
        Assert.NotEmpty(report.Warnings);
        Assert.Equal(0, _connector.Connects);
        Assert.Empty(_server.Creates);
    }

    [Fact]
    public async Task A_slot_marked_not_work_is_also_left_out_of_the_hours_registered_in_forecast()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));

        await _sut.AssignSlotStartsAsync([Fx.Mon(9, 15)], null);
        var draft = (await _sut.RebuildDraftAsync(Fx.Monday)).Draft;
        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        Assert.Equal(45, Assert.Single(_server.Creates).GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task A_removed_slot_stays_out_of_forecast_after_the_collector_brings_the_same_activity_back()
    {
        var store = _host.Get<TimeSheetHelper.Storage.ActivityStore>();
        var activity = Code(Fx.Mon(9), Fx.Mon(10), "contoso/api");
        activity.Id = 0;
        activity.ExternalId = "seg-1";
        await store.WriteAsync(activity);
        await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15), Fx.Mon(9, 30), Fx.Mon(9, 45)]);

        var again = Code(Fx.Mon(9), Fx.Mon(10), "contoso/api");
        again.Id = 0;
        again.ExternalId = "seg-1";
        await store.WriteAsync(again);

        var draft = (await _sut.RebuildDraftAsync(Fx.Monday)).Draft;
        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.Empty(draft.Entries);
        Assert.Empty(report.Cells);
        Assert.Empty(_server.Creates);
    }
}
