using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

public sealed class SessionGapStateTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);

    private static SubmissionGap Local(string message = "m", int day = 0) =>
        GapFx.Gap(SubmissionGapType.UnclassifiedRemaining, SubmissionGapSeverity.ToVerify, taskId: null, day: day, message: message);

    private static SubmissionGap Forecast(int taskId = 1, SubmissionGapSeverity severity = SubmissionGapSeverity.Blocking) =>
        GapFx.Gap(SubmissionGapType.TaskNotFound, severity, taskId: taskId);

    private static SubmissionAnalysis Analysis(bool simulation, params SubmissionGap[] gaps) =>
        GapFx.Analysis(gaps, GapFx.Summary(hasSimulation: simulation));

    [Fact]
    public void A_gap_to_verify_can_be_ignored_for_the_session_and_restored()
    {
        var session = new SessionGapState();
        var gap = Local();

        Assert.True(session.Ignore(gap));
        Assert.True(session.IsIgnored(gap));
        Assert.Equal(1, session.IgnoredCount);

        Assert.True(session.Restore(gap.Id));
        Assert.False(session.IsIgnored(gap));
        Assert.False(session.Restore(gap.Id));
    }

    [Fact]
    public void A_blocking_gap_cannot_be_ignored()
    {
        var session = new SessionGapState();
        var gap = Forecast();

        Assert.False(session.Ignore(gap));
        Assert.False(session.IsIgnored(gap));
        Assert.Equal(0, session.IgnoredCount);
    }

    [Fact]
    public void An_ignored_gap_whose_explanation_changed_is_no_longer_ignored()
    {
        var session = new SessionGapState();
        var gap = Local("1,25 h non classées");
        session.Ignore(gap);

        var changed = Local("2,00 h non classées");

        Assert.False(session.IsIgnored(changed));
        session.Reconcile(Analysis(false, changed));
        Assert.Equal(0, session.IgnoredCount);
    }

    [Fact]
    public void An_ignored_gap_whose_hours_changed_is_no_longer_ignored()
    {
        var session = new SessionGapState();
        var gap = GapFx.Gap(SubmissionGapType.ForecastHigher, SubmissionGapSeverity.ToVerify, expected: 2m, forecast: 3m);
        session.Ignore(gap);

        Assert.True(session.IsIgnored(gap));
        Assert.False(session.IsIgnored(gap with { ForecastHours = 4m }));
    }

    [Fact]
    public void Ignoring_does_not_touch_the_draft_so_the_gap_stays_in_the_analysis()
    {
        var session = new SessionGapState();
        var gap = Local();
        session.Ignore(gap);

        session.Reconcile(Analysis(false, gap));

        Assert.True(session.IsIgnored(gap));
        Assert.Empty(session.Resolved);
    }

    [Fact]
    public void A_gap_that_goes_away_between_two_analyses_is_settled_during_the_session()
    {
        var session = new SessionGapState(() => Now);
        var gap = Local();
        session.Reconcile(Analysis(false, gap));

        session.Reconcile(Analysis(false));

        var resolved = Assert.Single(session.Resolved);
        Assert.Equal(gap.Id, resolved.Id);
        Assert.Equal(gap.Title, resolved.Title);
        Assert.Equal(SubmissionGapSeverity.ToVerify, resolved.Severity);
        Assert.Equal(Now, resolved.AtUtc);
        Assert.Equal(GapLabels.Context(gap), resolved.Context);
    }

    [Fact]
    public void A_settled_gap_that_comes_back_is_no_longer_listed_as_settled()
    {
        var session = new SessionGapState();
        var gap = Local();
        session.Reconcile(Analysis(false, gap));
        session.Reconcile(Analysis(false));

        session.Reconcile(Analysis(false, gap));

        Assert.Empty(session.Resolved);
    }

    [Fact]
    public void Info_gaps_are_not_reported_as_settled()
    {
        var session = new SessionGapState();
        var info = GapFx.Gap(SubmissionGapType.ForecastTimeWithoutProject, SubmissionGapSeverity.Info, taskId: null);
        session.Reconcile(Analysis(true, info));

        session.Reconcile(Analysis(true));

        Assert.Empty(session.Resolved);
    }

    [Fact]
    public void The_latest_settled_gap_is_listed_first()
    {
        var session = new SessionGapState();
        var first = Local(day: 0);
        var second = Local(day: 1);
        session.Reconcile(Analysis(false, first));
        session.Reconcile(Analysis(false, second));
        session.Reconcile(Analysis(false));

        Assert.Equal([second.Id, first.Id], session.Resolved.Select(r => r.Id));
    }

    [Fact]
    public void Gaps_from_forecast_are_compared_only_between_analyses_that_both_had_a_simulation()
    {
        var session = new SessionGapState();
        var fromForecast = Forecast();
        session.Reconcile(Analysis(true, fromForecast));

        // Without a simulation Forecast's gaps are simply not known: they have not been settled.
        session.Reconcile(Analysis(false));
        Assert.Empty(session.Resolved);

        // The next simulation is compared with the last one that had one.
        session.Reconcile(Analysis(true));
        Assert.Equal([fromForecast.Id], session.Resolved.Select(r => r.Id));
    }

    [Fact]
    public void The_first_analysis_with_a_simulation_has_nothing_to_compare_with()
    {
        var session = new SessionGapState();

        session.Reconcile(Analysis(true, Forecast()));

        Assert.Empty(session.Resolved);
    }

    [Fact]
    public void An_ignored_forecast_gap_that_disappears_with_a_simulation_is_forgotten()
    {
        var session = new SessionGapState();
        var gap = Forecast(severity: SubmissionGapSeverity.ToVerify);
        session.Reconcile(Analysis(true, gap));
        session.Ignore(gap);

        session.Reconcile(Analysis(false));
        Assert.Equal(1, session.IgnoredCount);

        session.Reconcile(Analysis(true));
        Assert.Equal(0, session.IgnoredCount);
    }

    [Fact]
    public void An_ignored_local_gap_that_disappears_is_forgotten()
    {
        var session = new SessionGapState();
        var gap = Local();
        session.Reconcile(Analysis(false, gap));
        session.Ignore(gap);

        session.Reconcile(Analysis(false));

        Assert.Equal(0, session.IgnoredCount);
    }

    [Fact]
    public void Only_the_latest_settled_gaps_are_kept()
    {
        var session = new SessionGapState();
        var gaps = Enumerable.Range(0, SessionGapState.MaxResolved + 5)
            .Select(i => GapFx.Gap(SubmissionGapType.UnclassifiedRemaining, SubmissionGapSeverity.ToVerify, taskId: i))
            .ToArray();
        session.Reconcile(Analysis(false, gaps));

        session.Reconcile(Analysis(false));

        Assert.Equal(SessionGapState.MaxResolved, session.Resolved.Count);
    }

    [Fact]
    public void Reset_forgets_everything_of_the_week()
    {
        var session = new SessionGapState();
        var gap = Local();
        session.Reconcile(Analysis(false, gap));
        session.Ignore(gap);
        session.Reconcile(Analysis(false));

        session.Reset();

        Assert.Empty(session.Resolved);
        Assert.Equal(0, session.IgnoredCount);
        // The first analysis after a reset has no past to compare with.
        session.Reconcile(Analysis(false));
        Assert.Empty(session.Resolved);
    }

    [Theory]
    [InlineData(SubmissionGapType.UnclassifiedRemaining, true)]
    [InlineData(SubmissionGapType.DayBelowTarget, true)]
    [InlineData(SubmissionGapType.DayAboveTarget, true)]
    [InlineData(SubmissionGapType.TaskNotFound, false)]
    [InlineData(SubmissionGapType.ForecastHigher, false)]
    public void Only_gaps_born_from_the_local_draft_exist_without_a_simulation(SubmissionGapType type, bool local) =>
        Assert.Equal(local, SessionGapState.IsLocal(type));
}
