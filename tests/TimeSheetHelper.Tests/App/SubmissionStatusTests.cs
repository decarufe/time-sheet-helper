using TimeSheetHelper.App.Components.Shared;

namespace TimeSheetHelper.Tests.App;

public sealed class SubmissionStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>A week ready to be typed: fresh and successful simulation, nothing open.</summary>
    private static SubmissionStatusInput Ready() => new(
        GapFx.Analysis([], GapFx.Summary(cells: 2, cellsWithoutGap: 2, weekHours: 37.5m, target: 37.5m, within: true, hasSimulation: true, canFill: true)),
        OpenBlocking: 0,
        OpenToVerify: 0,
        Ignored: 0,
        FirstBlockingId: null,
        DraftSubmitted: false,
        HasRows: true,
        HasSimulation: true,
        SimulationFresh: true,
        SimulationSucceeded: true,
        SimulatedUtc: Now.AddMinutes(-3),
        SimulationRunning: false,
        FillRunning: false,
        Refreshing: false,
        NowUtc: Now);

    [Fact]
    public void A_fresh_successful_simulation_with_nothing_blocking_allows_the_fill()
    {
        var status = SubmissionStatusBuilder.Build(Ready());

        Assert.True(status.CanFill);
        Assert.Null(status.FillDisabledReason);
        Assert.Equal("Prêt à saisir", status.Verdict);
        Assert.Equal(StatusLevel.Ok, status.VerdictLevel);
        Assert.Equal(2, status.WithoutGap);
    }

    [Fact]
    public void Gaps_to_verify_do_not_block_the_fill_but_are_named_in_the_verdict()
    {
        var one = SubmissionStatusBuilder.Build(Ready() with { OpenToVerify = 1 });
        var several = SubmissionStatusBuilder.Build(Ready() with { OpenToVerify = 3 });

        Assert.True(one.CanFill);
        Assert.Equal("Prêt à saisir — 1 point à vérifier reste ouvert", one.Verdict);
        Assert.Equal(StatusLevel.Warn, one.VerdictLevel);
        Assert.True(several.CanFill);
        Assert.Equal("Prêt à saisir — 3 points à vérifier restent ouverts", several.Verdict);
    }

    [Theory]
    [InlineData(1, "1 écart bloquant reste à régler.", "1 écart bloque la saisie")]
    [InlineData(2, "2 écarts bloquants restent à régler.", "2 écarts bloquent la saisie")]
    public void Blocking_gaps_disable_the_fill_and_say_how_many_remain(int blocking, string reason, string verdict)
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { OpenBlocking = blocking, FirstBlockingId = "gap-1" });

        Assert.False(status.CanFill);
        Assert.Equal(reason, status.FillDisabledReason);
        Assert.Equal(verdict, status.Verdict);
        Assert.Equal(StatusLevel.Danger, status.VerdictLevel);
        Assert.Equal("gap-1", status.FirstBlockingId);
    }

    [Fact]
    public void The_reasons_are_given_in_a_fixed_order_the_most_basic_first()
    {
        var everythingWrong = Ready() with
        {
            DraftSubmitted = true, HasRows = false, SimulationRunning = true, HasSimulation = false,
            SimulationFresh = false, OpenBlocking = 2, SimulationSucceeded = false,
        };

        Assert.Contains("déjà saisie", SubmissionStatusBuilder.FillReason(everythingWrong));
        Assert.Contains("Aucune heure classée", SubmissionStatusBuilder.FillReason(everythingWrong with { DraftSubmitted = false }));
        Assert.Contains("opération est en cours", SubmissionStatusBuilder.FillReason(everythingWrong with { DraftSubmitted = false, HasRows = true }));
        Assert.Contains("Lancez d'abord la simulation", SubmissionStatusBuilder.FillReason(everythingWrong with { DraftSubmitted = false, HasRows = true, SimulationRunning = false }));
        Assert.Contains("a changé depuis la simulation", SubmissionStatusBuilder.FillReason(
            everythingWrong with { DraftSubmitted = false, HasRows = true, SimulationRunning = false, HasSimulation = true }));
        Assert.Contains("2 écarts bloquants", SubmissionStatusBuilder.FillReason(
            everythingWrong with { DraftSubmitted = false, HasRows = true, SimulationRunning = false, HasSimulation = true, SimulationFresh = true }));
        Assert.Contains("a signalé des problèmes", SubmissionStatusBuilder.FillReason(
            everythingWrong with { DraftSubmitted = false, HasRows = true, SimulationRunning = false, HasSimulation = true, SimulationFresh = true, OpenBlocking = 0 }));
    }

    [Fact]
    public void Nothing_to_fill_when_no_day_is_checked_and_the_verdict_asks_to_check_one()
    {
        var input = Ready() with { HasSelection = false };

        var status = SubmissionStatusBuilder.Build(input);

        Assert.False(status.CanFill);
        Assert.Contains("Cochez au moins une journée", SubmissionStatusBuilder.FillReason(input));
        Assert.Contains("Cochez la ou les journées", SubmissionStatusBuilder.Build(input with { HasSimulation = false, SimulationFresh = false }).Verdict);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void No_fill_while_anything_is_running_or_being_refreshed(bool simulating, bool filling, bool refreshing)
    {
        var input = Ready() with { SimulationRunning = simulating, FillRunning = filling, Refreshing = refreshing };

        Assert.Contains("opération est en cours", SubmissionStatusBuilder.FillReason(input));
        Assert.False(SubmissionStatusBuilder.Build(input).CanFill);
    }

    [Fact]
    public void A_stale_simulation_must_be_run_again_before_the_fill()
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { SimulationFresh = false });

        Assert.False(status.CanFill);
        Assert.Contains("a changé depuis la simulation", status.FillDisabledReason);
        Assert.Equal("Simulation périmée : le brouillon a changé", status.Verdict);
        Assert.Equal("périmée (il y a 3 min)", status.Steps[1].Text);
        Assert.Equal(StatusLevel.Warn, status.Steps[1].Level);
    }

    [Fact]
    public void An_incomplete_read_of_forecast_prevents_the_fill()
    {
        var summary = GapFx.Summary(hasSimulation: true, canFill: false);

        var status = SubmissionStatusBuilder.Build(Ready() with { Analysis = GapFx.Analysis([], summary) });

        Assert.False(status.CanFill);
        Assert.Contains("lecture de Forecast est incomplète", status.FillDisabledReason);
    }

    [Fact]
    public void A_submitted_draft_is_reported_as_typed_and_nothing_more_can_be_typed()
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { DraftSubmitted = true });

        Assert.False(status.CanFill);
        Assert.Equal("Semaine saisie dans Forecast", status.Verdict);
        Assert.Equal("saisi dans Forecast", status.Steps[0].Text);
        Assert.Equal("faite", status.Steps[2].Text);
    }

    [Fact]
    public void Before_any_simulation_the_verdict_asks_for_one()
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { HasSimulation = false, SimulatedUtc = null, SimulationFresh = false });

        Assert.Equal("Lancez la simulation pour savoir si la semaine est prête", status.Verdict);
        Assert.Equal(StatusLevel.Idle, status.VerdictLevel);
        Assert.Equal("à lancer", status.Steps[1].Text);
        Assert.Equal("bloquée", status.Steps[2].Text);
    }

    [Fact]
    public void An_empty_draft_has_nothing_to_type()
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { HasRows = false });

        Assert.Equal("Rien à saisir pour cette semaine", status.Verdict);
        Assert.Equal("vide", status.Steps[0].Text);
        Assert.Equal(StatusLevel.Warn, status.Steps[0].Level);
        Assert.False(status.CanFill);
    }

    [Fact]
    public void The_three_steps_follow_the_progress_of_the_week()
    {
        var running = SubmissionStatusBuilder.Build(Ready() with { SimulationRunning = true, Refreshing = true });
        Assert.Equal(["Brouillon", "Simulation", "Saisie"], running.Steps.Select(s => s.Name));
        Assert.Equal("mise à jour…", running.Steps[0].Text);
        Assert.Equal("en cours…", running.Steps[1].Text);

        var filling = SubmissionStatusBuilder.Build(Ready() with { FillRunning = true });
        Assert.Equal("en cours…", filling.Steps[2].Text);

        var ready = SubmissionStatusBuilder.Build(Ready());
        Assert.Equal("à jour", ready.Steps[0].Text);
        Assert.Equal("réussie (il y a 3 min)", ready.Steps[1].Text);
        Assert.Equal("prête", ready.Steps[2].Text);

        var gaps = SubmissionStatusBuilder.Build(Ready() with { SimulationSucceeded = false, OpenBlocking = 1 });
        Assert.Equal("écarts à régler (il y a 3 min)", gaps.Steps[1].Text);
        Assert.Equal(StatusLevel.Danger, gaps.Steps[1].Level);
    }

    [Fact]
    public void The_week_is_shown_against_the_objective_with_the_tolerance_when_reached()
    {
        var status = SubmissionStatusBuilder.Build(Ready());

        Assert.Equal("Semaine 37,50 h / 37,50 h (objectif atteint, tolérance ±0,25 h)", status.WeekText);
        Assert.Equal(StatusLevel.Ok, status.WeekLevel);
    }

    [Fact]
    public void A_week_out_of_the_tolerance_shows_its_difference_and_does_not_block()
    {
        var summary = GapFx.Summary(weekHours: 36m, target: 37.5m, within: false, hasSimulation: true, canFill: true);

        var status = SubmissionStatusBuilder.Build(Ready() with { Analysis = GapFx.Analysis([], summary) });

        Assert.Equal($"Semaine 36,00 h / 37,50 h ({TimesheetUi.SignedHours(-1.5m)} par rapport à l'objectif)", status.WeekText);
        Assert.Equal(StatusLevel.Warn, status.WeekLevel);
        Assert.True(status.CanFill);
    }

    [Fact]
    public void Without_an_objective_only_the_week_total_is_shown()
    {
        var summary = GapFx.Summary(weekHours: 30m);

        var status = SubmissionStatusBuilder.Build(Ready() with { Analysis = GapFx.Analysis([], summary) });

        Assert.Equal("Semaine 30,00 h", status.WeekText);
        Assert.Equal(StatusLevel.Idle, status.WeekLevel);
    }

    [Fact]
    public void Without_an_analysis_there_is_no_week_text_and_no_cell_count()
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { Analysis = null });

        Assert.Equal("", status.WeekText);
        Assert.Equal(0, status.WithoutGap);
    }

    [Fact]
    public void The_announcement_summarises_the_counters_for_a_screen_reader()
    {
        var status = SubmissionStatusBuilder.Build(Ready() with { OpenBlocking = 2, OpenToVerify = 3, Ignored = 1 });

        Assert.Equal("2 écarts bloquent la saisie. 2 bloquants, 3 à vérifier, 2 sans écart.", status.Announcement);
        Assert.Equal(1, status.Ignored);
        Assert.Contains("1 bloquant,", SubmissionStatusBuilder.Build(Ready() with { OpenBlocking = 1 }).Announcement);
    }
}
