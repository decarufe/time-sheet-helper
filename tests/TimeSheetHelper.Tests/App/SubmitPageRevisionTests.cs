using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// Revision of the Submit page after the review of Gadget: the rules of the fill that used to live in the page (FillGuard), and the
/// defects D2 (cells "without gap"), D3 (ignoring a blocking gap), D4 (pending group assignments in the fill dialog).
/// </summary>
public sealed class SubmitPageRevisionTests
{
    private static readonly SubmissionGapService Service = new();

    private static TimesheetDraft Draft(params (int Task, int Day, decimal Hours)[] entries) => new()
    {
        WeekStart = GapFx.Monday,
        Entries = entries.Select(e => new TimesheetEntry { ForecastTaskId = e.Task, Date = GapFx.Day(e.Day), Hours = e.Hours }).ToList(),
    };

    private static Dictionary<int, ForecastTask> Tasks(params int[] ids) => ids.ToDictionary(i => i, i => GapFx.Task(i, $"Tâche {i}"));

    // ── FillGuard: fingerprint ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_fingerprint_does_not_depend_on_how_the_hours_of_a_cell_are_split_nor_on_zero_hours()
    {
        var split = Draft((2, 1, 1m), (1, 0, 0.5m), (1, 0, 1m), (3, 2, 0m));
        var merged = Draft((1, 0, 1.5m), (2, 1, 1m));

        Assert.Equal(FillGuard.Fingerprint(merged), FillGuard.Fingerprint(split));
    }

    [Fact]
    public void The_fingerprint_changes_with_the_hours_the_task_the_day_or_the_week()
    {
        var baseline = FillGuard.Fingerprint(Draft((1, 0, 1m)));

        Assert.NotEqual(baseline, FillGuard.Fingerprint(Draft((1, 0, 1.25m))));
        Assert.NotEqual(baseline, FillGuard.Fingerprint(Draft((2, 0, 1m))));
        Assert.NotEqual(baseline, FillGuard.Fingerprint(Draft((1, 1, 1m))));
        var otherWeek = Draft((1, 0, 1m));
        otherWeek.WeekStart = GapFx.Monday.AddDays(7);
        Assert.NotEqual(baseline, FillGuard.Fingerprint(otherWeek));
    }

    // ── FillGuard: may the fill start ────────────────────────────────────────────────────────────

    [Fact]
    public void The_fill_is_refused_when_the_status_does_not_allow_it_even_if_the_draft_is_the_simulated_one()
    {
        var draft = Draft((1, 0, 1m));

        Assert.Equal(FillPreflight.NotAllowed, FillGuard.Preflight(false, draft, FillGuard.Fingerprint(draft)));
    }

    [Fact]
    public void The_fill_asks_for_a_new_simulation_when_the_draft_is_not_the_simulated_one()
    {
        var simulated = FillGuard.Fingerprint(Draft((1, 0, 1m)));

        Assert.Equal(FillPreflight.DraftChanged, FillGuard.Preflight(true, Draft((1, 0, 1.25m)), simulated));
        Assert.Equal(FillPreflight.DraftChanged, FillGuard.Preflight(true, Draft((1, 0, 1m), (2, 1, 1m)), simulated));
    }

    [Fact]
    public void The_fill_asks_for_a_new_simulation_when_the_draft_is_gone_already_typed_or_never_simulated()
    {
        var draft = Draft((1, 0, 1m));
        var fingerprint = FillGuard.Fingerprint(draft);
        var typed = Draft((1, 0, 1m));
        typed.SubmittedUtc = new DateTime(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);

        Assert.Equal(FillPreflight.DraftChanged, FillGuard.Preflight(true, null, fingerprint));
        Assert.Equal(FillPreflight.DraftChanged, FillGuard.Preflight(true, typed, fingerprint));
        Assert.Equal(FillPreflight.DraftChanged, FillGuard.Preflight(true, draft, null));
    }

