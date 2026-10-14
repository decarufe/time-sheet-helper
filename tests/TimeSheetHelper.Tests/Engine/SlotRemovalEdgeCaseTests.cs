using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>Edge cases of "Supprimer" (<c>RemoveSlotsAsync</c>): cumulative removals, point events, other days, store re-writes, atomicity and learning.</summary>
public sealed class SlotRemovalEdgeCaseTests : IAsyncLifetime
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

    private static ActivityEvent Commit(DateTime at, string repo) =>
        Fx.Act(ActivitySource.GitHub, at, null, a => { a.Kind = "commit"; a.Repo = repo; });

    private async Task<SlotAssignment> SlotAsync(DateOnly day, DateTime startUtc) =>
        (await _sut.GetDayTimelineAsync(day)).Slots.Single(s => s.SlotStartUtc == startUtc);

    private async Task<int> ActivityCountAsync()
    {
        await using var db = _host.Db();
        return await db.Activities.CountAsync();
    }

    [Fact]
    public async Task Removing_the_slots_of_an_activity_in_two_steps_erases_it_only_at_the_last_step()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));

        var first = await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15)]);
        Assert.Equal(new SlotRemovalResult(2, 0, 1), first);
        Assert.Equal(1, await ActivityCountAsync());

        var second = await _sut.RemoveSlotsAsync([Fx.Mon(9, 30), Fx.Mon(9, 45)]);

        Assert.Equal(new SlotRemovalResult(2, 1, 0), second);
        Assert.Equal(0, await ActivityCountAsync());
    }

    [Fact]
    public async Task A_point_event_is_erased_only_when_every_slot_of_its_evidence_window_is_removed()
    {
        // The commit at 11:05 gets a 15 min window [10:50, 11:05): it reaches back into the 10:45 slot.
        await _host.AddActivitiesAsync(Commit(Fx.Mon(11, 5), "contoso/api"));

        var first = await _sut.RemoveSlotsAsync([Fx.Mon(11)]);
        Assert.Equal(new SlotRemovalResult(1, 0, 1), first);
        Assert.Equal(1, await ActivityCountAsync());

        var second = await _sut.RemoveSlotsAsync([Fx.Mon(10, 45)]);

        Assert.Equal(new SlotRemovalResult(1, 1, 0), second);
        Assert.Equal(0, await ActivityCountAsync());
    }

    [Fact]
    public async Task Removing_slots_counts_each_overlapping_activity_as_erased_or_kept_and_touches_only_those()
    {
        await _host.AddActivitiesAsync(
            Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"),                                                    // also lives in 9:30, 9:45: kept
            Fx.Act(ActivitySource.Focus, Fx.Mon(9), Fx.Mon(9, 30), a => { a.ProcessName = "WINWORD"; a.Folder = @"C:\clients\fabrikam"; }), // only 9:00, 9:15: erased
            Code(Fx.Mon(13), Fx.Mon(14), "contoso/api"));                                                  // elsewhere: untouched

        var result = await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15)]);

        Assert.Equal(new SlotRemovalResult(2, 1, 1), result);
        await using var db = _host.Db();
        var left = await db.Activities.OrderBy(a => a.StartUtc).ToListAsync();
        Assert.Equal([Fx.Mon(9), Fx.Mon(13)], left.Select(a => a.StartUtc).ToArray());
    }

    [Fact]
    public async Task Removing_a_whole_day_leaves_the_same_hours_of_the_next_day_untouched()
    {
        await _host.AddActivitiesAsync(
            Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"),
            Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "contoso/api"));

        var result = await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15), Fx.Mon(9, 30), Fx.Mon(9, 45)]);

        Assert.Equal(new SlotRemovalResult(4, 1, 0), result);
        var week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Equal((0m, 1m), (week.Days[0].TotalHours, week.Days[1].TotalHours));
        Assert.Equal(SlotKind.Activity, (await SlotAsync(Tuesday, Fx.Local(Tuesday, 9))).Kind);
        Assert.Equal(1, await ActivityCountAsync());
    }

    [Fact]
    public async Task A_start_without_utc_kind_removes_the_same_slot()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(9, 15), "contoso/api"));
        var unspecified = DateTime.SpecifyKind(Fx.Mon(9), DateTimeKind.Unspecified);

        await _sut.RemoveSlotsAsync([unspecified]);

        Assert.Equal(SlotKind.Removed, (await SlotAsync(Fx.Monday, Fx.Mon(9))).Kind);
        Assert.Equal(0, await ActivityCountAsync());
    }

    [Fact]
    public async Task Not_work_then_delete_on_the_same_slot_drops_the_not_work_override()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], null);

        await _sut.RemoveSlotsAsync([Fx.Mon(9)]);

        var slot = await SlotAsync(Fx.Monday, Fx.Mon(9));
        Assert.Equal(SlotKind.Removed, slot.Kind);
        Assert.Empty(slot.Evidence);
        await using var db = _host.Db();
        Assert.Equal(0, await db.SlotOverrides.CountAsync());
        Assert.Equal(1, await db.RemovedSlots.CountAsync());
    }

    [Fact]
    public async Task Clearing_the_assignment_of_a_removed_slot_never_brings_the_slot_or_its_activity_back()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));
        await _sut.RemoveSlotsAsync([Fx.Mon(9)]);
        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], null); // marked "pas du travail" afterwards

        var notWork = await SlotAsync(Fx.Monday, Fx.Mon(9));
        Assert.Equal(SlotKind.ExcludedByOverride, notWork.Kind);
        Assert.Empty(notWork.Evidence);

        await _sut.ClearSlotStartsAsync([Fx.Mon(9)]);

        var slot = await SlotAsync(Fx.Monday, Fx.Mon(9));
        Assert.Equal(SlotKind.Removed, slot.Kind);
        Assert.Empty(slot.Evidence);
        Assert.Equal(0.75m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
    }

    [Fact]
    public async Task A_segment_that_keeps_growing_after_a_partial_removal_updates_its_row_and_keeps_the_removed_slot_masked()
    {
        var store = _host.Get<ActivityStore>();
        var segment = Code(Fx.Mon(9), Fx.Mon(9, 30), "contoso/api", "seg-grow");
        await store.WriteAsync(segment);
        await _sut.RemoveSlotsAsync([Fx.Mon(9)]); // 9:15 is still alive: the row stays

        segment.EndUtc = Fx.Mon(10);
        await store.WriteAsync(segment);

        Assert.Equal(1, await ActivityCountAsync());
        Assert.Equal(SlotKind.Removed, (await SlotAsync(Fx.Monday, Fx.Mon(9))).Kind);
        Assert.Equal(SlotKind.Activity, (await SlotAsync(Fx.Monday, Fx.Mon(9, 15))).Kind);
        Assert.Equal(SlotKind.Activity, (await SlotAsync(Fx.Monday, Fx.Mon(9, 45))).Kind);
        Assert.Equal(0.75m, (await _sut.GetWeekAsync(Fx.Monday)).Days[0].TotalHours);
    }

    [Fact]
    public async Task A_stale_collector_instance_of_an_erased_segment_does_not_duplicate_the_row_a_new_sync_already_stored()
    {
        var store = _host.Get<ActivityStore>();
        var stale = Code(Fx.Mon(9), Fx.Mon(9, 30), "contoso/api", "seg-dup");
        await store.WriteAsync(stale);
        var staleId = stale.Id;
        await _sut.RemoveSlotsAsync([Fx.Mon(9), Fx.Mon(9, 15)]);
        Assert.Equal(0, await ActivityCountAsync());

        await store.WriteAsync(Code(Fx.Mon(9), Fx.Mon(9, 45), "contoso/api", "seg-dup")); // new sync, same ExternalId
        stale.EndUtc = Fx.Mon(10);
        await store.WriteAsync(stale); // the old instance checkpoints again
        await store.WriteAsync(stale);

        Assert.NotEqual(staleId, stale.Id);
        await using var db = _host.Db();
        var row = await db.Activities.SingleAsync();
        Assert.Equal((stale.Id, Fx.Mon(10)), (row.Id, row.EndUtc));
    }

    [Fact]
    public async Task A_failure_while_erasing_activities_leaves_no_tombstone_and_keeps_the_assignment()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(9, 15), "contoso/api"));
        await _sut.AssignSlotStartsAsync([Fx.Mon(9)], _support.Id, "Revue");
        await using (var db = _host.Db())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER block_activity_delete BEFORE DELETE ON Activities BEGIN SELECT RAISE(ABORT, 'boom'); END;");

        await Assert.ThrowsAsync<DbUpdateException>(() => _sut.RemoveSlotsAsync([Fx.Mon(9)]));

        await using var check = _host.Db();
        Assert.Equal(0, await check.RemovedSlots.CountAsync());
        Assert.Equal(1, await check.Activities.CountAsync());
        var kept = await check.SlotOverrides.SingleAsync();
        Assert.Equal((Fx.Mon(9), _support.Id, "Revue"), (kept.SlotStartUtc, kept.ForecastTaskId, kept.Notes));
        Assert.Equal(SlotKind.Override, (await SlotAsync(Fx.Monday, Fx.Mon(9))).Kind);
    }

    [Fact]
    public async Task Deleted_activities_no_longer_pollute_the_rule_learned_from_a_day_correction()
    {
        await _host.AddActivitiesAsync(
            Fx.Act(ActivitySource.Focus, Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 11), a => { a.Folder = @"C:\clients\fabrikam\plans"; a.ProcessName = "WINWORD"; }),
            Fx.Act(ActivitySource.Focus, Fx.Local(Tuesday, 11), Fx.Local(Tuesday, 12), a => { a.Folder = @"C:\clients\fabrikam\specs"; a.ProcessName = "WINWORD"; }),
            Fx.Act(ActivitySource.Focus, Fx.Local(Tuesday, 12), Fx.Local(Tuesday, 14), a => { a.Folder = @"D:\perso\vacances"; a.ProcessName = "chrome"; }));
        var target = new CorrectionTarget(new SlotRange(Fx.Local(Tuesday, 0), Fx.Local(Tuesday.AddDays(1), 0)));

        var noisy = await _sut.ProposeRuleFromCorrectionAsync(target, _support.Id);
        var noise = new SlotRange(Fx.Local(Tuesday, 12), Fx.Local(Tuesday, 14)).SlotStarts().ToList();
        await _sut.RemoveSlotsAsync(noise);
        var clean = await _sut.ProposeRuleFromCorrectionAsync(target, _support.Id);

        Assert.NotEqual((noisy?.Field, noisy?.Pattern), (clean?.Field, clean?.Pattern));
        Assert.Equal((RuleField.Folder, @"C:\clients\fabrikam"), (clean!.Field, clean.Pattern));
    }
}
