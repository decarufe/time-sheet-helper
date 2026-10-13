using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Proposals;

namespace TimeSheetHelper.Tests.Engine;

public class WeekServiceTests : IAsyncLifetime
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

    private static ActivityEvent Code(DateTime start, DateTime end, string repo) =>
        Fx.Act(ActivitySource.VsCode, start, end, a => { a.Repo = repo; a.Title = "Program.cs - " + repo; a.ProcessName = "Code"; });

    [Fact]
    public async Task Week_totals_count_quarter_hours_per_task()
    {
        await _host.AddActivitiesAsync(
            Code(Fx.Mon(9), Fx.Mon(12, 7), "contoso/api"),      // 12 full slots + 7 min => 13 slots
            Code(Fx.Mon(13), Fx.Mon(13, 4), "contoso/api"),     // 4 min => nothing
            Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "fabrikam/web"));

        var week = await _sut.GetWeekAsync(Fx.Monday.AddDays(3)); // any day of the week gives its Monday

        Assert.Equal(Fx.Monday, week.WeekStart);
        Assert.Equal(7, week.Days.Count);

        var monday = week.Days[0];
        var hours = Assert.Single(monday.Tasks);
        Assert.Equal(_dev.Id, hours.ForecastTaskId);
        Assert.Equal(3.25m, hours.Hours);
        Assert.Equal(3.25m, monday.TotalHours);
        Assert.Equal(7.5m, monday.TargetHours);
        Assert.Equal(4.25m, monday.Gap);
        Assert.Equal(DayFlag.UnderTarget, monday.Flag);

        var tuesday = week.Days[1];
        Assert.Equal(0m, tuesday.TotalHours);
        Assert.Equal(1m, tuesday.UnclassifiedHours);

        Assert.Equal(0m, week.Days[5].TargetHours); // Saturday
        Assert.Equal(DayFlag.Ok, week.Days[5].Flag);
        Assert.Equal(3.25m, week.TotalHours);
        Assert.Equal(37.5m, week.TargetHours);
        Assert.All(week.Days.SelectMany(d => d.Tasks), t => Assert.Equal(0m, t.Hours % 0.25m));
    }

    [Fact]
    public async Task Unclassified_groups_carry_minutes_samples_and_suggestion()
    {
        _host.Classifier.Answer = g => new ClassificationResult(_support.Id, 0.75, "Dépôt Fabrikam", new SuggestedRule(RuleField.Repo, RuleOperator.Equals, "fabrikam/web"));
        await _host.AddActivitiesAsync(Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10, 30), "fabrikam/web"));

        var week = await _sut.GetWeekAsync(Fx.Monday);

        var group = Assert.Single(week.UnclassifiedGroups);
        Assert.Equal("repo:fabrikam/web", group.Key);
        Assert.Equal(GroupKind.Repo, group.Kind);
        Assert.Equal(90, group.Minutes);
        Assert.Equal(1.5m, group.Hours);
        Assert.Equal(["Program.cs - fabrikam/web"], group.Metadata.SampleTitles);
        Assert.Equal(TimeSpan.FromMinutes(90), group.Metadata.TotalDuration);
        Assert.Equal(_support.Id, group.Suggestion?.TaskId);

        // Second load uses the cache.
        await _sut.GetWeekAsync(Fx.Monday);
        Assert.Single(_host.Classifier.Calls);
    }

    [Fact]
    public async Task Accepting_a_suggestion_stores_a_claude_rule_and_classifies_the_group()
    {
        _host.Classifier.Answer = g => new ClassificationResult(_support.Id, 0.75, "Dépôt Fabrikam", new SuggestedRule(RuleField.Repo, RuleOperator.Equals, "fabrikam/web"));
        await _host.AddActivitiesAsync(Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "fabrikam/web"));
        await _sut.GetWeekAsync(Fx.Monday);

        var rule = await _sut.AcceptSuggestionAsync("repo:fabrikam/web", Fx.Monday);

        Assert.NotNull(rule);
        Assert.Equal("claude", rule.Origin);
        Assert.Equal(_support.Id, rule.ForecastTaskId);
        var week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Equal(1m, week.Days[1].Tasks.Single(t => t.ForecastTaskId == _support.Id).Hours);
    }

    [Fact]
    public async Task Suggested_rule_not_matching_the_group_falls_back_to_the_group_key()
    {
        _host.Classifier.Answer = g => new ClassificationResult(_support.Id, 0.6, "r", new SuggestedRule(RuleField.Title, RuleOperator.Contains, "nothing like this"));
        await _host.AddActivitiesAsync(Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "fabrikam/web"));
        await _sut.GetWeekAsync(Fx.Monday);

        var rule = await _sut.AcceptSuggestionAsync("repo:fabrikam/web", Fx.Monday);

        Assert.Equal((RuleField.Repo, "fabrikam/web"), (rule!.Field, rule.Pattern));
    }

    [Fact]
    public async Task Accepting_without_suggestion_does_nothing() =>
        Assert.Null(await _sut.AcceptSuggestionAsync("repo:unknown"));

    [Fact]
    public async Task Assigned_slots_override_the_engine()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(10), "contoso/api"));

        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(9, 5), Fx.Mon(9, 30)), _support.Id); // snaps to 9:00 and 9:15
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(9, 45), Fx.Mon(10)), null);           // not work
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(20), Fx.Mon(20, 15)), _support.Id);   // evening, no activity

        var timeline = await _sut.GetDayTimelineAsync(Fx.Monday);
        Assert.Equal(SlotKind.Override, timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9)).Kind);
        Assert.Equal(SlotKind.ExcludedByOverride, timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9, 45)).Kind);
        Assert.Equal(_dev.Id, timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9, 30)).TaskId);
        Assert.NotEmpty(timeline.Activities);

        var monday = (await _sut.GetWeekAsync(Fx.Monday)).Days[0];
        Assert.Equal(0.75m, monday.Tasks.Single(t => t.ForecastTaskId == _support.Id).Hours);
        Assert.Equal(0.25m, monday.Tasks.Single(t => t.ForecastTaskId == _dev.Id).Hours);

        // Re-assigning updates instead of duplicating; clearing gives the slots back to the engine.
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(9), Fx.Mon(9, 15)), _dev.Id);
        await using (var db = _host.Db())
            Assert.Equal(4, await db.SlotOverrides.CountAsync());

        await _sut.ClearSlotsAsync(new SlotRange(Fx.Mon(0), Fx.Mon(23)));
        monday = (await _sut.GetWeekAsync(Fx.Monday)).Days[0];
        Assert.Equal(1m, Assert.Single(monday.Tasks).Hours);
    }

    [Fact]
    public async Task Assignment_comment_reaches_the_week_and_the_draft()
    {
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(9), Fx.Mon(9, 30)), _support.Id, "  Revue de code  ");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(10), Fx.Mon(10, 15)), _support.Id, "revue de code");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(11), Fx.Mon(11, 15)), _support.Id, "Réunion d'équipe");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(12), Fx.Mon(12, 15)), _support.Id);

        var timeline = await _sut.GetDayTimelineAsync(Fx.Monday);
        Assert.Equal("Revue de code", timeline.Slots.Single(s => s.SlotStartUtc == Fx.Mon(9)).Notes);

        var hours = (await _sut.GetWeekAsync(Fx.Monday)).Days[0].Tasks.Single(t => t.ForecastTaskId == _support.Id);
        Assert.Equal(1.25m, hours.Hours);
        Assert.Equal("Revue de code ; Réunion d'équipe", hours.Notes);

        var result = await _sut.RebuildDraftAsync(Fx.Monday);
        Assert.Equal("Revue de code ; Réunion d'équipe", result.Draft.Entries.Single().Notes);

        // Re-assigning without a comment drops it, and "not work" never keeps one.
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(9), Fx.Mon(9, 30)), _support.Id);
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(11), Fx.Mon(11, 15)), null, "ignoré");
        await using var db = _host.Db();
        var overrides = await db.SlotOverrides.ToListAsync();
        Assert.Null(overrides.Single(o => o.SlotStartUtc == Fx.Mon(9)).Notes);
        Assert.Null(overrides.Single(o => o.SlotStartUtc == Fx.Mon(11)).Notes);
        Assert.Equal("revue de code", overrides.Single(o => o.SlotStartUtc == Fx.Mon(10)).Notes);
    }

    [Fact]
    public async Task Recent_comments_are_distinct_most_recent_first_and_specific_to_the_task()
    {
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(9), Fx.Mon(9, 15)), _support.Id, "Premier");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(10), Fx.Mon(10, 15)), _support.Id, "Deuxième");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(11), Fx.Mon(11, 30)), _support.Id, "deuxième");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(13), Fx.Mon(13, 15)), _support.Id, "Troisième");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(14), Fx.Mon(14, 15)), _dev.Id, "Autre tâche");
        await _sut.AssignSlotsAsync(new SlotRange(Fx.Mon(15), Fx.Mon(15, 15)), _support.Id);

        Assert.Equal(["Troisième", "deuxième", "Premier"], await _sut.GetRecentCommentsAsync(_support.Id));
        Assert.Equal(["Troisième", "deuxième"], await _sut.GetRecentCommentsAsync(_support.Id, max: 2));
        Assert.Equal(["Autre tâche"], await _sut.GetRecentCommentsAsync(_dev.Id));
        Assert.Empty(await _sut.GetRecentCommentsAsync(_dev.Id + 100));
    }

    [Fact]
    public async Task Non_contiguous_slots_get_the_same_task_and_comment_and_can_be_cleared_together()
    {
        await _sut.AssignSlotStartsAsync([Fx.Mon(9), Fx.Mon(9, 45), Fx.Mon(14), Fx.Mon(9)], _support.Id, " Support client ");

        await using (var db = _host.Db())
        {
            var overrides = await db.SlotOverrides.OrderBy(o => o.SlotStartUtc).ToListAsync();
            Assert.Equal([Fx.Mon(9), Fx.Mon(9, 45), Fx.Mon(14)], overrides.Select(o => o.SlotStartUtc));
            Assert.All(overrides, o => Assert.Equal((_support.Id, "Support client"), (o.ForecastTaskId, o.Notes)));
        }

        var monday = (await _sut.GetWeekAsync(Fx.Monday)).Days[0];
        Assert.Equal(0.75m, monday.Tasks.Single(t => t.ForecastTaskId == _support.Id).Hours);

        await _sut.AssignSlotStartsAsync([Fx.Mon(9, 45)], _dev.Id, "Dev");
        await _sut.ClearSlotStartsAsync([Fx.Mon(9), Fx.Mon(14)]);

        await using var db2 = _host.Db();
        var left = await db2.SlotOverrides.SingleAsync();
        Assert.Equal((Fx.Mon(9, 45), _dev.Id, "Dev"), (left.SlotStartUtc, left.ForecastTaskId, left.Notes));
    }

    [Fact]
    public async Task Correction_on_a_group_learns_a_rule()
    {
        await _host.AddActivitiesAsync(
            Fx.Act(ActivitySource.Focus, Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 11), a => { a.Folder = @"C:\clients\fabrikam\plans"; a.ProcessName = "WINWORD"; }),
            Fx.Act(ActivitySource.Focus, Fx.Local(Tuesday, 11), Fx.Local(Tuesday, 12), a => { a.Folder = @"C:\clients\fabrikam\specs"; a.ProcessName = "WINWORD"; }));

        var week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Equal(2, week.UnclassifiedGroups.Count);

        // Correcting a whole day: both folders share a meaningful prefix.
        var (from, to) = (Fx.Local(Tuesday, 0), Fx.Local(Tuesday.AddDays(1), 0));
        var rule = await _sut.CreateRuleFromCorrectionAsync(new CorrectionTarget(new SlotRange(from, to)), _support.Id);

        Assert.NotNull(rule);
        Assert.Equal((RuleField.Folder, RuleOperator.StartsWith, @"C:\clients\fabrikam", "correction"), (rule.Field, rule.Operator, rule.Pattern, rule.Origin));

        week = await _sut.GetWeekAsync(Fx.Monday);
        Assert.Empty(week.UnclassifiedGroups);
        Assert.Equal(3m, week.Days[1].TotalHours);
    }

    [Fact]
    public async Task Correction_restricted_to_a_group_uses_only_its_activities()
    {
        await _host.AddActivitiesAsync(
            Code(Fx.Local(Tuesday, 9), Fx.Local(Tuesday, 10), "fabrikam/web"),
            Fx.Act(ActivitySource.Focus, Fx.Local(Tuesday, 10), Fx.Local(Tuesday, 14), a => { a.ProcessName = "chrome"; a.Title = "News"; }));

        var (from, to) = (Fx.Local(Tuesday, 0), Fx.Local(Tuesday.AddDays(1), 0));
        var proposal = await _sut.ProposeRuleFromCorrectionAsync(new CorrectionTarget(new SlotRange(from, to), "repo:fabrikam/web"), _support.Id);

        Assert.Equal((RuleField.Repo, "fabrikam/web"), (proposal!.Field, proposal.Pattern));
        await using var db = _host.Db();
        Assert.Equal(1, await db.Rules.CountAsync()); // proposing stores nothing
    }

    [Fact]
    public async Task Rebuild_writes_the_draft_and_keeps_notes()
    {
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(11), "contoso/api"));

        var result = await _sut.RebuildDraftAsync(Fx.Monday);
        Assert.False(result.AlreadySubmitted);
        var entry = Assert.Single(result.Draft.Entries);
        Assert.Equal((Fx.Monday, _dev.Id, 2m), (entry.Date, entry.ForecastTaskId, entry.Hours));

        await using (var db = _host.Db())
        {
            var stored = await db.TimesheetEntries.SingleAsync();
            stored.Notes = "Revue de code";
            await db.SaveChangesAsync();
        }

        await _host.AddActivitiesAsync(Code(Fx.Mon(14), Fx.Mon(15), "contoso/api"));
        result = await _sut.RebuildDraftAsync(Fx.Monday);

        entry = Assert.Single(result.Draft.Entries);
        Assert.Equal(3m, entry.Hours);
        Assert.Equal("Revue de code", entry.Notes);
        await using (var db = _host.Db())
            Assert.Equal(1, await db.TimesheetDrafts.CountAsync());
    }

    [Fact]
    public async Task Submitted_draft_is_never_overwritten()
    {
        await using (var db = _host.Db())
        {
            db.TimesheetDrafts.Add(new TimesheetDraft
            {
                WeekStart = Fx.Monday,
                SubmittedUtc = Fx.Mon(17),
                Entries = [new TimesheetEntry { Date = Fx.Monday, ForecastTaskId = _support.Id, Hours = 7.5m }],
            });
            await db.SaveChangesAsync();
        }
        await _host.AddActivitiesAsync(Code(Fx.Mon(9), Fx.Mon(11), "contoso/api"));

        var result = await _sut.RebuildDraftAsync(Fx.Monday);

        Assert.True(result.AlreadySubmitted);
        await using (var db = _host.Db())
        {
            var entry = await db.TimesheetEntries.SingleAsync();
            Assert.Equal((_support.Id, 7.5m), (entry.ForecastTaskId, entry.Hours));
        }
        Assert.True((await _sut.GetWeekAsync(Fx.Monday)).IsSubmitted);
    }

    [Fact]
    public async Task Commit_just_after_midnight_is_evidence_for_the_previous_evening()
    {
        await _host.AddActivitiesAsync(
            Code(Fx.Mon(23, 45), Fx.Mon(23, 52), "fabrikam/web"),
            Fx.Act(ActivitySource.LocalGit, Fx.Local(Tuesday, 0, 5), null, a => { a.Kind = "commit"; a.Repo = "contoso/api"; }));

        var timeline = await _sut.GetDayTimelineAsync(Fx.Monday);
        var last = timeline.Slots[^1];

        Assert.Equal(Fx.Mon(23, 45), last.SlotStartUtc);
        Assert.Equal(2, last.Evidence.Count);
        // 7 min of VS Code on an unknown repo (630) vs 10 min of commit window on a known task (300).
        Assert.Equal("repo:fabrikam/web", last.GroupKey);
        Assert.Contains(last.Alternatives, c => c.TaskId == _dev.Id);
    }

    [Fact]
    public void Configuration_is_bound_from_the_engine_section()
    {
        var options = _host.Get<Microsoft.Extensions.Options.IOptions<EngineOptions>>().Value;
        Assert.Equal(7.5m, options.TargetHoursPerDay);
        Assert.Equal("claude-sonnet-5-5", options.Claude.Model);
        Assert.False(options.Claude.Enabled);
    }
}

