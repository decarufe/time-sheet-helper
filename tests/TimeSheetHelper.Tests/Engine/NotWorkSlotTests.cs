using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>"Pas du travail" = <c>AssignSlotStartsAsync(starts, null)</c>: the activities stay, the slot just does not count. Not to be confused with deleting a slot.</summary>
public sealed class NotWorkSlotTests : IAsyncLifetime
{
    private EngineTestHost _host = null!;
    private WeekService _sut = null!;
    private ForecastTask _dev = null!;
    private ForecastTask _support = null!;

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost(new() { ["Engine:TargetHoursPerDay"] = "7.5" });
        _sut = _host.Get<WeekService>();
        _dev = await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc.");
        _support = await _host.AddTaskAsync("Fabrikam", "Support");
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "contoso/api", _dev.Id);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static ActivityEvent Code(DateTime start, DateTime end, string repo) =>
        Fx.Act(ActivitySource.VsCode, start, end, a => { a.Repo = repo; a.Title = "Program.cs - " + repo; a.ProcessName = "Code"; });

    private async Task<SlotAssignment> SlotAsync(DateTime startUtc) =>
        (await _sut.GetDayTimelineAsync(Fx.Monday)).Slots.Single(s => s.SlotStartUtc == startUtc);

    [Fact]
    public async Task Not_work_keeps_the_activities_of_the_slot_visible_and_out_of_the_totals()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));

        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], null);

        var slot = await SlotAsync(Fx.Mon(9));
        Assert.Equal(SlotKind.ExcludedByOverride, slot.Kind);
        Assert.NotEmpty(slot.Evidence);
        Assert.False(slot.IsWork);
        await using (var db = _host.Db())
            Assert.Equal(1, await db.Activities.CountAsync());
        Assert.Equal(0.75m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
    }

    [Fact]
    public async Task A_not_work_slot_can_be_reassigned_to_a_task_later()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], null);

        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], _support.Id, "Revue");

        var slot = await SlotAsync(Fx.Mon(9));
        Assert.Equal((SlotKind.Override, _support.Id), (slot.Kind, slot.TaskId));
        Assert.NotEmpty(slot.Evidence);
        Assert.Equal(1m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
    }

    [Fact]
    public async Task Not_work_and_deleted_slots_are_told_apart_in_the_model_and_in_the_database()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));

        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], null);
        await _sut.RemoveSlotsAsync([Fx.Mon(9, 15)]);

        var notWork = await SlotAsync(Fx.Mon(9));
        var deleted = await SlotAsync(Fx.Mon(9, 15));
        Assert.Equal((SlotKind.ExcludedByOverride, true), (notWork.Kind, notWork.Evidence.Count > 0));
        Assert.Equal((SlotKind.Removed, false), (deleted.Kind, deleted.Evidence.Count > 0));
        await using var db = _host.Db();
        Assert.Equal(Fx.Mon(9), (await db.SlotOverrides.SingleAsync()).SlotStartUtc);
        Assert.Equal(Fx.Mon(9, 15), (await db.RemovedSlots.SingleAsync()).SlotStartUtc);
    }

    [Fact]
    public async Task Not_work_on_a_slot_of_an_unclassified_group_takes_it_out_of_the_group_minutes()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web")); // no rule: unclassified

        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], null);

        var week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Equal(45, Assert.Single(week.UnclassifiedGroups).Minutes);
        Assert.Equal(0.75m, week.Days[0].UnclassifiedHours);
    }
}
