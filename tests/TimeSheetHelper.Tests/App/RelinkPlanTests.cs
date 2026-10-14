using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

public sealed class RelinkPlanTests
{
    private static ForecastTaskMatch Match(string code = "T3382", int? projectId = 1078) =>
        new(code, "Intégration paie v2", null, projectId, "P1078", "Paie", "Client Alpha");

    private static TimesheetDraft Draft(params (int Task, int Day, decimal Hours)[] entries) => new()
    {
        WeekStart = GapFx.Monday,
        Entries = entries.Select(e => new TimesheetEntry { ForecastTaskId = e.Task, Date = GapFx.Day(e.Day), Hours = e.Hours }).ToList(),
    };

    [Fact]
    public void The_remote_task_takes_the_task_number_as_its_identity_and_keeps_the_project()
    {
        var remote = RelinkPlan.ToRemote(Match());

        Assert.Equal("T3382", remote.Id);
        Assert.Equal("T3382", remote.Code);
        Assert.Equal("Intégration paie v2", remote.Title);
        Assert.Equal("Paie", remote.ProjectName);
        Assert.Equal("P1078", remote.ProjectCode);
        Assert.Equal("Client Alpha", remote.ClientName);
        Assert.Equal("1078", remote.ProjectId);
    }

    [Fact]
    public void The_remote_task_has_no_project_id_when_the_match_has_none()
    {
        Assert.Null(RelinkPlan.ToRemote(Match(projectId: null)).ProjectId);
    }

    [Fact]
    public void Another_local_task_with_the_same_number_is_the_merge_target()
    {
        var local = GapFx.Task(1, "Ancienne", externalId: "ancienne");
        var known = GapFx.Task(2, "Déjà connue", externalId: "T3382");

        var target = RelinkPlan.MergeTarget([local, known], local.Id, "T3382");

        Assert.Same(known, target);
    }

    [Fact]
    public void Without_another_task_carrying_the_number_the_link_is_plain()
    {
        var local = GapFx.Task(1, "Ancienne", externalId: "T3382");
        var other = GapFx.Task(2, "Autre", externalId: "T9999");

        Assert.Null(RelinkPlan.MergeTarget([local, other], local.Id, "T3382"));
    }

    [Fact]
    public void The_number_must_match_exactly_to_count_as_the_same_task()
    {
        var local = GapFx.Task(1, "Ancienne", externalId: "ancienne");
        var other = GapFx.Task(2, "Autre", externalId: "t3382");

        Assert.Null(RelinkPlan.MergeTarget([local, other], local.Id, "T3382"));
    }

    [Fact]
    public void The_merge_impact_counts_the_cells_the_hours_and_the_rules_of_the_local_task_only()
    {
        var local = GapFx.Task(1, "Ancienne", externalId: "ancienne");
        var target = GapFx.Task(2, "Déjà connue", externalId: "T3382");
        var draft = Draft((1, 0, 1m), (1, 0, 0.5m), (1, 2, 2m), (1, 3, 0m), (2, 0, 4m));

        var impact = RelinkPlan.ImpactOf(local, target, draft, rules: 3);

        Assert.Equal("Ancienne", impact.LocalName);
        Assert.Equal("Déjà connue", impact.TargetName);
        Assert.Equal("T3382", impact.TargetCode);
        Assert.Equal(2, impact.DraftCells);
        Assert.Equal(3.5m, impact.DraftHours);
        Assert.Equal(3, impact.Rules);
    }

    [Fact]
    public void A_local_task_without_hours_in_the_draft_merges_with_an_empty_impact()
    {
        var local = GapFx.Task(1, "Ancienne", externalId: "ancienne");
        var target = GapFx.Task(2, "Déjà connue", externalId: "T3382");

        var impact = RelinkPlan.ImpactOf(local, target, Draft((2, 0, 4m)), rules: 0);

        Assert.Equal(0, impact.DraftCells);
        Assert.Equal(0m, impact.DraftHours);
    }
}
