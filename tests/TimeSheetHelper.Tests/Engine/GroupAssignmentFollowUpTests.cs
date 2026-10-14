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

/// <summary>Follow-ups of <see cref="GroupAssignmentService"/>: group growing while pending, week submitted while pending, Confirmer retried after a partial failure.</summary>
public sealed class GroupAssignmentFollowUpTests : IAsyncLifetime
{
    private const string Repo = "repo:fabrikam/web";
    private const string AppGroup = "app:code:notes";

    private static readonly DateOnly Tuesday = Fx.Monday.AddDays(1);

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

    private async Task SubmitWeekAsync()
    {
        await using var db = _host.Db();
        db.TimesheetDrafts.Add(new TimesheetDraft { WeekStart = Fx.Monday, SubmittedUtc = Fx.Clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync();
    }

    /// <summary>Monday 9-10 assigned (4 slots), then Tuesday 9-10 of the same repo arrives (4 more unclassified slots).</summary>
    private async Task<PendingGroupAssignment> AssignThenGrowAsync(int taskId)
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = (await _sut.AssignAsync(Fx.Monday, Repo, taskId))!;
        await _host.AddActivitiesAsync(Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "fabrikam/web"));
        return pending;
    }

    // 1. Groupe qui grossit pendant l'attente

    [Fact]
    public async Task Slots_that_arrive_while_pending_stay_unclassified_until_the_group_is_extended()
    {
        var pending = await AssignThenGrowAsync(_support.Id);

        var week = await _weeks.GetWeekAsync(Fx.Monday);

        var group = Assert.Single(week.UnclassifiedGroups);
        Assert.Equal((Repo, 60), (group.Key, group.Minutes));
        Assert.Equal(4, Assert.Single(_sut.GetPending(Fx.Monday)).Slots.Count);
        Assert.Equal(pending.Id, _sut.GetPending(Fx.Monday)[0].Id);
        Assert.Equal(4, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Assigning_again_extends_the_pending_group_to_the_new_slots_with_the_same_id()
    {
        var pending = await AssignThenGrowAsync(_support.Id);

        var extended = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        Assert.Equal(pending.Id, extended!.Id);
        Assert.Equal(8, extended.Slots.Count);
        Assert.Equal(2m, extended.Hours);
        Assert.Equal(extended.Id, Assert.Single(_sut.GetPending(Fx.Monday)).Id);
        Assert.Equal(8, Assert.Single(_sut.GetPending(Fx.Monday)).Slots.Count);
        var overrides = await OverridesAsync();
        Assert.Equal(8, overrides.Count);
        Assert.All(overrides, o => Assert.Equal(_support.Id, o.ForecastTaskId));
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Equal((1m, 1m), (week.Days[0].TotalHours, week.Days[1].TotalHours));
    }

    [Fact]
    public async Task Assigning_again_without_new_slots_changes_nothing()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        var again = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        Assert.Equal(pending, again);
        Assert.Equal(4, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Extending_with_another_task_switches_the_whole_pending_group()
    {
        await AssignThenGrowAsync(_support.Id);

        var extended = await _sut.AssignAsync(Fx.Monday, Repo, _dev.Id);

        Assert.Equal((_dev.Id, 8), (extended!.TaskId, extended.Slots.Count));
        Assert.All(await OverridesAsync(), o => Assert.Equal(_dev.Id, o.ForecastTaskId));
        Assert.Equal(8, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Cancelling_an_extended_assignment_restores_the_new_slots_too()
    {
        await AssignThenGrowAsync(_support.Id);
        var extended = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        var result = await _sut.CancelAsync(extended!.Id);

        Assert.Equal((8, 0), (result!.SlotsRestored, result.SlotsSkipped));
        Assert.Empty(await OverridesAsync());
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(120, Assert.Single(week.UnclassifiedGroups).Minutes);
    }

    [Fact]
    public async Task Cancelling_an_extension_keeps_the_original_snapshot_of_a_slot_assigned_twice()
    {
        // A slot already in the snapshot that falls back to unclassified (its override was cleared) must not get a second, wrong snapshot entry.
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        await using (var db = _host.Db())
        {
            db.SlotOverrides.RemoveRange(db.SlotOverrides.Where(o => o.SlotStartUtc == Fx.Mon(9)));
            await db.SaveChangesAsync();
        }

        var extended = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        Assert.Equal(pending!.Id, extended!.Id);
        Assert.Equal(4, extended.Slots.Count);
        Assert.Equal(4, (await OverridesAsync()).Count);
        var result = await _sut.CancelAsync(extended.Id);
        Assert.Equal((4, 0), (result!.SlotsRestored, result.SlotsSkipped));
        Assert.Empty(await OverridesAsync());
    }

    [Fact]
    public async Task Confirming_an_extended_assignment_creates_one_rule_covering_every_slot()
    {
        await AssignThenGrowAsync(_support.Id);
        var extended = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        var result = await _sut.ConfirmAsync(extended!.Id);

        Assert.Equal((RuleField.Repo, "fabrikam/web"), (result!.Rule!.Field, result.Rule.Pattern));
        Assert.Equal(0, result.SlotsKeptManual);
        Assert.Equal(1, await RuleCountAsync());
        Assert.Empty(await OverridesAsync());
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Equal((1m, 1m), (week.Days[0].TotalHours, week.Days[1].TotalHours));
    }

    // 2. Semaine soumise pendant l'attente

    [Fact]
    public async Task Confirming_on_a_submitted_week_is_refused_and_leaves_overrides_and_rules_alone()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        await SubmitWeekAsync();

        var result = await _sut.ConfirmAsync(pending!.Id);

        Assert.Null(result!.Rule);
        Assert.Equal(0, result.SlotsKeptManual);
        Assert.True(result.WeekSubmitted);
        Assert.Equal(0, await RuleCountAsync());
        var overrides = await OverridesAsync();
        Assert.Equal(4, overrides.Count);
        Assert.All(overrides, o => Assert.Equal(_support.Id, o.ForecastTaskId));
        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Null(await _sut.ConfirmAsync(pending.Id)); // purged: now simply unknown
    }

    [Fact]
    public async Task Cancelling_on_a_submitted_week_is_refused_and_keeps_the_frozen_assignment()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        await SubmitWeekAsync();

        var result = await _sut.CancelAsync(pending!.Id);

        Assert.Equal((0, 0, true), (result!.SlotsRestored, result.SlotsSkipped, result.WeekSubmitted));
        Assert.Equal(4, (await OverridesAsync()).Count);
        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Null(await _sut.CancelAsync(pending.Id));
    }

    [Fact]
    public async Task Keeping_once_on_a_submitted_week_reports_the_submission_and_purges_the_pending_state()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        await SubmitWeekAsync();

        var result = await _sut.TryKeepOnceAsync(pending!.Id);

        Assert.Equal((false, true), (result.Kept, result.WeekSubmitted));
        Assert.Empty(_sut.GetPending(Fx.Monday));
        var again = await _sut.TryKeepOnceAsync(pending.Id);
        Assert.Equal((false, false), (again.Kept, again.WeekSubmitted));
        Assert.Equal(4, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Keeping_once_on_an_open_week_reports_a_normal_success()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        var result = await _sut.TryKeepOnceAsync(pending!.Id);

        Assert.Equal((true, false), (result.Kept, result.WeekSubmitted));
    }

    [Fact]
    public async Task Assigning_on_a_submitted_week_purges_its_pending_state_and_adds_nothing()
    {
        await AssignThenGrowAsync(_support.Id);
        await SubmitWeekAsync();

        Assert.Null(await _sut.AssignAsync(Fx.Monday, Repo, _support.Id));

        Assert.Empty(_sut.GetPending(Fx.Monday));
        Assert.Equal(4, (await OverridesAsync()).Count);
    }

    [Fact]
    public async Task Listing_pending_assignments_drops_those_of_a_week_submitted_since()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await _sut.AssignAsync(Fx.Monday, Repo, _support.Id);

        Assert.Equal(pending!.Id, Assert.Single(await _sut.GetPendingAsync(Fx.Monday)).Id);
        Assert.Empty(await _sut.GetPendingAsync(Fx.Monday.AddDays(7)));

        await SubmitWeekAsync();

        Assert.Empty(await _sut.GetPendingAsync(Fx.Monday.AddDays(2)));
        Assert.Empty(_sut.GetPending(Fx.Monday));
    }

    // 4. Confirmer non atomique : nouvel essai idempotent

    /// <summary>A service whose Restore step dies once the rule is stored (<see cref="FailingFactory.FailWhen"/> = rule exists).</summary>
    private (GroupAssignmentService Sut, FailingFactory Factory) FailingService()
    {
        var factory = new FailingFactory(_host.Get<IDbContextFactory<TimeSheetDbContext>>());
        return (new GroupAssignmentService(factory, _host.Get<WeekProposalBuilder>(), _weeks, _host.Get<SlotAllocator>()), factory);
    }

    private void FailOnceTheRuleIsStored(FailingFactory factory) =>
        factory.FailWhen = () => RuleCountAsync().GetAwaiter().GetResult() > 0;

    [Fact]
    public async Task Confirming_again_after_a_failure_right_after_the_rule_was_stored_finishes_the_job_without_a_second_rule()
    {
        var (sut, factory) = FailingService();
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        FailOnceTheRuleIsStored(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ConfirmAsync(pending!.Id));

        Assert.Equal(1, await RuleCountAsync());
        Assert.Equal(4, (await OverridesAsync()).Count);   // nothing lost: the manual assignment is still there
        var stillPending = Assert.Single(sut.GetPending(Fx.Monday));
        Assert.Equal(pending!.Id, stillPending.Id);
        Assert.NotNull(stillPending.StoredRuleId);

        factory.FailWhen = null;
        var result = await sut.ConfirmAsync(pending.Id);

        Assert.NotNull(result?.Rule);
        Assert.Equal(stillPending.StoredRuleId, result.Rule.Id);
        Assert.Equal(1, await RuleCountAsync());
        Assert.Empty(await OverridesAsync());
        Assert.Empty(sut.GetPending(Fx.Monday));
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(1m, week.Days[0].TotalHours);
        Assert.Empty(week.UnclassifiedGroups);
    }

    [Fact]
    public async Task Confirming_again_after_the_overrides_were_restored_reapplies_the_slots_the_rule_does_not_cover()
    {
        // Same app/title group; the rule learned from it is "Project = Alpha" (3 of 4 slots), the last slot has no project.
        // Once stored, that rule takes the Alpha activities out of the group: deriving it again would find nothing stable.
        var (sut, factory) = FailingService();
        await _host.AddActivitiesAsync(Notes(Fx.Mon(9), Fx.Mon(9, 45), "Alpha"), Notes(Fx.Mon(9, 45), Fx.Mon(10), null));
        var pending = await sut.AssignAsync(Fx.Monday, AppGroup, _support.Id);
        FailOnceTheRuleIsStored(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ConfirmAsync(pending!.Id));
        factory.FailWhen = null;
        // What Restore would have done just before dying later in the sequence.
        await using (var db = _host.Db())
        {
            db.SlotOverrides.RemoveRange(db.SlotOverrides);
            await db.SaveChangesAsync();
        }

        var partial = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(15, Assert.Single(partial.UnclassifiedGroups).Minutes); // the slot the rule misses fell back to unclassified

        var result = await sut.ConfirmAsync(pending!.Id);

        Assert.NotNull(result?.Rule);
        Assert.Equal(1, result.SlotsKeptManual);
        Assert.Equal(1, await RuleCountAsync());
        var kept = Assert.Single(await OverridesAsync());
        Assert.Equal((Fx.Mon(9, 45), _support.Id), (kept.SlotStartUtc, kept.ForecastTaskId));
        var week = await _weeks.GetWeekAsync(Fx.Monday);
        Assert.Equal(1m, week.Days[0].TotalHours);
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Empty(sut.GetPending(Fx.Monday));
    }

    [Fact]
    public async Task Switching_the_task_after_a_failed_confirm_drops_the_remembered_rule_and_confirms_the_new_task()
    {
        var (sut, factory) = FailingService();
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        FailOnceTheRuleIsStored(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ConfirmAsync(pending!.Id));
        factory.FailWhen = null;

        var switched = await sut.AssignAsync(Fx.Monday, Repo, _dev.Id);

        Assert.Equal((_dev.Id, (int?)null), (switched!.TaskId, switched.StoredRuleId));
        var result = await sut.ConfirmAsync(switched.Id);
        Assert.Equal(_dev.Id, result!.Rule!.ForecastTaskId);
        Assert.Equal(1, await RuleCountAsync());
    }

    [Fact]
    public async Task A_remembered_rule_that_was_deleted_meanwhile_is_derived_again()
    {
        var (sut, factory) = FailingService();
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "fabrikam/web"));
        var pending = await sut.AssignAsync(Fx.Monday, Repo, _support.Id);
        FailOnceTheRuleIsStored(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ConfirmAsync(pending!.Id));
        factory.FailWhen = null;
        await using (var db = _host.Db())
        {
            db.Rules.RemoveRange(db.Rules);
            await db.SaveChangesAsync();
        }

        var result = await sut.ConfirmAsync(pending!.Id);

        Assert.NotNull(result?.Rule);
        Assert.Equal(1, await RuleCountAsync());
        Assert.Empty(await OverridesAsync());
    }

    private sealed class FailingFactory(IDbContextFactory<TimeSheetDbContext> inner) : IDbContextFactory<TimeSheetDbContext>
    {
        public Func<bool>? FailWhen { get; set; }

        public TimeSheetDbContext CreateDbContext() =>
            FailWhen?.Invoke() == true ? throw new InvalidOperationException("Simulated failure") : inner.CreateDbContext();

        public Task<TimeSheetDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
