using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast.Submission;
using static TimeSheetHelper.Tests.App.GapFx;

namespace TimeSheetHelper.Tests.App;

public sealed class GapActionPlannerTests
{
    private static readonly GapActionContext Context = new(Monday, _ => true);

    private static IReadOnlyList<GapActionView> Plan(SubmissionGap gap, GapActionContext? context = null) =>
        GapActionPlanner.Plan(gap, context ?? Context);

    private static GapActionView Of(IReadOnlyList<GapActionView> views, SubmissionGapResolution resolution) =>
        Assert.Single(views, v => v.Resolution == resolution);

    [Fact]
    public void Actions_come_recommended_first_then_the_available_ones_then_the_disabled_ones()
    {
        var gap = Gap(actions:
        [
            Action(SubmissionGapResolution.OpenInForecast),
            Action(SubmissionGapResolution.RefreshTasks),
            Action(SubmissionGapResolution.LinkTask, recommended: true),
            Action(SubmissionGapResolution.ReplaceTask, available: false, reason: "Plus tard."),
        ]);

        var views = Plan(gap);

        Assert.Equal(
            [SubmissionGapResolution.LinkTask, SubmissionGapResolution.RefreshTasks, SubmissionGapResolution.OpenInForecast, SubmissionGapResolution.ReplaceTask],
            views.Select(v => v.Resolution));
        Assert.Equal(
            [GapActionKind.Button, GapActionKind.Button, GapActionKind.Disabled, GapActionKind.Disabled],
            views.Select(v => v.Kind));
        Assert.True(views[0].IsRecommended);
    }

    [Fact]
    public void An_action_the_page_runs_is_a_button_with_its_french_label()
    {
        var views = Plan(Gap(actions: [Action(SubmissionGapResolution.RefreshTasks)]));

        var view = Assert.Single(views);
        Assert.Equal(GapActionKind.Button, view.Kind);
        Assert.Equal("Rafraîchir les tâches depuis Forecast", view.Label);
        Assert.Null(view.Reason);
        Assert.Null(view.Href);
    }

    [Theory]
    [InlineData(SubmissionGapResolution.LinkTask)]
    [InlineData(SubmissionGapResolution.CreateTask)]
    [InlineData(SubmissionGapResolution.RefreshTasks)]
    [InlineData(SubmissionGapResolution.ReactivateTask)]
    [InlineData(SubmissionGapResolution.RecalculateDraft)]
    [InlineData(SubmissionGapResolution.Retry)]
    [InlineData(SubmissionGapResolution.ReadAgain)]
    [InlineData(SubmissionGapResolution.ResumeFill)]
    public void Every_resolution_the_page_runs_is_offered_as_a_button(SubmissionGapResolution resolution) =>
        Assert.Equal(GapActionKind.Button, Plan(Gap(actions: [Action(resolution)]))[0].Kind);

    [Theory]
    [InlineData(SubmissionGapResolution.AddComplement)]
    [InlineData(SubmissionGapResolution.AlignDraftOnForecast)]
    [InlineData(SubmissionGapResolution.AcceptForecastHours)]
    [InlineData(SubmissionGapResolution.ReplaceTask)]
    public void A_resolution_of_the_next_phase_is_disabled_with_its_reason_even_if_the_backend_offers_it(SubmissionGapResolution resolution)
    {
        var unavailable = Plan(Gap(actions: [Action(resolution, available: false, reason: "Raison du serveur.")]))[0];
        var offered = Plan(Gap(actions: [Action(resolution)]))[0];

        Assert.Equal(GapActionKind.Disabled, unavailable.Kind);
        Assert.Equal("Raison du serveur.", unavailable.Reason);
        Assert.Equal(GapActionKind.Disabled, offered.Kind);
        Assert.Equal(GapActionPlanner.LaterStep, offered.Reason);
    }

    [Fact]
    public void An_unavailable_action_without_a_reason_gets_the_generic_one()
    {
        var view = Plan(Gap(actions: [Action(SubmissionGapResolution.AddComplement, available: false)]))[0];

        Assert.Equal(GapActionKind.Disabled, view.Kind);
        Assert.Equal(GapActionPlanner.LaterStep, view.Reason);
    }

    [Fact]
    public void Opening_forecast_is_disabled_because_no_stable_address_is_known()
    {
        var view = Plan(Gap(actions: [Action(SubmissionGapResolution.OpenInForecast)]))[0];

        Assert.Equal(GapActionKind.Disabled, view.Kind);
        Assert.Equal(GapActionPlanner.ForecastUrlUnknown, view.Reason);
        Assert.Null(view.Href);
    }

    [Fact]
    public void Signing_in_is_a_link_to_the_sources_page()
    {
        var view = Plan(Gap(actions: [Action(SubmissionGapResolution.SignIn, recommended: true)]))[0];

        Assert.Equal(GapActionKind.Link, view.Kind);
        Assert.Equal("sources", view.Href);
        Assert.True(view.IsRecommended);
    }

