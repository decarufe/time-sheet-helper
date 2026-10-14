using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>"Supprimer" = <c>RemoveSlotsAsync</c>: activities are erased and the slots are tombstoned. Runs on a real SQLite engine.</summary>
public sealed class SlotRemovalTests : IAsyncLifetime
{
    private EngineTestHost _host = null!;
    private WeekService _sut = null!;
    private ForecastTask _dev = null!;
    private ForecastTask _support = null!;

    private static readonly DateOnly Tuesday = Fx.Monday.AddDays(1);

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost(new() { ["Engine:TargetHoursPerDay"] = "7.5" });
        _sut = _host.Get<WeekService>();
        _dev = await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc.");
        _support = await _host.AddTaskAsync("Fabrikam", "Support");
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "contoso/api", _dev.Id);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static ActivityEvent Code(DateTime start, DateTime end, string repo, string? externalId = null)
    {
        var activity = Fx.Act(ActivitySource.VsCode, start, end, a => { a.Repo = repo; a.Title = "Program.cs - " + repo; a.ProcessName = "Code"; a.ExternalId = externalId; });
        activity.Id = 0;
        return activity;
    }

    private static List<DateTime> Hour(int hour) => [Fx.Mon(hour), Fx.Mon(hour, 15), Fx.Mon(hour, 30), Fx.Mon(hour, 45)];

    private async Task<SlotAssignment> SlotAsync(DateOnly day, DateTime startUtc) =>
        (await _sut.GetDayTimelineAsync(day)).Slots.Single(s => s.SlotStartUtc == startUtc);

    private async Task<int> ActivityCountAsync()
    {
        await using var db = _host.Db();
        return await db.Activities.CountAsync();
    }

    [Fact]
    public async Task Removing_every_slot_of_an_activity_erases_it_and_empties_the_totals_the_list_and_the_draft()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        Assert.Equal(1m, Assert.Single((await _sut.RebuildDraftAsync(Fx.Monday)).Draft.Entries).Hours);

        var result = await _sut.RemoveSlotsAsync(Hour(9));

        Assert.Equal(new SlotRemovalResult(4, 1, 0), result);
        Assert.Equal(0, await ActivityCountAsync());
        var timeline = await _sut.GetDayTimelineAsync(Fx.Monday);
        Assert.All(Hour(9), start =>
        {
            var slot = timeline.Slots.Single(s => s.SlotStartUtc == start);
            Assert.Equal(SlotKind.Removed, slot.Kind);
            Assert.Empty(slot.Evidence);
        });
        Assert.Empty(timeline.Activities);
        Assert.Equal(0m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
        Assert.Empty((await _sut.RebuildDraftAsync(Fx.Monday)).Draft.Entries);
    }

    [Fact]
    public async Task An_activity_that_also_covers_live_slots_is_kept_but_ignored_inside_the_removed_slot()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));

        var result = await _sut.RemoveSlotsAsync([Fx.Mon(9)]);

        Assert.Equal(new SlotRemovalResult(1, 0, 1), result);
        Assert.Equal(1, await ActivityCountAsync());
        Assert.Equal(SlotKind.Removed, (await SlotAsync(Fx.Monday, Fx.Mon(9))).Kind);
        var live = await SlotAsync(Fx.Monday, Fx.Mon(9, 15));
        Assert.Equal((SlotKind.Activity, _dev.Id), (live.Kind, live.TaskId));
        Assert.Equal(0.75m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
    }

    [Fact]
    public async Task A_removed_slot_does_not_come_back_when_a_collector_brings_the_same_external_id_again()
    {
        var store = _host.Get<ActivityStore>();
        await store.WriteAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api", "seg-1"));
        await _sut.RemoveSlotsAsync(Hour(9));
        Assert.Equal(0, await ActivityCountAsync());

        await store.WriteAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api", "seg-1")); // next sync

        Assert.Equal(1, await ActivityCountAsync()); // stored again, but inert
        var timeline = await _sut.GetDayTimelineAsync(Fx.Monday);
        Assert.All(Hour(9), start => Assert.Equal(SlotKind.Removed, timeline.Slots.Single(s => s.SlotStartUtc == start).Kind));
        Assert.Empty(timeline.Activities);
        Assert.Equal(0m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
        Assert.Empty((await _sut.RebuildDraftAsync(Fx.Monday)).Draft.Entries);
    }

    [Fact]
    public async Task A_segment_deleted_while_its_collector_still_holds_it_keeps_growing_without_reviving_the_removed_slots()
    {
        var store = _host.Get<ActivityStore>();
        var segment = Code(Fx.Mon(9), Fx.Mon(9, 30), "contoso/api");
        await store.WriteAsync(segment);
        Assert.NotEqual(0, segment.Id);

        await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15)]);
        Assert.Equal(0, await ActivityCountAsync());

        segment.EndUtc = Fx.Mon(10); // the focus tracker checkpoints the same instance
        await store.WriteAsync(segment);

        var timeline = await _sut.GetDayTimelineAsync(Fx.Monday);
        Assert.Equal(SlotKind.Removed, timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9)).Kind);
        Assert.Equal(SlotKind.Removed, timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9, 15)).Kind);
        Assert.Equal(SlotKind.Activity, timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9, 30)).Kind);
        Assert.Equal(0.5m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
    }

    [Fact]
    public async Task Removing_a_hand_assigned_slot_drops_the_assignment_and_its_note()
    {
        await _sut.AssignSlotStartsAsync([Fx.Mon(9), Fx.Mon(9, 15)], _support.Id, "Revue");

        await _sut.RemoveSlotsAsync([Fx.Mon(9)]);

        await using (var db = _host.Db())
            Assert.Equal(Fx.Mon(9, 15), (await db.SlotOverrides.SingleAsync()).SlotStartUtc);
        var entry = Assert.Single((await _sut.RebuildDraftAsync(Fx.Monday)).Draft.Entries);
        Assert.Equal((_support.Id, 0.25m, "Revue"), (entry.ForecastTaskId, entry.Hours, entry.Notes));
    }

    [Fact]
    public async Task Removing_the_same_slot_twice_keeps_a_single_tombstone_and_the_second_call_deletes_nothing()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(9, 15), "contoso/api"));

        await _sut.RemoveSlotsAsync([Fx.Mon(9)]);
        var second = await _sut.RemoveSlotsAsync([Fx.Mon(9)]);

        Assert.Equal(new SlotRemovalResult(1, 0, 0), second);
        await using var db = _host.Db();
        Assert.Equal(Fx.Mon(9), (await db.RemovedSlots.SingleAsync()).SlotStartUtc);
    }

    [Fact]
    public async Task Removing_nothing_is_harmless()
    {
        Assert.Equal(new SlotRemovalResult(0, 0, 0), await _sut.RemoveSlotsAsync([]));

        await using var db = _host.Db();
        Assert.Equal(0, await db.RemovedSlots.CountAsync());
    }

    [Fact]
    public async Task A_removed_slot_can_still_be_assigned_by_hand_afterwards()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        await _sut.RemoveSlotsAsync([Fx.Mon(9)]);

        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], _support.Id);

        var slot = await SlotAsync(Fx.Monday, Fx.Mon(9));
        Assert.Equal((SlotKind.Override, _support.Id), (slot.Kind, slot.TaskId));
        Assert.Empty(slot.Evidence); // the erased activity is not resurrected
    }

    [Fact]
    public async Task Removing_slots_of_an_unclassified_group_shrinks_its_minutes_and_the_group_disappears_when_empty()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web")); // no rule: unclassified
        var week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Equal(60, Assert.Single(week.UnclassifiedGroups).Minutes);

        await _sut.RemoveSlotsAsync([Fx.Mon(9)]);
        week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Equal(45, Assert.Single(week.UnclassifiedGroups).Minutes);
        Assert.Equal(0.75m, week.Days[0].UnclassifiedHours);

        await _sut.RemoveSlotsAsync([Fx.Mon(9, 15), Fx.Mon(9, 30), Fx.Mon(9, 45)]);
        week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Equal(0m, week.TotalHours);
        Assert.Equal(0, await ActivityCountAsync());
    }

    [Fact]
    public async Task Removing_the_last_slot_before_midnight_leaves_the_next_day_untouched()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(23, 30), Fx.Local(Tuesday, 0, 30), "contoso/api"));

        var result = await _sut.RemoveSlotsAsync([Fx.Mon(23, 45)]);

        Assert.Equal(1, result.ActivitiesKept);
        var week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Equal((0.25m, 0.5m), (week.Days[0].TotalHours, week.Days[1].TotalHours));
        Assert.Equal(SlotKind.Activity, (await SlotAsync(Tuesday, Fx.Local(Tuesday, 0))).Kind);
    }

    [Fact]
    public async Task On_the_fall_back_day_removing_one_of_the_two_1_15_slots_leaves_the_other()
    {
        var fallBack = new DateOnly(2026, 11, 1); // Toronto: 01:00-02:00 happens twice
        var first = new DateTime(2026, 11, 1, 5, 15, 0, DateTimeKind.Utc);  // 01:15 EDT
        var second = new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc); // 01:15 EST
        await _host.AddActivitiesAsync(Code(new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), "contoso/api"));

        await _sut.RemoveSlotsAsync([second]);

        var timeline = await _sut.GetDayTimelineAsync(fallBack);
        Assert.Equal(SlotKind.Activity, timeline.Slots.Single(s => s.SlotStartUtc == first).Kind);
        Assert.Equal(SlotKind.Removed, timeline.Slots.Single(s => s.SlotStartUtc == second).Kind);
        Assert.Equal(1, timeline.Slots.Count(s => s.Kind == SlotKind.Removed));
        Assert.Equal(2.75m, (await _sut.GetWeekAsync(fallBack)).Days[6].TotalHours);
    }
}
