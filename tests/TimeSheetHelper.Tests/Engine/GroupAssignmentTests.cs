using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>Assigning a task to an unclassified group: applied at once, then Confirmer (rule) / Cette fois seulement / Annuler.</summary>
public sealed class GroupAssignmentTests : IAsyncLifetime
{
    private const string Group = "repo:fabrikam/web";

    private EngineTestHost _host = null!;
    private WeekService _weeks = null!;
    private GroupAssignmentService _sut = null!;
    private ForecastTask _dev = null!;
    private ForecastTask _support = null!;

    private static readonly DateOnly Tuesday = Fx.Monday.AddDays(1);

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost(new() { ["Engine:TargetHoursPerDay"] = "7.5" });
        _weeks = _host.Get<WeekService>();
        _sut = _host.Get<GroupAssignmentService>();
        _dev = await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc.");
        _support = await _host.AddTaskAsync("Fabrikam", "Support");
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "contoso/api", _dev.Id);
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web")); // 4 slots, no rule
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static ActivityEvent Code(DateTime start, DateTime end, string repo) =>
        Fx.Act(ActivitySource.VsCode, start, end, a => { a.Repo = repo; a.Title = "Program.cs - " + repo; a.ProcessName = "Code"; });

    private async Task<int> RuleCountAsync()
    {
        await using var db = _host.Db();
        return await db.Rules.CountAsync();
    }

    private async Task<List<SlotOverride>> OverridesAsync()
    {
        await using var db = _host.Db();
        return await db.SlotOverrides.AsNoTracking().OrderBy(o => o.SlotStartUtc).ToListAsync();
    }

    private async Task<(decimal Classified, bool GroupStillListed)> WeekStateAsync()
    {
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        return (week.Days[0].TotalHours, week.UnclassifiedGroups.Any(g => g.Key == Group));
    }

    [Fact]
    public async Task Assigning_applies_the_task_to_the_group_slots_without_creating_a_rule()
    {
        var rulesBefore = await RuleCountAsync();

        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.NotNull(pending);
        Assert.Equal((Group, _support.Id, 4), (pending.GroupKey, pending.TaskId, pending.Slots.Count));
        Assert.Equal(1m, pending.Hours);
        Assert.Equal(rulesBefore, await RuleCountAsync());
        Assert.All(await OverridesAsync(), o => Assert.Equal(_support.Id, o.ForecastTaskId));
        Assert.Equal(4, (await OverridesAsync()).Count);
        Assert.Equal((1m, false), await WeekStateAsync()); // visible in the totals; no longer listed as unclassified
        Assert.Equal(pending.Id, Assert.Single(_sut.GetPending(Fx.Monday)).Id);
    }

    [Fact]
    public async Task Pending_assignments_belong_to_their_week_only()
    {
        await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.Single(_sut.GetPending(Fx.Monday.AddDays(3)));
        Assert.Empty(_sut.GetPending(Fx.Monday.AddDays(7)));
    }

    [Fact]
    public async Task Confirming_creates_exactly_one_rule_and_drops_the_pending_state()
    {
        var rulesBefore = await RuleCountAsync();
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        var result = await _sut.ConfirmAsync(pending!.Id);

        Assert.NotNull(result?.Rule);
        Assert.Equal((RuleField.Repo, "fabrikam/web", _support.Id, "correction"), (result.Rule.Field, result.Rule.Pattern, result.Rule.ForecastTaskId, result.Rule.Origin));
        Assert.Equal(0, result.SlotsKeptManual);
        Assert.Equal(rulesBefore + 1, await RuleCountAsync());
        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Empty(await OverridesAsync()); // the rule decides now
        Assert.Equal((1m, false), await WeekStateAsync());
    }

    [Fact]
    public async Task Confirming_twice_does_not_create_a_second_rule()
    {
        var rulesBefore = await RuleCountAsync();
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        await _sut.ConfirmAsync(pending!.Id);
        var second = await _sut.ConfirmAsync(pending.Id);

        Assert.Null(second);
        Assert.Equal(rulesBefore + 1, await RuleCountAsync());
    }

    [Fact]
    public async Task Keeping_once_keeps_the_assignment_without_a_rule_and_drops_the_pending_state()
    {
        var rulesBefore = await RuleCountAsync();
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.True(await _sut.KeepOnceAsync(pending!.Id));

        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Equal(rulesBefore, await RuleCountAsync());
        Assert.Equal(4, (await OverridesAsync()).Count);
        Assert.Equal((1m, false), await WeekStateAsync());
    }

    [Fact]
    public async Task Keeping_once_twice_is_a_no_op()
    {
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.True(await _sut.KeepOnceAsync(pending!.Id));
        Assert.False(await _sut.KeepOnceAsync(pending.Id));

        Assert.Equal(4, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Cancelling_restores_the_previous_state_and_creates_no_rule()
    {
        var rulesBefore = await RuleCountAsync();
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        var result = await _sut.CancelAsync(pending!.Id);

        Assert.Equal((4, 0), (result!.SlotsRestored, result.SlotsSkipped));
        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Empty(await OverridesAsync());
        Assert.Equal(rulesBefore, await RuleCountAsync());
        Assert.Equal((0m, true), await WeekStateAsync()); // back to unclassified, 1 h to assign
    }

    [Fact]
    public async Task Cancelling_twice_is_a_no_op()
    {
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        await _sut.CancelAsync(pending!.Id);

        Assert.Null(await _sut.CancelAsync(pending.Id));
        Assert.Empty(await OverridesAsync());
    }

    [Fact]
    public async Task Assigning_twice_to_the_same_task_reuses_the_pending_assignment()
    {
        var first = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);
        var second = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(_sut.GetPending(Fx.Monday));
        Assert.Equal(4, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Assigning_a_pending_group_to_another_task_switches_it_and_cancel_still_restores_everything()
    {
        var first = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        var switched = await _sut.AssignAsync(Fx.Monday, Group, _dev.Id);

        Assert.Equal((first!.Id, _dev.Id), (switched!.Id, switched.TaskId));
        Assert.All(await OverridesAsync(), o => Assert.Equal(_dev.Id, o.ForecastTaskId));

        var result = await _sut.CancelAsync(switched.Id);

        Assert.Equal(4, result!.SlotsRestored);
        Assert.Empty(await OverridesAsync());
    }

    [Fact]
    public async Task Cancelling_leaves_an_earlier_assignment_on_other_slots_of_the_same_activity_untouched()
    {
        await _weeks.AssignSlotStartsAsync([Fx.Mon(9)], _dev.Id, "Avant"); // earlier manual assignment on the first slot
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.Equal(3, pending!.Slots.Count);
        Assert.DoesNotContain(pending.Slots, s => s.SlotStartUtc == Fx.Mon(9));

        await _sut.CancelAsync(pending.Id);

        var remaining = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9), _dev.Id, "Avant"), (remaining.SlotStartUtc, remaining.ForecastTaskId, remaining.Notes));
    }

    [Fact]
    public async Task Cancelling_does_not_undo_what_the_user_changed_since()
    {
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);
        await _weeks.AssignSlotStartsAsync([Fx.Mon(9)], _dev.Id, "Revue");   // reassigned in the day view
        await _weeks.RemoveSlotsAsync([Fx.Mon(9, 15)]);                       // deleted in the day view

        var result = await _sut.CancelAsync(pending!.Id);

        Assert.Equal((2, 2), (result!.SlotsRestored, result.SlotsSkipped));
        var remaining = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9), _dev.Id, "Revue"), (remaining.SlotStartUtc, remaining.ForecastTaskId, remaining.Notes));
        await using var db = _host.Db();
        Assert.Equal(Fx.Mon(9, 15), (await db.RemovedSlots.SingleAsync()).SlotStartUtc);
    }

    [Fact]
    public async Task Deleted_and_not_work_slots_are_never_assigned_nor_restored()
    {
        await _weeks.AssignSlotStartsAsync([Fx.Mon(9)], null);   // Pas du travail
        await _weeks.RemoveSlotsAsync([Fx.Mon(9, 15)]);          // Supprimer

        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);

        Assert.Equal([Fx.Mon(9, 30), Fx.Mon(9, 45)], pending!.Slots.Select(s => s.SlotStartUtc));
        var slots = (await _weeks.GetDayTimelineAsync(Fx.Monday)).Slots.ToDictionary(s => s.SlotStartUtc);
        Assert.Equal(SlotKind.ExcludedByOverride, slots[Fx.Mon(9)].Kind);
        Assert.Equal(SlotKind.Removed, slots[Fx.Mon(9, 15)].Kind);

        await _sut.CancelAsync(pending.Id);

        var remaining = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9), (int?)null), (remaining.SlotStartUtc, remaining.ForecastTaskId));
        await using var db = _host.Db();
        Assert.Equal(Fx.Mon(9, 15), (await db.RemovedSlots.SingleAsync()).SlotStartUtc);
    }

    [Fact]
    public async Task A_group_that_comes_back_after_keeping_once_is_unclassified_again_without_touching_the_earlier_assignment()
    {
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);
        await _sut.KeepOnceAsync(pending!.Id);

        await _host.AddActivitiesAsync(Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "fabrikam/web"));
        var week = await _weeks.GetWeekAsync(Fx.Monday);

        var group = Assert.Single(week.UnclassifiedGroups);
        Assert.Equal((Group, 60), (group.Key, group.Minutes));
        Assert.Equal(1m, week.Days[0].TotalHours);   // Monday kept
        Assert.Equal(0m, week.Days[1].TotalHours);

        var next = await _sut.AssignAsync(Fx.Monday, Group, _dev.Id);
        Assert.NotEqual(pending.Id, next!.Id);
        Assert.All(next.Slots, s => Assert.Equal(Tuesday, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s.SlotStartUtc, FixedTimeProvider.Toronto))));
        Assert.Equal(4, next.Slots.Count);
    }

    [Fact]
    public async Task A_rule_created_elsewhere_while_pending_does_not_break_confirm_or_duplicate_the_rule()
    {
        var pending = await _sut.AssignAsync(Fx.Monday, Group, _support.Id);
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "fabrikam/web", _dev.Id, order: 5);
        var rulesBefore = await RuleCountAsync();

        Assert.Equal((1m, false), await WeekStateAsync()); // the manual assignment still wins until settled

        var result = await _sut.ConfirmAsync(pending!.Id);

        Assert.Equal(_support.Id, result!.Rule!.ForecastTaskId);
        Assert.Equal(rulesBefore, await RuleCountAsync()); // same field/operator/pattern: re-pointed, not duplicated
        Assert.Equal((1m, false), await WeekStateAsync());
    }

    [Fact]
    public async Task Nothing_is_assigned_for_an_unknown_group_an_unknown_task_or_a_submitted_week()
    {
        Assert.Null(await _sut.AssignAsync(Fx.Monday, "repo:nothing/here", _support.Id));
        Assert.Null(await _sut.AssignAsync(Fx.Monday, Group, 9999));

        await using (var db = _host.Db())
        {
            db.TimesheetDrafts.Add(new TimesheetDraft { WeekStart = Fx.Monday, SubmittedUtc = Fx.Clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }

        Assert.Null(await _sut.AssignAsync(Fx.Monday, Group, _support.Id));
        Assert.Empty(await OverridesAsync());
        Assert.Empty(_sut.GetPending(Fx.Monday));
    }
}
