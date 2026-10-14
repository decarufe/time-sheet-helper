using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// Review of the Submit page logic (Gadget): the rules of the fill and of the gaps checked through the real pieces the page wires
/// together (gap service, board, session, status, dialog), plus the context handed to the gap service.
/// </summary>
public sealed class SubmitPageGadgetTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);
    private static readonly SubmissionGapService Service = new();

    private static TimesheetDraft Draft(params (int Task, int Day, decimal Hours)[] entries) => new()
    {
        WeekStart = GapFx.Monday,
        Entries = entries.Select(e => new TimesheetEntry { ForecastTaskId = e.Task, Date = GapFx.Day(e.Day), Hours = e.Hours }).ToList(),
    };

    private static ForecastCellResult Closed(int task, int day, string message = "Tâche terminée") =>
        GapFx.Cell(task, day) with { Findings = [new ForecastCellFinding(ForecastFindingKind.TaskClosed, message)] };

    private static ForecastCellResult Missing(int task, int day) => GapFx.Cell(task, day, ForecastCellStatus.RowNotFound);

    /// <summary>What the page computes for the fill: the board counts the gaps still open, the status decides.</summary>
    private static SubmissionStatus StatusOf(SubmissionAnalysis analysis, SessionGapState session, ForecastFillReport report, bool fresh = true)
    {
        var board = GapBoard.Build(analysis.Gaps, session);
        return SubmissionStatusBuilder.Build(new SubmissionStatusInput(
            analysis, board.OpenBlockingCount, board.OpenToVerifyCount, session.IgnoredCount, board.FirstBlocking?.Id,
            DraftSubmitted: false, HasRows: true, HasSimulation: true, SimulationFresh: fresh, SimulationSucceeded: report.Success,
            SimulatedUtc: Now.AddMinutes(-1), SimulationRunning: false, FillRunning: false, Refreshing: false, NowUtc: Now));
    }

    [Fact]
    public void A_blocking_gap_cannot_be_ignored_into_a_fill_but_a_gap_to_verify_can()
    {
        var draft = Draft((1, 0, 1m), (2, 0, 1m));
        var report = GapFx.Report(Closed(1, 0), Missing(2, 0));
        var analysis = Service.Analyze(draft, report, null);
        var session = new SessionGapState();

        foreach (var gap in analysis.Gaps)
            session.Ignore(gap);
        var status = StatusOf(analysis, session, report);

        Assert.Equal(1, session.IgnoredCount);
        Assert.False(status.CanFill);
        Assert.Equal("1 écart bloquant reste à régler.", status.FillDisabledReason);
        Assert.Equal(1, status.Blocking);
        Assert.Equal(0, status.ToVerify);
        Assert.Equal(1, status.Ignored);
        Assert.Equal(SubmissionGapType.TaskNotFound, GapBoard.Build(analysis.Gaps, session).FirstBlocking?.Type);
    }

    [Fact]
    public void Once_the_blocking_gap_is_settled_the_fill_opens_with_the_ignored_gap_still_ignored_and_named_in_the_dialog()
    {
        var draft = Draft((1, 0, 1m), (2, 0, 1m));
        var session = new SessionGapState();
        var before = Service.Analyze(draft, GapFx.Report(Closed(1, 0), Missing(2, 0)), null);
        session.Reconcile(before);
        session.Ignore(Assert.Single(before.Gaps, g => g.Type == SubmissionGapType.TaskClosed));

        var report = GapFx.Report(Closed(1, 0), GapFx.Cell(2, 0));
        var after = Service.Analyze(draft, report, null);
        session.Reconcile(after);
        var status = StatusOf(after, session, report);
        var board = GapBoard.Build(after.Gaps, session);
        var paragraphs = SubmitDialogs.FillParagraphs(2m, report.Cells.Count, GapFx.Monday, board.OpenToVerify, session.IgnoredCount, after.Summary);

        Assert.True(status.CanFill);
        Assert.Equal("Prêt à saisir", status.Verdict);
        Assert.Equal(1, status.Ignored);
        Assert.Contains(paragraphs, p => p.StartsWith("1 écart ignoré pour cette session"));
        Assert.DoesNotContain(paragraphs, p => p.Contains("à vérifier"));
        Assert.Equal("Tâche introuvable dans Forecast", Assert.Single(session.Resolved).Title);
    }

    [Fact]
    public void The_fill_dialog_lists_the_gaps_to_verify_still_open_and_not_the_ignored_ones()
    {
        var draft = Draft((1, 0, 1m), (2, 1, 1m));
        var report = GapFx.Report(
            Closed(1, 0), GapFx.Cell(2, 1) with { Findings = [new ForecastCellFinding(ForecastFindingKind.TaskBlocked, "Tâche bloquée")] });
        var analysis = Service.Analyze(draft, report, null);
        var session = new SessionGapState();
        var ignored = Assert.Single(analysis.Gaps, g => g.Type == SubmissionGapType.TaskClosed);
        var open = Assert.Single(analysis.Gaps, g => g.Type == SubmissionGapType.TaskBlocked);
        session.Ignore(ignored);

        var board = GapBoard.Build(analysis.Gaps, session);
        var paragraphs = SubmitDialogs.FillParagraphs(2m, 2, GapFx.Monday, board.OpenToVerify, session.IgnoredCount, analysis.Summary);

        var verify = Assert.Single(paragraphs, p => p.Contains("à vérifier"));
        Assert.Contains(open.Title, verify);
        Assert.DoesNotContain(ignored.Title, verify);
        Assert.Contains(paragraphs, p => p.StartsWith("1 écart ignoré"));
        Assert.True(StatusOf(analysis, session, report).CanFill);
    }

    [Fact]
    public void An_ignored_gap_comes_back_into_the_fill_dialog_when_its_situation_changes_and_is_not_ignored_again_when_it_returns()
    {
        var draft = Draft((1, 0, 8m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));
        SubmissionDraftContext Context(decimal unclassified) => new(GapFx.Monday, Enumerable.Range(0, 7).Select(i =>
            new SubmissionDayContext(GapFx.Day(i), i < 5 ? 8m : 0m, i == 0 ? unclassified : 0m)).ToList());
        var session = new SessionGapState();
        var first = Service.Analyze(draft, null, Context(1.5m));
        session.Reconcile(first);
        session.Ignore(Assert.Single(first.Gaps, g => g.Type == SubmissionGapType.UnclassifiedRemaining));
        Assert.Equal(0, GapBoard.Build(first.Gaps, session).OpenToVerifyCount);

        var changed = Service.Analyze(draft, null, Context(2m));
        session.Reconcile(changed);
        var board = GapBoard.Build(changed.Gaps, session);

        Assert.Equal(1, board.OpenToVerifyCount);
        Assert.Equal(0, session.IgnoredCount);
        var paragraphs = SubmitDialogs.FillParagraphs(40m, 5, GapFx.Monday, board.OpenToVerify, session.IgnoredCount, changed.Summary);
        Assert.Contains(paragraphs, p => p.Contains("1 point à vérifier reste ouvert"));
        Assert.DoesNotContain(paragraphs, p => p.Contains("ignoré"));

        // The old situation coming back is a new decision to make: the earlier choice is gone.
        var back = Service.Analyze(draft, null, Context(1.5m));
        session.Reconcile(back);
        Assert.Equal(1, GapBoard.Build(back.Gaps, session).OpenToVerifyCount);
    }

    [Fact]
    public void Ignoring_the_same_gap_twice_counts_once_and_the_ignored_gaps_follow_the_filter()
    {
        var verifyMonday = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 0);
        var verifyTuesday = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 1);
        var session = new SessionGapState();

        Assert.True(session.Ignore(verifyMonday));
        Assert.True(session.Ignore(verifyMonday));
        Assert.True(session.Ignore(verifyTuesday));
        var board = GapBoard.Build([verifyMonday, verifyTuesday], session, new GapFilter(Day: GapFx.Day(1)));

        Assert.Equal(2, session.IgnoredCount);
        Assert.Equal([verifyTuesday.Id], board.Ignored.Select(g => g.Id));
        Assert.Empty(board.Visible);
        Assert.Equal(0, board.OpenToVerifyCount);
    }

    [Fact]
    public void The_day_filter_does_not_hide_open_blocking_gaps_from_the_counts_that_guard_the_fill()
    {
        var blockingOnTuesday = GapFx.Gap(taskId: 1, day: 1);
        var verifyOnMonday = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 0);

        var board = GapBoard.Build([blockingOnTuesday, verifyOnMonday], new SessionGapState(), new GapFilter(Day: GapFx.Day(0)));

        Assert.Equal([verifyOnMonday.Id], board.Visible.Select(g => g.Id));
        Assert.Equal(1, board.OpenBlockingCount);
        Assert.Equal(blockingOnTuesday.Id, board.FirstBlocking?.Id);
    }

    [Fact]
    public void The_cells_of_the_grid_are_the_cells_the_fill_plans_whatever_stands_outside_the_week_or_at_zero()
    {
        var draft = Draft((1, 0, 1m), (1, 0, 0.5m), (2, 1, 2m), (1, 2, 0m), (3, 7, 4m), (4, -1, 1m));
        var tasks = Enumerable.Range(1, 4).Select(i => GapFx.Task(i, $"Tâche {i}")).ToDictionary(t => t.Id);

        var grid = DraftGrid.Build(draft, tasks, GapFx.Monday, null);
        var planned = ForecastFillPlanner.Plan(draft, tasks);

        var gridCells = grid.Rows.SelectMany(r => r.Hours.Where(h => h.Value > 0).Select(h => (r.TaskId, h.Key))).Order().ToList();
        Assert.Equal(planned.Select(c => (c.Task.Id, c.Date)).Order(), gridCells);
        Assert.Equal(planned.Sum(c => c.Hours), grid.GrandTotal);
        Assert.Equal(3.5m, grid.GrandTotal);
    }

    // ── The context the gap service gets from the week proposal ──────────────────────────────────

    private static SlotAssignment Slot(DateTime startUtc, SlotKind kind) => new(startUtc, kind, null, null, 1.0, [], [], TimeSpan.Zero);

    private static WeekProposal Proposal(
        DateOnly weekStart, Dictionary<DateOnly, IReadOnlyList<SlotAssignment>> slots, Dictionary<DateOnly, decimal>? unclassified = null) =>
        new(
            weekStart,
            Enumerable.Range(0, 7).Select(weekStart.AddDays).Select(d => new DayProposal(
                d, [], 0m, d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0m : 8m, unclassified?.GetValueOrDefault(d) ?? 0m, DayFlag.Ok)).ToList(),
            [],
            slots.ToDictionary(p => p.Key, p => p.Value),
            new Dictionary<int, TimeSheetHelper.Core.Forecast.ForecastTask>(),
            false);

    [Fact]
    public void Only_the_slots_marked_not_work_or_ignored_count_as_set_aside_and_deleted_ones_count_for_nothing()
    {
        var monday = GapFx.Monday;
        var start = new DateTime(2026, 10, 12, 13, 0, 0, DateTimeKind.Utc);
        var slots = new List<SlotAssignment>
        {
            Slot(start, SlotKind.ExcludedByOverride), Slot(start.AddMinutes(15), SlotKind.ExcludedByOverride), Slot(start.AddMinutes(30), SlotKind.ExcludedByOverride),
            Slot(start.AddMinutes(45), SlotKind.Ignored),
            Slot(start.AddHours(1), SlotKind.Removed), Slot(start.AddHours(1).AddMinutes(15), SlotKind.Removed),
            Slot(start.AddHours(2), SlotKind.Activity), Slot(start.AddHours(2).AddMinutes(15), SlotKind.Override), Slot(start.AddHours(2).AddMinutes(30), SlotKind.Empty),
        };

        var context = SubmissionReviewService.ContextOf(Proposal(
            monday, new() { [monday] = slots }, new() { [monday.AddDays(1)] = 1.25m }));

        Assert.Equal(monday, context.WeekStart);
        Assert.Equal(Enumerable.Range(0, 7).Select(monday.AddDays), context.Days.Select(d => d.Date));
        Assert.Equal(1.0m, context.Days[0].ExcludedHours);
        Assert.Equal(0m, context.Days[1].ExcludedHours);
        Assert.Equal(1.25m, context.Days[1].UnclassifiedHours);
        Assert.Equal(0m, context.Days[0].UnclassifiedHours);
        Assert.Equal([8m, 8m, 8m, 8m, 8m, 0m, 0m], context.Days.Select(d => d.TargetHours));
    }

    [Fact]
    public void On_the_day_the_clock_falls_back_the_two_slots_of_the_repeated_hour_are_two_different_slots()
    {
        var weekStart = new DateOnly(2026, 10, 26);
        var fallBack = new DateOnly(2026, 11, 1);
        // 01:15 EDT then 01:15 EST: the same wall-clock time, one hour apart.
        var slots = new List<SlotAssignment>
        {
            Slot(new DateTime(2026, 11, 1, 5, 15, 0, DateTimeKind.Utc), SlotKind.ExcludedByOverride),
            Slot(new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc), SlotKind.ExcludedByOverride),
        };

        var context = SubmissionReviewService.ContextOf(Proposal(weekStart, new() { [fallBack] = slots }));

        var sunday = Assert.Single(context.Days, d => d.Date == fallBack);
        Assert.Equal(0.5m, sunday.ExcludedHours);
        Assert.Equal(0m, sunday.TargetHours);
    }

    // ── Defects found by the review (expected behaviour, to enable once fixed) ───────────────────

    [Fact]
    public void Cells_whose_state_could_not_be_read_are_not_listed_as_without_gap()
    {
        var draft = Draft((1, 0, 1m), (2, 0, 1m));
        var report = GapFx.Report(
            GapFx.Cell(1, 0, ForecastCellStatus.Ok),
            GapFx.Cell(2, 0, ForecastCellStatus.Unknown) with { Issue = ForecastCellIssue.ReadIncomplete, EntryState = ForecastEntryState.Unknown });
        report.ReadTruncated = true;

        var analysis = Service.Analyze(draft, report, null);

        Assert.Equal([(1, GapFx.Day(0))], GapBoard.CellsWithoutGap(report, analysis.Gaps).Select(c => (c.ForecastTaskId, c.Date)));
        Assert.Equal(1, analysis.Summary.CellsWithoutGapCount);
    }

    [Fact]
    public void A_blocking_gap_never_gets_an_ignore_button_even_if_the_backend_lists_the_action_as_available()
    {
        var gap = GapFx.Gap(actions: [GapFx.Action(SubmissionGapResolution.IgnoreKnowingly)]);

        var view = Assert.Single(GapActionPlanner.Plan(gap, new GapActionContext(GapFx.Monday, _ => true)));

        Assert.Equal(GapActionKind.Disabled, view.Kind);
    }
}

public sealed class AnalysisSchedulingGadgetTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Tickets_taken_from_many_threads_leave_only_the_latest_one_current_and_cancel_all_the_others()
    {
        using var sequencer = new AnalysisSequencer();
        var tickets = new AnalysisSequencer.Ticket[300];

        await Task.WhenAll(Enumerable.Range(0, tickets.Length).Select(i => Task.Run(() => tickets[i] = sequencer.Begin())));

        var latest = tickets.MaxBy(t => t.Version);
        Assert.Equal(latest, Assert.Single(tickets, sequencer.IsCurrent));
        Assert.Equal(tickets.Length, tickets.Select(t => t.Version).Distinct().Count());
        Assert.All(tickets.Where(t => t.Version != latest.Version), t => Assert.True(t.Token.IsCancellationRequested));
        Assert.False(latest.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancelling_while_the_action_runs_cancels_its_token_and_nothing_stays_pending()
    {
        var started = new TaskCompletionSource<CancellationToken>();
        var finished = new TaskCompletionSource();
        using var debounced = new DebouncedAction(TimeSpan.FromMilliseconds(20), async token =>
        {
            started.TrySetResult(token);
            try
            {
                await Task.Delay(Patience, token);
            }
            finally
            {
                finished.TrySetResult();
            }
        });

        debounced.Request();
        var token = await started.Task.WaitAsync(Patience);
        debounced.Cancel();
        await finished.Task.WaitAsync(Patience);

        Assert.True(token.IsCancellationRequested);
        Assert.False(debounced.IsPending);
    }
}
