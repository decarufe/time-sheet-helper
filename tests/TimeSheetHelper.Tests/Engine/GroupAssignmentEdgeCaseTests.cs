using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>Edge cases of <see cref="GroupAssignmentService"/>: no stable rule, slots outside the rule, changes made since, concurrency, restart.</summary>
public sealed class GroupAssignmentEdgeCaseTests : IAsyncLifetime
{
    private const string Repo = "repo:fabrikam/web";
    private const string NoRuleGroup = "source:vscode";
    private const string AppGroup = "app:code:notes";

    private EngineTestHost _host = null!;
    private WeekService _weeks = null!;
    private GroupAssignmentService _sut = null!;
    private ForecastTask _dev = null!;
    private ForecastTask _support = null!;

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost(new() { ["Engine:TargetHoursPerDay"] = "7.5" });
        _weeks = _host.Get<WeekService>();
        _sut = _host.Get<GroupAssignmentService>();
        _dev = await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc.");
        _support = await _host.AddTaskAsync("Fabrikam", "Support");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static ActivityEvent Code(DateTime start, DateTime end, string repo) =>
        Fx.Act(ActivitySource.VsCode, start, end, a => { a.Repo = repo; a.Title = "Program.cs - " + repo; a.ProcessName = "Code"; });

    private static ActivityEvent Notes(DateTime start, DateTime end, string? project) =>
        Fx.Act(ActivitySource.VsCode, start, end, a => { a.ProcessName = "Code"; a.Title = "notes - ide"; a.Project = project; });

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