public class EngineConfigurationTests
{
    [Fact]
    public async Task Working_day_is_8_hours_by_default_and_configurable()
    {
        await using (var host = new EngineTestHost(new()))
            Assert.Equal(8m, host.Get<Microsoft.Extensions.Options.IOptions<EngineOptions>>().Value.TargetHoursPerDay);

        await using (var host = new EngineTestHost(new() { ["Engine:TargetHoursPerDay"] = "7.5" }))
            Assert.Equal(7.5m, host.Get<Microsoft.Extensions.Options.IOptions<EngineOptions>>().Value.TargetHoursPerDay);
    }

    [Fact]
    public async Task Settings_override_defaults()
    {
        await using var host = new EngineTestHost(new()
        {
            ["Engine:MinActivityPerSlot"] = "00:02:00",
            ["Engine:EvidenceWindows:GitHub.commit"] = "00:30:00",
            ["Engine:SourceWeights:Focus"] = "2",
            ["Engine:Claude:Enabled"] = "true",
            ["Engine:Claude:Model"] = "claude-opus-5-5",
        });
        var o = host.Get<Microsoft.Extensions.Options.IOptions<EngineOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(2), o.MinActivityPerSlot);
        Assert.Equal(TimeSpan.FromMinutes(30), o.GetEvidenceWindow(ActivitySource.GitHub, "commit"));
        Assert.Equal(TimeSpan.FromMinutes(20), o.GetEvidenceWindow(ActivitySource.GitHub, "pr-review"));
        Assert.Equal(2, o.GetSourceWeight(ActivitySource.Focus));
        Assert.Equal(1.5, o.GetSourceWeight(ActivitySource.VsCode));
        Assert.True(o.Claude.Enabled);
        Assert.Equal("claude-opus-5-5", o.Claude.Model);
    }
}