    [Fact]
    public void The_fill_starts_only_on_the_simulated_draft_with_the_status_allowing_it()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 2m));

        Assert.Equal(FillPreflight.Allowed, FillGuard.Preflight(true, draft, FillGuard.Fingerprint(Draft((2, 1, 2m), (1, 0, 1m)))));
    }

    // ── FillGuard: may the week be marked as typed ───────────────────────────────────────────────

    [Fact]
    public void The_week_is_marked_typed_when_every_planned_cell_is_read_back_ok()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 2m));
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok), GapFx.Cell(2, 1, ForecastCellStatus.Ok));

        Assert.True(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2)));
    }

    [Theory]
    [InlineData(ForecastCellStatus.Planned)]
    [InlineData(ForecastCellStatus.Mismatch)]
    [InlineData(ForecastCellStatus.RowNotFound)]
    [InlineData(ForecastCellStatus.Error)]
    [InlineData(ForecastCellStatus.Locked)]
    [InlineData(ForecastCellStatus.Unknown)]
    public void The_week_stays_open_when_a_single_cell_is_not_verified_ok(ForecastCellStatus status)
    {
        var draft = Draft((1, 0, 1m), (2, 1, 2m));
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok), GapFx.Cell(2, 1, status));

        Assert.False(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2)));
    }

    [Fact]
    public void The_week_stays_open_when_the_report_holds_fewer_cells_than_the_draft_plans()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 2m));
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok));

        Assert.False(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2)));
    }

    [Fact]
    public void The_week_stays_open_when_the_report_holds_more_cells_than_the_draft_plans()
    {
        var draft = Draft((1, 0, 1m));
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok), GapFx.Cell(2, 1, ForecastCellStatus.Ok));

        Assert.False(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2)));
    }

    [Fact]
    public void An_empty_report_never_marks_the_week_even_when_nothing_was_planned()
    {
        Assert.False(FillGuard.IsWeekFullyEntered(GapFx.Report(), Draft(), Tasks(1)));
        Assert.False(FillGuard.IsWeekFullyEntered(GapFx.Report(), Draft((1, 0, 0m)), Tasks(1)));
    }

    [Fact]
    public void The_cells_planned_are_counted_like_the_fill_counts_them_zero_hours_and_unknown_tasks_excluded()
    {
        var draft = Draft((1, 0, 1m), (1, 0, 0.5m), (2, 1, 0m), (9, 2, 1m));

        Assert.True(FillGuard.IsWeekFullyEntered(GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok)), draft, Tasks(1, 2)));
        Assert.False(FillGuard.IsWeekFullyEntered(
            GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok), GapFx.Cell(2, 1, ForecastCellStatus.Ok)), draft, Tasks(1, 2)));
    }

    [Fact]
    public void A_day_scoped_verdict_only_expects_the_cells_of_the_selected_day_even_when_the_draft_holds_more()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 2m));
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok));

        Assert.True(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2), [GapFx.Day(0)]));
        Assert.False(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2)));
        Assert.False(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2), [GapFx.Day(0), GapFx.Day(1)]));
    }

    [Fact]
    public void A_null_or_empty_day_selection_expects_the_whole_draft_like_the_old_overload()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 2m));
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Ok), GapFx.Cell(2, 1, ForecastCellStatus.Ok));

        Assert.True(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2), null));
        Assert.True(FillGuard.IsWeekFullyEntered(report, draft, Tasks(1, 2), []));
    }

    // ── FillGuard: does the result still belong to the screen (D1) ───────────────────────────────

    [Fact]
    public void The_result_of_an_operation_is_shown_only_for_the_week_still_on_screen_and_while_the_page_lives()
    {
        Assert.True(FillGuard.ResultApplies(GapFx.Monday, GapFx.Monday, disposed: false));
        Assert.False(FillGuard.ResultApplies(GapFx.Monday, GapFx.Monday.AddDays(7), disposed: false));
        Assert.False(FillGuard.ResultApplies(GapFx.Monday, GapFx.Monday, disposed: true));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public void A_simulation_does_not_start_without_a_draft_on_a_typed_week_on_a_gone_page_or_under_a_running_fill(
        bool hasDraft, bool submitted, bool disposed, bool fillRunning, bool expected) =>
        Assert.Equal(expected, FillGuard.CanSimulate(hasDraft, submitted, disposed, fillRunning));

    // ── D2: cells without gap ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cells_not_attempted_after_an_interruption_are_not_listed_as_without_gap()
    {
        var draft = Draft((1, 0, 1m), (2, 0, 1m));
        var report = GapFx.Report(
            GapFx.Cell(1, 0, ForecastCellStatus.Ok),
            GapFx.Cell(2, 0, ForecastCellStatus.Error) with { Issue = ForecastCellIssue.NotAttempted, EntryState = ForecastEntryState.NotEntered });
        report.Interrupted = true;

        var analysis = Service.Analyze(draft, report, null);

        Assert.Equal([(1, GapFx.Day(0))], GapBoard.CellsWithoutGap(report, analysis.Gaps).Select(c => (c.ForecastTaskId, c.Date)));
        Assert.Equal(1, analysis.Summary.CellsWithoutGapCount);
    }

    [Fact]
    public void Cells_of_a_global_error_are_not_listed_as_without_gap()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 1m));
        var report = GapFx.Report(
            GapFx.Cell(1, 0, ForecastCellStatus.Error) with { Message = "Forecast injoignable" },
            GapFx.Cell(2, 1, ForecastCellStatus.Error) with { Message = "Forecast injoignable" });
        report.GlobalError = "Forecast injoignable";

        var analysis = Service.Analyze(draft, report, null);

        Assert.Empty(GapBoard.CellsWithoutGap(report, analysis.Gaps));
        Assert.Equal(0, analysis.Summary.CellsWithoutGapCount);
    }

    [Fact]
    public void Ready_and_already_entered_cells_are_the_only_ones_without_gap_and_the_service_and_the_board_agree()
    {
        var draft = Draft((1, 0, 1m), (2, 0, 1m), (3, 0, 1m), (4, 0, 1m));
        var report = GapFx.Report(
            GapFx.Cell(1, 0, ForecastCellStatus.Planned),
            GapFx.Cell(2, 0, ForecastCellStatus.Ok),
            GapFx.Cell(3, 0, ForecastCellStatus.Unknown) with { Issue = ForecastCellIssue.ReadIncomplete },
            GapFx.Cell(4, 0, ForecastCellStatus.Ok) with { Findings = [new ForecastCellFinding(ForecastFindingKind.TaskBlocked, "Bloquée")] });
        report.ReadTruncated = true;

        var analysis = Service.Analyze(draft, report, null);
        var clean = GapBoard.CellsWithoutGap(report, analysis.Gaps).Select(c => c.ForecastTaskId).Order().ToList();

        Assert.Equal([1, 2], clean);
        Assert.Equal(clean.Count, analysis.Summary.CellsWithoutGapCount);
    }

    // ── D3: a blocking gap is never ignored ──────────────────────────────────────────────────────

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

    // ── D4: pending group assignments in the fill dialog ─────────────────────────────────────────

    private static PendingGroupAssignment Pending(string key, int slots) => new(
        Guid.NewGuid(), GapFx.Monday, key, GroupKind.Repo, 1,
        new ActivityGroup(key, [ActivitySource.VsCode], [], [], [], TimeSpan.FromHours(1)),
        Enumerable.Range(0, slots).Select(i => new PendingSlot(new DateTime(2026, 10, 12, 13, 0, 0, DateTimeKind.Utc).AddMinutes(15 * i), false, null, null)).ToList());

    private static IReadOnlyList<string> Fill(PendingAssignments? pending) =>
        SubmitDialogs.FillParagraphs(8m, 3, GapFx.Monday, [], 0, null, pending);

    [Fact]
    public void Pending_assignments_add_up_their_groups_and_their_hours()
    {
        var pending = PendingAssignments.Of([Pending("repo:a", 4), Pending("repo:b", 2)]);

        Assert.Equal(2, pending.Groups);
        Assert.Equal(1.5m, pending.Hours);
        Assert.Equal(PendingAssignments.None, PendingAssignments.Of([]));
    }

    [Fact]
    public void The_fill_dialog_says_how_many_groups_are_still_to_confirm_and_that_they_are_typed_as_they_are_without_blocking()
    {
        var paragraphs = Fill(new PendingAssignments(2, 1.5m));

        var line = Assert.Single(paragraphs, p => p.Contains("À confirmer"));
        Assert.StartsWith("2 groupes d'activités en attente", line);
        Assert.Contains("1,50 h", line);
        Assert.Contains("déjà comptée dans le brouillon", line);
        Assert.Contains("saisie dans Forecast telle quelle", line);
        Assert.Contains("ne bloque pas la saisie", line);
        Assert.Contains("pas envoyée pour approbation", paragraphs[^1]);
    }

    [Fact]
    public void One_pending_group_is_written_in_the_singular()
    {
        var line = Assert.Single(Fill(new PendingAssignments(1, 0.25m)), p => p.Contains("À confirmer"));

        Assert.StartsWith("1 groupe d'activités en attente", line);
    }

    [Fact]
    public void Without_pending_assignments_the_dialog_does_not_mention_them()
    {
        Assert.DoesNotContain(Fill(null), p => p.Contains("À confirmer"));
        Assert.DoesNotContain(Fill(PendingAssignments.None), p => p.Contains("À confirmer"));
        Assert.Equal(3, Fill(PendingAssignments.None).Count);
    }
}