    [Fact]
    public void Classifying_work_is_a_link_to_the_unclassified_section_of_the_week()
    {
        var gap = Gap(SubmissionGapType.UnclassifiedRemaining, SubmissionGapSeverity.ToVerify, taskId: null, actions: [Action(SubmissionGapResolution.ReclassifySlots)]);

        var view = Plan(gap)[0];

        Assert.Equal(GapActionKind.Link, view.Kind);
        Assert.Equal("./?semaine=2026-10-12#unclassified-title", view.Href);
    }

    [Theory]
    [InlineData(SubmissionGapResolution.ViewDaySlots)]
    [InlineData(SubmissionGapResolution.MarkNotWork)]
    [InlineData(SubmissionGapResolution.Remove)]
    public void Actions_on_the_slots_of_a_day_are_links_to_that_day(SubmissionGapResolution resolution)
    {
        var view = Plan(Gap(day: 2, actions: [Action(resolution)]))[0];

        Assert.Equal(GapActionKind.Link, view.Kind);
        Assert.Equal("jour/2026-10-14", view.Href);
    }

    [Fact]
    public void A_gap_without_a_day_sends_the_slot_actions_to_the_week()
    {
        var gap = Gap(taskId: null, actions: [Action(SubmissionGapResolution.ViewDaySlots)]) with { Date = null };

        Assert.Equal("./?semaine=2026-10-12", Plan(gap)[0].Href);
    }

    [Theory]
    [InlineData(SubmissionGapResolution.LinkTask)]
    [InlineData(SubmissionGapResolution.CreateTask)]
    [InlineData(SubmissionGapResolution.ReactivateTask)]
    public void Editing_a_task_that_no_longer_exists_is_disabled_with_its_reason(SubmissionGapResolution resolution)
    {
        var view = Plan(Gap(taskId: 7, actions: [Action(resolution)]), new GapActionContext(Monday, id => id != 7))[0];

        Assert.Equal(GapActionKind.Disabled, view.Kind);
        Assert.Equal(GapActionPlanner.TaskMissing, view.Reason);
    }

    [Fact]
    public void A_blocking_gap_cannot_be_ignored_and_says_why()
    {
        var gap = Gap(actions: [Action(SubmissionGapResolution.IgnoreKnowingly, available: false, reason: "Phase A2.")]);

        var view = Plan(gap)[0];

        Assert.Equal(GapActionKind.Disabled, view.Kind);
        Assert.Equal(GapActionPlanner.BlockingCannotBeIgnored, view.Reason);
    }

    [Theory]
    [InlineData(SubmissionGapSeverity.ToVerify)]
    [InlineData(SubmissionGapSeverity.Info)]
    public void A_gap_that_does_not_block_can_be_ignored_for_the_session_even_if_the_backend_lists_it_as_unavailable(SubmissionGapSeverity severity)
    {
        var gap = Gap(SubmissionGapType.ForecastWarning, severity, actions: [Action(SubmissionGapResolution.IgnoreKnowingly, available: false, reason: "Phase A2.")]);

        var view = Assert.Single(Plan(gap));

        Assert.Equal(GapActionKind.Button, view.Kind);
        Assert.Null(view.Reason);
    }

    [Fact]
    public void A_gap_to_verify_gets_the_ignore_action_when_the_backend_does_not_list_it()
    {
        var gap = Gap(SubmissionGapType.UnclassifiedRemaining, SubmissionGapSeverity.ToVerify, taskId: null, actions: [Action(SubmissionGapResolution.ReclassifySlots, recommended: true)]);

        var views = Plan(gap);

        Assert.Equal([SubmissionGapResolution.ReclassifySlots, SubmissionGapResolution.IgnoreKnowingly], views.Select(v => v.Resolution));
        Assert.Equal(GapActionKind.Button, Of(views, SubmissionGapResolution.IgnoreKnowingly).Kind);
        Assert.False(Of(views, SubmissionGapResolution.IgnoreKnowingly).IsRecommended);
    }

    [Fact]
    public void A_blocking_gap_is_not_given_an_ignore_action_it_did_not_list()
    {
        var views = Plan(Gap(actions: [Action(SubmissionGapResolution.RefreshTasks)]));

        Assert.DoesNotContain(views, v => v.Resolution == SubmissionGapResolution.IgnoreKnowingly);
    }

    [Fact]
    public void The_week_and_day_links_use_the_iso_date()
    {
        Assert.Equal("./?semaine=2026-10-12", GapActionPlanner.WeekHref(Monday));
        Assert.Equal("./?semaine=2026-10-12#unclassified-title", GapActionPlanner.WeekHref(Monday, unclassified: true));
        Assert.Equal("jour/2026-10-16", GapActionPlanner.DayHref(Day(4)));
    }
}