    private async Task<PendingGroupAssignment> AssignRepoGroupAsync(int taskId)
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web")); // 4 slots
        return (await _sut.AssignAsync(Fx.Monday, Repo, taskId))!;
    }

    // (a) Confirmer quand aucune règle stable n'est déductible

    private async Task<PendingGroupAssignment> AssignGroupWithoutStableRuleAsync()
    {
        await _host.AddActivitiesAsync(Fx.Act(ActivitySource.VsCode, Fx.Mon(9), Fx.Mon(10)));
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(NoRuleGroup, Assert.Single(week.UnclassifiedGroups).Key); // precondition: a "source:" group has no stable rule
        return (await _sut.AssignAsync(Fx.Monday, NoRuleGroup, _support.Id))!;
    }

    [Fact]
    public async Task Confirming_without_a_stable_rule_creates_nothing_and_keeps_the_assignment_pending()
    {
        var pending = await AssignGroupWithoutStableRuleAsync();
        var overridesBefore = await OverridesAsync();

        var result = await _sut.ConfirmAsync(pending.Id);

        Assert.NotNull(result);
        Assert.Null(result.Rule);
        Assert.Equal(0, result.SlotsKeptManual);
        Assert.Equal(0, await RuleCountAsync());
        Assert.Equal(pending.Id, Assert.Single(_sut.GetPending(Fx.Monday)).Id);
        Assert.Equal(overridesBefore.Select(o => (o.SlotStartUtc, o.ForecastTaskId)), (await OverridesAsync()).Select(o => (o.SlotStartUtc, o.ForecastTaskId)));
        Assert.Equal(4, overridesBefore.Count);
    }

    [Fact]
    public async Task Without_a_stable_rule_the_pending_assignment_can_still_be_cancelled()
    {
        var pending = await AssignGroupWithoutStableRuleAsync();
        await _sut.ConfirmAsync(pending.Id);

        var result = await _sut.CancelAsync(pending.Id);

        Assert.Equal((4, 0), (result!.SlotsRestored, result.SlotsSkipped));
        Assert.Empty(await OverridesAsync());
        Assert.Equal(0, await RuleCountAsync());
    }

    [Fact]
    public async Task Without_a_stable_rule_the_pending_assignment_can_still_be_kept_once()
    {
        var pending = await AssignGroupWithoutStableRuleAsync();
        await _sut.ConfirmAsync(pending.Id);

        Assert.True(await _sut.KeepOnceAsync(pending.Id));

        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Equal(4, (await OverridesAsync()).Count);
        Assert.Equal(0, await RuleCountAsync());
    }

    // (b) Créneaux que la règle ne couvrirait pas

    [Fact]
    public async Task Confirming_keeps_the_assignment_on_slots_the_new_rule_does_not_cover()
    {
        // Same app/title group; the rule learned from it is "Project = Alpha" (3 of 4 slots), the last slot has no project.
        await _host.AddActivitiesAsync(Notes(Fx.Mon(9), Fx.Mon(9, 45), "Alpha"), Notes(Fx.Mon(9, 45), Fx.Mon(10), null));
        var pending = await _sut.AssignAsync(Fx.Monday, AppGroup, _support.Id);
        Assert.Equal(4, pending!.Slots.Count);

        var result = await _sut.ConfirmAsync(pending.Id);

        Assert.Equal((RuleField.Project, "Alpha", _support.Id), (result!.Rule!.Field, result.Rule.Pattern, result.Rule.ForecastTaskId));
        Assert.Equal(1, result.SlotsKeptManual);
        var kept = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9, 45), _support.Id), (kept.SlotStartUtc, kept.ForecastTaskId));
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(1m, week.Days[0].TotalHours);       // nothing fell back to unclassified
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Empty(_sut.GetPending(Fx.Monday));
    }

    // (c) Un créneau ayant déjà un override avant l'assignation : un tel créneau est classé (Override) ou non travail, donc
    // jamais pris par l'assignation ; on vérifie ce comportement observable plutôt que la branche défensive HadOverride.

    [Fact]
    public async Task A_slot_that_already_had_an_override_is_neither_taken_nor_changed_by_assign_confirm_or_cancel()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        await _weeks.AssignSlotStartsAsync([Fx.Mon(9, 15)], _dev.Id, "Avant");
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        Assert.DoesNotContain(pending!.Slots, s => s.SlotStartUtc == Fx.Mon(9, 15));
        Assert.All(pending.Slots, s => Assert.False(s.HadOverride));

        await _sut.ConfirmAsync(pending.Id);

        var remaining = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9, 15), _dev.Id, "Avant"), (remaining.SlotStartUtc, remaining.ForecastTaskId, remaining.Notes));
    }

    // Changes made since

    [Fact]
    public async Task Confirming_does_not_drop_what_the_user_changed_since()
    {
        var pending = await AssignRepoGroupAsync(_support.Id);
        await _weeks.AssignSlotStartsAsync([Fx.Mon(9)], _dev.Id, "Revue");

        var result = await _sut.ConfirmAsync(pending.Id);

        Assert.NotNull(result?.Rule);
        var remaining = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9), _dev.Id, "Revue"), (remaining.SlotStartUtc, remaining.ForecastTaskId, remaining.Notes));
    }

    [Fact]
    public async Task Confirming_does_not_bring_back_a_slot_deleted_since()
    {
        var pending = await AssignRepoGroupAsync(_support.Id);
        await _weeks.RemoveSlotsAsync([Fx.Mon(9, 15)]);

        var result = await _sut.ConfirmAsync(pending.Id);

        Assert.Equal(0, result!.SlotsKeptManual);
        Assert.Empty(await OverridesAsync());
        await using var db = _host.Db();
        Assert.Equal(Fx.Mon(9, 15), (await db.RemovedSlots.SingleAsync()).SlotStartUtc);
        var slots = (await _weeks.GetDayTimelineAsync(Fx.Monday)).Slots.ToDictionary(s => s.SlotStartUtc);
        Assert.Equal(SlotKind.Removed, slots[Fx.Mon(9, 15)].Kind);
    }

    [Fact]
    public async Task Cancelling_keeps_a_comment_the_user_added_to_an_assigned_slot()
    {
        var pending = await AssignRepoGroupAsync(_support.Id);
        await _weeks.AssignSlotStartsAsync([Fx.Mon(9)], _support.Id, "Ticket 42"); // same task, now with a comment

        var result = await _sut.CancelAsync(pending.Id);

        Assert.Equal((3, 1), (result!.SlotsRestored, result.SlotsSkipped));
        var remaining = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9), _support.Id, "Ticket 42"), (remaining.SlotStartUtc, remaining.ForecastTaskId, remaining.Notes));
    }

    // A settled assignment stays settled

    [Theory]
    [InlineData("keep", "cancel")]
    [InlineData("keep", "confirm")]
    [InlineData("cancel", "keep")]
    [InlineData("cancel", "confirm")]
    [InlineData("confirm", "keep")]
    [InlineData("confirm", "cancel")]
    public async Task A_settled_assignment_cannot_be_settled_again_another_way(string first, string second)
    {
        var pending = await AssignRepoGroupAsync(_support.Id);
        await SettleAsync(first, pending.Id);
        var rules = await RuleCountAsync();
        var overrides = (await OverridesAsync()).Select(o => (o.SlotStartUtc, o.ForecastTaskId)).ToList();

        Assert.False(await SettleAsync(second, pending.Id));

        Assert.Equal(rules, await RuleCountAsync());
        Assert.Equal(overrides, (await OverridesAsync()).Select(o => (o.SlotStartUtc, o.ForecastTaskId)));
        Assert.Empty(_sut.GetPending(Fx.Monday));
    }

    private async Task<bool> SettleAsync(string how, Guid id) => how switch
    {
        "keep" => await _sut.KeepOnceAsync(id),
        "cancel" => await _sut.CancelAsync(id) is not null,
        "confirm" => await _sut.ConfirmAsync(id) is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(how)),
    };

    // Other groups, concurrency, restart

    [Fact]
    public async Task Assigning_one_group_leaves_the_slots_of_other_groups_alone()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"), Code(Fx.Mon(10), Fx.Mon(11), "litware/app"));

        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        Assert.All(pending!.Slots, s => Assert.InRange(s.SlotStartUtc, Fx.Mon(9), Fx.Mon(9, 45)));
        Assert.All(await OverridesAsync(), o => Assert.InRange(o.SlotStartUtc, Fx.Mon(9), Fx.Mon(9, 45)));
        Assert.Equal("repo:litware/app", Assert.Single((await _weeks.GetWeekAsync(Fx.Monday)).UnclassifiedGroups).Key);

        await _sut.CancelAsync(pending.Id);

        Assert.Empty(await OverridesAsync());
        Assert.Equal(["repo:fabrikam/web", "repo:litware/app"], (await _weeks.GetWeekAsync(Fx.Monday)).UnclassifiedGroups.Select(g => g.Key).Order());
    }

    [Fact]
    public async Task Concurrent_confirms_create_one_rule_and_settle_once()
    {
        var pending = await AssignRepoGroupAsync(_support.Id);
        var rulesBefore = await RuleCountAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _sut.ConfirmAsync(pending.Id))));

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(rulesBefore + 1, await RuleCountAsync());
        Assert.Empty(await OverridesAsync());
        Assert.Empty(_sut.GetPending(Fx.Monday));
    }

    [Fact]
    public async Task Concurrent_confirm_and_cancel_settle_exactly_once_and_stay_consistent()
    {
        var pending = await AssignRepoGroupAsync(_support.Id);

        var confirm = Task.Run(() => _sut.ConfirmAsync(pending.Id));
        var cancel = Task.Run(() => _sut.CancelAsync(pending.Id));
        await Task.WhenAll(confirm, cancel);

        var confirmed = await confirm is not null;
        var cancelled = await cancel is not null;
        Assert.True(confirmed ^ cancelled);
        Assert.Equal(confirmed ? 1 : 0, await RuleCountAsync()); // a rule exists only if Confirmer won
        Assert.Empty(await OverridesAsync());
        Assert.Empty(_sut.GetPending(Fx.Monday));
    }

    [Fact]
    public async Task Concurrent_assigns_of_a_group_leave_one_pending_assignment_with_matching_overrides()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));

        var results = await Task.WhenAll(
            Task.Run(() => _sut.AssignAsync(Fx.Monday, Repo, _support.Id)),
            Task.Run(() => _sut.AssignAsync(Fx.Monday, Repo, _dev.Id)),
            Task.Run(() => _sut.AssignAsync(Fx.Monday, Repo, _support.Id)),
            Task.Run(() => _sut.AssignAsync(Fx.Monday, Repo, _dev.Id)));

        Assert.All(results, Assert.NotNull);
        Assert.Single(results.Select(r => r!.Id).Distinct());
        var pending = Assert.Single(_sut.GetPending(Fx.Monday));
        var overrides = await OverridesAsync();
        Assert.Equal(4, overrides.Count);
        Assert.All(overrides, o => Assert.Equal(pending.TaskId, o.ForecastTaskId));

        await _sut.CancelAsync(pending.Id);

        Assert.Empty(await OverridesAsync());
    }

    [Fact]
    public async Task After_a_restart_the_assignment_persists_without_a_pending_state_or_a_rule()
    {
        var pending = await AssignRepoGroupAsync(_support.Id);
        var restarted = new GroupAssignmentService(
            _host.Get<IDbContextFactory<TimeSheetDbContext>>(), _host.Get<WeekProposalBuilder>(), _weeks, _host.Get<SlotAllocator>());

        Assert.Empty(restarted.GetPending(Fx.Monday));
        Assert.Null(await restarted.CancelAsync(pending.Id));
        Assert.Null(await restarted.ConfirmAsync(pending.Id));
        Assert.False(await restarted.KeepOnceAsync(pending.Id));
        Assert.Null(await restarted.AssignAsync(Fx.Monday, Repo, _dev.Id)); // already classified: nothing left to assign

        Assert.Equal(0, await RuleCountAsync());
        Assert.All(await OverridesAsync(), o => Assert.Equal(_support.Id, o.ForecastTaskId));
        Assert.Equal(4, (await OverridesAsync()).Count);
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(1m, week.Days[0].TotalHours);
        Assert.Empty(week.UnclassifiedGroups);
    }
}
