using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>A blocking gap is never ignored: the rule behind the "Écarts à régler" list of the Submit page.</summary>
public sealed class SubmitGapRulesTests
{
    [Fact]
    public void A_blocking_gap_keeps_its_other_actions_but_its_ignore_entry_is_disabled_with_the_reason_even_if_the_backend_offers_it()
    {
        var gap = GapFx.Gap(actions:
            [GapFx.Action(SubmissionGapResolution.LinkTask, recommended: true), GapFx.Action(SubmissionGapResolution.IgnoreKnowingly)]);

        var views = GapActionPlanner.Plan(gap, new GapActionContext(GapFx.Monday, _ => true));

        Assert.Equal(GapActionKind.Button, Assert.Single(views, v => v.Resolution == SubmissionGapResolution.LinkTask).Kind);
        var ignore = Assert.Single(views, v => v.Resolution == SubmissionGapResolution.IgnoreKnowingly);
        Assert.Equal(GapActionKind.Disabled, ignore.Kind);
        Assert.Equal(GapActionPlanner.BlockingCannotBeIgnored, ignore.Reason);
        Assert.Equal(SubmissionGapResolution.IgnoreKnowingly, views[^1].Resolution);
    }

    [Fact]
    public void A_gap_to_verify_is_offered_the_ignore_button_whether_or_not_the_backend_lists_it()
    {
        var listed = GapFx.Gap(SubmissionGapType.TaskClosed, SubmissionGapSeverity.ToVerify, actions: [GapFx.Action(SubmissionGapResolution.IgnoreKnowingly, available: false)]);
        var unlisted = GapFx.Gap(SubmissionGapType.TaskClosed, SubmissionGapSeverity.ToVerify, actions: []);
        var context = new GapActionContext(GapFx.Monday, _ => true);

        Assert.Equal(GapActionKind.Button, Assert.Single(GapActionPlanner.Plan(listed, context)).Kind);
        Assert.Equal(GapActionKind.Button, Assert.Single(GapActionPlanner.Plan(unlisted, context)).Kind);
    }

    [Fact]
    public void Ignoring_a_blocking_gap_is_refused_and_recorded_nowhere()
    {
        var blocking = GapFx.Gap();
        var session = new SessionGapState();

        Assert.False(session.Ignore(blocking));

        Assert.Equal(0, session.IgnoredCount);
        Assert.False(session.IsIgnored(blocking));
        Assert.Equal(1, GapBoard.Build([blocking], session).OpenBlockingCount);
    }

    [Fact]
    public void A_gap_ignored_while_to_verify_stops_being_ignored_when_it_becomes_blocking_under_the_same_id()
    {
        var toVerify = GapFx.Gap(SubmissionGapType.TaskClosed, SubmissionGapSeverity.ToVerify);
        var blocking = GapFx.Gap(SubmissionGapType.TaskClosed, SubmissionGapSeverity.Blocking);
        var session = new SessionGapState();
        Assert.Equal(toVerify.Id, blocking.Id);
        session.Reconcile(GapFx.Analysis([toVerify]));
        Assert.True(session.Ignore(toVerify));

        Assert.False(session.IsIgnored(blocking));
        session.Reconcile(GapFx.Analysis([blocking]));

        Assert.Equal(0, session.IgnoredCount);
        Assert.Equal(1, GapBoard.Build([blocking], session).OpenBlockingCount);
    }
}
