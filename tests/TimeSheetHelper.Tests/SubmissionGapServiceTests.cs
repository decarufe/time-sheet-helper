using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests;

public sealed class SubmissionGapServiceTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private readonly SubmissionGapService _sut = new();

    private static TimesheetDraft Draft(params (int Task, int Day, decimal Hours)[] cells) =>
        new() { WeekStart = Monday, Entries = [.. cells.Select(c => new TimesheetEntry { ForecastTaskId = c.Task, Date = Monday.AddDays(c.Day), Hours = c.Hours })] };

    /// <summary>A week at 8 h on every working day, with the given unclassified and excluded hours per day index.</summary>
    private static SubmissionDraftContext Context(
        IReadOnlyDictionary<int, decimal>? unclassified = null, IReadOnlyDictionary<int, decimal>? excluded = null, decimal target = 8m) =>
        new(Monday, Enumerable.Range(0, 7).Select(i => new SubmissionDayContext(
            Monday.AddDays(i),
            i < 5 ? target : 0m,
            unclassified?.GetValueOrDefault(i) ?? 0m,
            excluded?.GetValueOrDefault(i) ?? 0m)).ToList());

    private static ForecastFillReport Report(params ForecastCellResult[] cells)
    {
        var report = new ForecastFillReport { WeekStart = Monday, Mode = ForecastSubmitMode.DryRun };
        report.Cells.AddRange(cells);
        return report;
    }

    private static ForecastCellResult Cell(
        int task, int day, decimal expected, ForecastCellStatus status, decimal? actual = null, string? message = null) =>
        new(task, $"Acme / Tâche {task}", Monday.AddDays(day), expected, actual, status, message);

    private static TimesheetDraft FullWeek() =>
        Draft((1, 0, 8m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));

    private SubmissionAnalysis Analyze(TimesheetDraft draft, ForecastFillReport? report = null, SubmissionDraftContext? context = null) =>
        _sut.AnalyzeAsync(draft, report, context).GetAwaiter().GetResult();

    private static SubmissionGap Single(SubmissionAnalysis analysis, SubmissionGapType type) =>
        Assert.Single(analysis.Gaps, g => g.Type == type);

    // ── Local gaps ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hours_of_a_task_left_out_of_the_fill_still_count_in_the_day_and_week_totals_and_the_objective()
    {
        // Task 9 (flagged or ignored) has no cell in the report: the writer left it out. Its 2 h are worked, so no total drops them.
        var draft = Draft((1, 0, 6m), (9, 0, 2m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));
        var report = Report(
            Cell(1, 0, 6m, ForecastCellStatus.Planned), Cell(1, 1, 8m, ForecastCellStatus.Planned), Cell(1, 2, 8m, ForecastCellStatus.Planned),
            Cell(1, 3, 8m, ForecastCellStatus.Planned), Cell(1, 4, 8m, ForecastCellStatus.Planned));

        var analysis = Analyze(draft, report, Context());

        Assert.Equal(40m, analysis.Summary.WeekHours);
        Assert.Equal(8m, analysis.Summary.Days.Single(d => d.Date == Monday).DraftHours);
        Assert.True(analysis.Summary.Days.Single(d => d.Date == Monday).WithinTolerance);
        Assert.True(analysis.Summary.WeekWithinTolerance);
        Assert.DoesNotContain(analysis.Gaps, g => g.Type is SubmissionGapType.DayBelowTarget or SubmissionGapType.TaskNotFound);
    }

    [Fact]
    public void A_week_on_target_with_a_clean_simulation_has_no_gap_and_can_be_filled()
    {
        var report = Report(
            Cell(1, 0, 8m, ForecastCellStatus.Planned), Cell(1, 1, 8m, ForecastCellStatus.Planned), Cell(1, 2, 8m, ForecastCellStatus.Ok, 8m),
            Cell(1, 3, 8m, ForecastCellStatus.Planned), Cell(1, 4, 8m, ForecastCellStatus.Planned));

        var analysis = Analyze(FullWeek(), report, Context());

        Assert.Empty(analysis.Gaps);
        Assert.Equal(5, analysis.Summary.CellCount);
        Assert.Equal(5, analysis.Summary.CellsWithoutGapCount);
        Assert.True(analysis.Summary.CanFill);
        Assert.Equal(40m, analysis.Summary.WeekHours);
        Assert.Equal(40m, analysis.Summary.WeekTargetHours);
        Assert.True(analysis.Summary.WeekWithinTolerance);
    }

    [Fact]
    public void Unclassified_work_left_is_reported_per_day_and_does_not_block()
    {
        var analysis = Analyze(FullWeek(), Report(Cell(1, 0, 8m, ForecastCellStatus.Planned)), Context(new Dictionary<int, decimal> { [1] = 1.5m, [3] = 0.25m }));

        var gaps = analysis.Gaps.Where(g => g.Type == SubmissionGapType.UnclassifiedRemaining).ToList();
        Assert.Equal(2, gaps.Count);
        Assert.All(gaps, g => Assert.Equal(SubmissionGapSeverity.ToVerify, g.Severity));
        Assert.Equal(Monday.AddDays(1), gaps[0].Date);
        Assert.Contains("1,5 h", gaps[0].Message);
        Assert.Equal(SubmissionGapResolution.ReclassifySlots, gaps[0].Actions.Single(a => a.IsRecommended).Resolution);
        Assert.Equal(1.75m, analysis.Summary.UnclassifiedHours);
        Assert.True(analysis.Summary.CanFill);

        // Preview: the day and the week once the work is classified.
        var preview = Assert.IsType<SubmissionGapPreview>(gaps[0].Preview);
        Assert.Equal((8m, 9.5m, 40m, 41.5m), (preview.DayBefore, preview.DayAfter, preview.WeekBefore, preview.WeekAfter));
    }

    [Fact]
    public void A_day_below_the_target_is_to_verify_and_a_day_above_it_is_info()
    {
        var draft = Draft((1, 0, 6m), (1, 1, 9.5m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));

        var analysis = Analyze(draft, null, Context());

        var below = Single(analysis, SubmissionGapType.DayBelowTarget);
        Assert.Equal(SubmissionGapSeverity.ToVerify, below.Severity);
        Assert.Equal(Monday, below.Date);
        Assert.Equal(-2m, below.DeltaHours);
        var above = Single(analysis, SubmissionGapType.DayAboveTarget);
        Assert.Equal(SubmissionGapSeverity.Info, above.Severity);
        Assert.Equal(1.5m, above.DeltaHours);
    }

    [Theory]
    [InlineData(7.75, false)]
    [InlineData(8.25, false)]
    [InlineData(7.5, true)]
    [InlineData(8.5, true)]
    public void The_objective_tolerance_is_a_quarter_hour(double hours, bool isGap)
    {
        var draft = Draft((1, 0, (decimal)hours), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));

        var analysis = Analyze(draft, null, Context());

        Assert.Equal(isGap, analysis.Gaps.Any(g => g.Date == Monday && g.Type is SubmissionGapType.DayBelowTarget or SubmissionGapType.DayAboveTarget));
    }

    [Fact]
    public void The_week_total_is_judged_against_the_objective_with_the_same_tolerance()
    {
        var draft = Draft((1, 0, 8m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 7.75m));

        var within = Analyze(draft, null, Context()).Summary;
        var outside = Analyze(Draft((1, 0, 8m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 7.5m)), null, Context()).Summary;

        Assert.True(within.WeekWithinTolerance);
        Assert.Equal(-0.25m, within.WeekDeltaHours);
        Assert.False(outside.WeekWithinTolerance);
        Assert.Equal(0.25m, within.ToleranceHours);
    }

    [Fact]
    public void A_weekend_with_hours_is_a_day_without_target_and_an_empty_one_is_nothing()
    {
        var draft = Draft((1, 0, 8m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m), (1, 5, 2m));

        var gap = Single(Analyze(draft, null, Context()), SubmissionGapType.DayAboveTarget);

        Assert.Equal(Monday.AddDays(5), gap.Date);
        Assert.Contains("sans objectif", gap.Message);
        Assert.DoesNotContain(Analyze(FullWeek(), null, Context()).Gaps, g => g.Date == Monday.AddDays(6));
    }

    [Fact]
    public void Excluded_hours_are_summed_in_the_summary_and_never_a_gap()
    {
        var analysis = Analyze(FullWeek(), null, Context(excluded: new Dictionary<int, decimal> { [0] = 1m, [2] = 0.5m }));

        Assert.Equal(1.5m, analysis.Summary.ExcludedHours);
        Assert.Empty(analysis.Gaps);
    }

    [Fact]
    public void Without_a_context_there_is_no_objective_check()
    {
        var analysis = Analyze(Draft((1, 0, 1m)));

        Assert.Empty(analysis.Gaps);
        Assert.Null(analysis.Summary.WeekTargetHours);
        Assert.Null(analysis.Summary.WeekWithinTolerance);
    }

    [Fact]
    public void Without_a_simulation_nothing_can_be_filled_yet()
    {
        var analysis = Analyze(FullWeek(), null, Context());

        Assert.False(analysis.Summary.HasSimulation);
        Assert.False(analysis.Summary.CanFill);
        Assert.Equal(0, analysis.Summary.CellCount);
    }

    // ── Gaps of the simulation ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_task_not_found_is_blocking_with_the_resolutions_that_apply()
    {
        var report = Report(Cell(1, 0, 8m, ForecastCellStatus.RowNotFound, message: "introuvable"));

        var analysis = Analyze(FullWeek(), report, Context());

        var gap = Single(analysis, SubmissionGapType.TaskNotFound);
        Assert.Equal(SubmissionGapSeverity.Blocking, gap.Severity);
        Assert.Equal("introuvable", gap.Message);
        Assert.Equal(new SubmissionGapCell(1, "Acme / Tâche 1", Monday), gap.Cell);
        var resolutions = gap.Actions.Select(a => a.Resolution).ToList();
        Assert.Contains(SubmissionGapResolution.LinkTask, resolutions);
        Assert.Contains(SubmissionGapResolution.CreateTask, resolutions);
        Assert.Contains(SubmissionGapResolution.RefreshTasks, resolutions);
        Assert.Contains(SubmissionGapResolution.MarkNotWork, resolutions);
        Assert.Equal(SubmissionGapResolution.LinkTask, gap.Actions.Single(a => a.IsRecommended).Resolution);
        Assert.False(analysis.Summary.CanFill);
        Assert.Equal(1, analysis.Summary.BlockingCount);

        // Not available before the persisted adjustments: said so, with a reason.
        var ignore = gap.Actions.Single(a => a.Resolution == SubmissionGapResolution.IgnoreKnowingly);
        Assert.False(ignore.IsAvailable);
        Assert.NotNull(ignore.UnavailableReason);
        Assert.Null(gap.AdjustedHours);
        Assert.Equal(8m, gap.Preview!.CellBefore);
        Assert.Equal(0m, gap.Preview.CellAfter);
        Assert.Equal(32m, gap.Preview.WeekAfter);
    }

    [Fact]
    public void An_ambiguous_task_is_not_reported_as_missing()
    {
        var cell = Cell(1, 0, 8m, ForecastCellStatus.RowNotFound) with { Issue = ForecastCellIssue.TaskAmbiguous };

        var analysis = Analyze(FullWeek(), Report(cell));

        Assert.Equal(SubmissionGapSeverity.Blocking, Single(analysis, SubmissionGapType.TaskAmbiguous).Severity);
        Assert.DoesNotContain(analysis.Gaps, g => g.Type == SubmissionGapType.TaskNotFound);
    }

    [Fact]
    public void A_task_unknown_locally_is_a_blocking_gap_that_the_draft_rebuild_can_fix()
    {
        var cell = Cell(9999, 2, 1.5m, ForecastCellStatus.Error, message: "inconnue") with { Issue = ForecastCellIssue.TaskUnknownLocally };

        var gap = Single(Analyze(FullWeek(), Report(cell)), SubmissionGapType.TaskUnknownLocally);

        Assert.Equal(SubmissionGapSeverity.Blocking, gap.Severity);
        Assert.Equal(SubmissionGapResolution.RecalculateDraft, gap.Actions.Single(a => a.IsRecommended).Resolution);
    }

    [Fact]
    public void Forecast_higher_than_expected_is_blocking_with_the_direction_the_delta_and_an_aligned_preview()
    {
        var cell = Cell(1, 1, 8m, ForecastCellStatus.Mismatch, 10m, "écart") with { Comparison = ForecastComparisonLevel.ProjectAndDay };

        var analysis = Analyze(FullWeek(), Report(cell), Context());

        var gap = Single(analysis, SubmissionGapType.ForecastHigher);
        Assert.Equal(SubmissionGapSeverity.Blocking, gap.Severity);
        Assert.Equal((8m, 10m, 2m), (gap.ExpectedHours, gap.ForecastHours, gap.DeltaHours));
        Assert.True(gap.IsApproximate);
        Assert.Contains("approximative", gap.Message);
        Assert.Contains("de plus", gap.Message);
        Assert.Equal(SubmissionGapResolution.AlignDraftOnForecast, gap.Actions.Single(a => a.IsRecommended).Resolution);
        Assert.False(gap.Actions.Single(a => a.IsRecommended).IsAvailable);
        Assert.Equal((8m, 10m, 8m, 10m, 40m, 42m), (gap.Preview!.CellBefore, gap.Preview.CellAfter, gap.Preview.DayBefore, gap.Preview.DayAfter, gap.Preview.WeekBefore, gap.Preview.WeekAfter));
    }

    [Fact]
    public void Forecast_lower_than_expected_offers_the_complement_and_leaves_the_draft_totals_alone()
    {
        var entry = new ForecastRemoteEntry("1", null, Monday, 2m, "9", "740", "77", "Déjà là", "NOT_SUBMITTED");
        var cell = Cell(1, 0, 8m, ForecastCellStatus.Mismatch, 2m) with { Comparison = ForecastComparisonLevel.TaskAndDay, ExistingEntries = [entry] };

        var gap = Single(Analyze(FullWeek(), Report(cell), Context()), SubmissionGapType.ForecastLower);

        Assert.Equal(-6m, gap.DeltaHours);
        Assert.False(gap.IsApproximate);
        Assert.Contains("de moins", gap.Message);
        Assert.Equal(SubmissionGapResolution.AddComplement, gap.Actions.Single(a => a.IsRecommended).Resolution);
        Assert.Equal("Déjà là", Assert.Single(gap.ExistingEntries).Notes);
        Assert.Equal((2m, 8m, 8m, 8m, 40m, 40m), (gap.Preview!.CellBefore, gap.Preview.CellAfter, gap.Preview.DayBefore, gap.Preview.DayAfter, gap.Preview.WeekBefore, gap.Preview.WeekAfter));
    }

    [Fact]
    public void Cells_of_one_project_and_day_in_conflict_make_one_gap_with_the_related_cells()
    {
        var a = Cell(1, 0, 5m, ForecastCellStatus.Mismatch, 1m) with { GroupKey = "740|20261005", GroupExpected = 8m };
        var b = Cell(2, 0, 3m, ForecastCellStatus.Mismatch, 1m) with { GroupKey = "740|20261005", GroupExpected = 8m };

        var analysis = Analyze(FullWeek(), Report(a, b));

        var gap = Single(analysis, SubmissionGapType.ForecastLower);
        Assert.Equal(8m, gap.ExpectedHours);
        Assert.Equal(2, new[] { gap.Cell! }.Concat(gap.RelatedCells).Count());
        Assert.Equal(1, analysis.Summary.BlockingCount);
    }

    [Fact]
    public void An_incomplete_read_is_one_blocking_gap_not_one_per_cell()
    {
        var report = Report(
            Cell(1, 0, 8m, ForecastCellStatus.Unknown) with { Issue = ForecastCellIssue.ReadIncomplete },
            Cell(1, 1, 8m, ForecastCellStatus.Unknown) with { Issue = ForecastCellIssue.ReadIncomplete });
        report.ReadTruncated = true;
        report.ReadDetail = "14 lues sur 230";

        var analysis = Analyze(FullWeek(), report);

        var gap = Single(analysis, SubmissionGapType.ReadTruncated);
        Assert.Contains("14 lues sur 230", gap.Message);
        Assert.Single(analysis.Gaps);
        Assert.False(analysis.Summary.ReadComplete);
        Assert.False(analysis.Summary.CanFill);
    }

    [Fact]
    public void A_global_forecast_error_is_one_gap_and_a_single_cell_error_is_its_own()
    {
        var report = Report(
            Cell(1, 0, 8m, ForecastCellStatus.Error, message: "panne"),
            Cell(1, 1, 8m, ForecastCellStatus.Error, message: "panne"));
        report.GlobalError = "panne";
        var single = Report(Cell(1, 0, 8m, ForecastCellStatus.Error, message: "refus") with { Issue = ForecastCellIssue.WriteRefused });

        var global = Analyze(FullWeek(), report);
        var local = Analyze(FullWeek(), single);

        Assert.Equal(SubmissionGapType.ForecastError, Assert.Single(global.Gaps).Type);
        var cellGap = Assert.Single(local.Gaps);
        Assert.Equal(SubmissionGapType.ForecastError, cellGap.Type);
        Assert.NotNull(cellGap.Cell);
        Assert.Equal("Saisie refusée par Forecast", cellGap.Title);
    }

    [Fact]
    public void An_interrupted_fill_is_one_blocking_gap_and_the_cells_not_tried_are_not_repeated()
    {
        var report = Report(
            Cell(1, 0, 8m, ForecastCellStatus.Ok, 8m) with { EntryState = ForecastEntryState.Entered },
            Cell(1, 1, 8m, ForecastCellStatus.Unknown) with { Issue = ForecastCellIssue.OutcomeUnknown, EntryState = ForecastEntryState.Unknown },
            Cell(1, 2, 8m, ForecastCellStatus.Error) with { Issue = ForecastCellIssue.NotAttempted });
        report.Interrupted = true;
        report.InterruptionMessage = "délai";

        var analysis = Analyze(FullWeek(), report);

        var interrupted = Single(analysis, SubmissionGapType.FillInterrupted);
        Assert.Contains("1/3", interrupted.Message);
        Assert.Equal(SubmissionGapResolution.ReadAgain, interrupted.Actions.Single(a => a.IsRecommended).Resolution);
        Assert.Equal(SubmissionGapType.CellEntryUnknown, Single(analysis, SubmissionGapType.CellEntryUnknown).Type);
        Assert.Equal(2, analysis.Gaps.Count);
    }

    [Fact]
    public void The_findings_of_the_checks_are_to_verify_gaps_that_do_not_block()
    {
        var cell = Cell(1, 0, 8m, ForecastCellStatus.Planned) with
        {
            Findings =
            [
                new ForecastCellFinding(ForecastFindingKind.TaskClosed, "terminée"),
                new ForecastCellFinding(ForecastFindingKind.PlanWarning, "semaine approuvée"),
                new ForecastCellFinding(ForecastFindingKind.EstimateExceeded, "dépassée"),
            ],
        };

        var analysis = Analyze(FullWeek(), Report(cell));

        Assert.All(analysis.Gaps, g => Assert.Equal(SubmissionGapSeverity.ToVerify, g.Severity));
        Assert.Equal(
            [SubmissionGapType.TaskClosed, SubmissionGapType.EstimateExceeded, SubmissionGapType.ForecastWarning],
            analysis.Gaps.Select(g => g.Type).OrderBy(t => t == SubmissionGapType.TaskClosed ? 0 : t == SubmissionGapType.EstimateExceeded ? 1 : 2));
        Assert.True(analysis.Summary.CanFill);
        Assert.Equal(0, analysis.Summary.CellsWithoutGapCount);
    }

    [Fact]
    public void What_the_checks_could_not_verify_is_an_info_gap()
    {
        var report = Report(Cell(1, 0, 8m, ForecastCellStatus.Planned));
        report.PreflightLimits.Add("Outil « list_project_tasks » absent.");

        var gap = Single(Analyze(FullWeek(), report), SubmissionGapType.PreflightUnavailable);

        Assert.Equal(SubmissionGapSeverity.Info, gap.Severity);
        Assert.Contains("list_project_tasks", gap.Message);
    }

    // ── Counters, order, identity ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_summary_counts_each_severity_and_the_cells_without_a_gap()
    {
        var report = Report(
            Cell(1, 0, 8m, ForecastCellStatus.Planned),
            Cell(1, 1, 8m, ForecastCellStatus.Planned),
            Cell(1, 2, 8m, ForecastCellStatus.RowNotFound),
            Cell(1, 3, 8m, ForecastCellStatus.Mismatch, 3m));
        var draft = Draft((1, 0, 8m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 9m));

        var analysis = Analyze(draft, report, Context(new Dictionary<int, decimal> { [4] = 0.5m }));

        var summary = analysis.Summary;
        Assert.Equal(2, summary.BlockingCount);              // task not found + Forecast lower
        Assert.Equal(1, summary.ToVerifyCount);              // unclassified Friday
        Assert.Equal(1, summary.InfoCount);                  // Friday above target (9 h)
        Assert.Equal(4, summary.CellCount);
        Assert.Equal(2, summary.CellsWithoutGapCount);
        Assert.False(summary.CanFill);
    }

    [Fact]
    public void Gaps_are_ordered_blocking_first_then_by_day_and_their_ids_are_stable_and_unique()
    {
        var report = Report(
            Cell(1, 3, 8m, ForecastCellStatus.RowNotFound),
            Cell(1, 1, 8m, ForecastCellStatus.RowNotFound));
        var draft = Draft((1, 0, 6m), (1, 1, 8m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));

        var first = Analyze(draft, report, Context());
        var second = Analyze(draft, report, Context());

        Assert.Equal(first.Gaps.Select(g => g.Id), second.Gaps.Select(g => g.Id));
        Assert.Equal(first.Gaps.Count, first.Gaps.Select(g => g.Id).Distinct().Count());
        Assert.Equal(
            [SubmissionGapSeverity.Blocking, SubmissionGapSeverity.Blocking, SubmissionGapSeverity.ToVerify],
            first.Gaps.Select(g => g.Severity));
        Assert.Equal([Monday.AddDays(1), Monday.AddDays(3), Monday], first.Gaps.Select(g => g.Date!.Value));
        Assert.Equal("TaskNotFound|1|20261006", first.Gaps[0].Id);
    }

    // ── Failures that produce no report ───────────────────────────────────────────────────────────

    [Fact]
    public void Not_being_signed_in_is_a_blocking_gap_with_the_sign_in_resolution()
    {
        var gap = _sut.CreateFailureGap(new ForecastAuthRequiredException(), Monday);

        Assert.Equal(SubmissionGapType.AuthRequired, gap.Type);
        Assert.Equal(SubmissionGapSeverity.Blocking, gap.Severity);
        Assert.Equal(SubmissionGapResolution.SignIn, gap.Actions.Single(a => a.IsRecommended).Resolution);
    }

    [Fact]
    public void Any_other_failure_is_a_blocking_forecast_error_with_retry()
    {
        var gap = _sut.CreateFailureGap(new HttpRequestException("réseau coupé"), Monday);

        Assert.Equal(SubmissionGapType.ForecastError, gap.Type);
        Assert.Contains("réseau coupé", gap.Message);
        Assert.Equal(SubmissionGapResolution.Retry, gap.Actions.Single(a => a.IsRecommended).Resolution);
    }

    // ── Summary by day, time without a project, the other findings ───────────────────────────────

    [Fact]
    public void The_summary_has_one_row_per_day_with_its_objective_and_the_tolerance()
    {
        var draft = Draft((1, 0, 6m), (1, 1, 8.25m), (1, 2, 8m), (1, 3, 8m), (1, 4, 8m));

        var days = Analyze(draft, null, Context(new Dictionary<int, decimal> { [0] = 2m }, new Dictionary<int, decimal> { [4] = 1m })).Summary.Days;

        Assert.Equal(7, days.Count);
        Assert.Equal(Monday, days[0].Date);
        Assert.Equal((6m, 8m, -2m, false, 2m), (days[0].DraftHours, days[0].TargetHours, days[0].DeltaHours, days[0].WithinTolerance, days[0].UnclassifiedHours));
        Assert.True(days[1].WithinTolerance);
        Assert.Equal(1m, days[4].ExcludedHours);
        Assert.Equal((0m, 0m, true), (days[5].DraftHours, days[5].TargetHours, days[5].WithinTolerance));
    }

    [Fact]
    public void Without_a_context_the_summary_has_no_day_rows()
    {
        Assert.Empty(Analyze(FullWeek()).Summary.Days);
    }

    [Fact]
    public void Time_without_a_project_in_forecast_is_an_info_gap_left_out_of_the_submission()
    {
        var report = Report(Cell(1, 0, 8m, ForecastCellStatus.Planned));
        report.NonProjectEntries.Add(new ForecastRemoteEntry(null, null, Monday.AddDays(2), 1m, "9", null, "54897446", "Sport", "APPROVED", false, "Non-project time"));
        report.NonProjectEntries.Add(new ForecastRemoteEntry(null, null, Monday.AddDays(4), 1m, "9", null, "54897449", "Sport", "APPROVED", false, "Non-project time"));

        var analysis = Analyze(FullWeek(), report, Context());

        var gap = Single(analysis, SubmissionGapType.ForecastTimeWithoutProject);
        Assert.Equal(SubmissionGapSeverity.Info, gap.Severity);
        Assert.Equal(2m, gap.ForecastHours);
        Assert.Equal(2, gap.ExistingEntries.Count);
        Assert.Contains("exclues de la soumission", gap.Message);
        Assert.True(analysis.Summary.CanFill);
        Assert.Equal(2m, analysis.Summary.ForecastNonProjectHours);
        Assert.Equal(1m, analysis.Summary.Days[2].ForecastNonProjectHours);
        Assert.Equal(0m, analysis.Summary.Days[1].ForecastNonProjectHours);
    }

    [Theory]
    [InlineData(ForecastFindingKind.TaskBlocked, SubmissionGapType.TaskBlocked, SubmissionGapResolution.OpenInForecast)]
    [InlineData(ForecastFindingKind.ProjectClosed, SubmissionGapType.ProjectClosed, SubmissionGapResolution.OpenInForecast)]
    [InlineData(ForecastFindingKind.TaskInactive, SubmissionGapType.TaskInactive, SubmissionGapResolution.ReactivateTask)]
    [InlineData(ForecastFindingKind.PendingApproval, SubmissionGapType.CellPendingApproval, SubmissionGapResolution.OpenInForecast)]
    public void Each_kind_of_finding_becomes_a_to_verify_gap_with_its_own_recommended_resolution(
        ForecastFindingKind kind, SubmissionGapType type, SubmissionGapResolution recommended)
    {
        var cell = Cell(1, 0, 8m, ForecastCellStatus.Planned) with { Findings = [new ForecastCellFinding(kind, "constat")] };

        var analysis = Analyze(FullWeek(), Report(cell));

        var gap = Single(analysis, type);
        Assert.Equal(SubmissionGapSeverity.ToVerify, gap.Severity);
        Assert.Equal("constat", gap.Message);
        Assert.Equal(recommended, gap.Actions.Single(a => a.IsRecommended).Resolution);
        Assert.True(analysis.Summary.CanFill);
    }

    [Fact]
    public void A_conflict_on_registrations_that_are_approved_keeps_a_finding()
    {
        var locked = new ForecastRemoteEntry("1", null, Monday, 2m, "9", "740", "77", null, "APPROVED");
        var cell = Cell(1, 0, 8m, ForecastCellStatus.Mismatch, 2m) with
        {
            ExistingEntries = [locked],
            Findings = [new ForecastCellFinding(ForecastFindingKind.TaskClosed, "terminée")],
        };

        var analysis = Analyze(FullWeek(), Report(cell));

        Single(analysis, SubmissionGapType.ForecastLower);
        Assert.Single(analysis.Gaps, g => g.Type == SubmissionGapType.TaskClosed);
        Assert.Equal(1, analysis.Summary.BlockingCount);
        Assert.Equal(1, analysis.Summary.ToVerifyCount);
    }

    [Fact]
    public void A_cell_whose_outcome_is_unknown_blocks_and_never_counts_as_without_a_gap()
    {
        var cell = Cell(1, 0, 8m, ForecastCellStatus.Unknown, 3m) with { Issue = ForecastCellIssue.OutcomeUnknown, EntryState = ForecastEntryState.Unknown };

        var analysis = Analyze(FullWeek(), Report(cell));

        var gap = Single(analysis, SubmissionGapType.CellEntryUnknown);
        Assert.Equal(SubmissionGapSeverity.Blocking, gap.Severity);
        Assert.Equal(3m, gap.ForecastHours);
        Assert.Equal(SubmissionGapResolution.ReadAgain, gap.Actions.Single(a => a.IsRecommended).Resolution);
        Assert.Equal(0, analysis.Summary.CellsWithoutGapCount);
        Assert.False(analysis.Summary.CanFill);
    }

    [Fact]
    public void A_cell_error_identical_to_the_global_error_is_not_repeated()
    {
        var report = Report(Cell(1, 0, 8m, ForecastCellStatus.Error, message: "panne"));
        report.GlobalError = "panne";

        var analysis = Analyze(FullWeek(), report);

        Assert.Equal(SubmissionGapType.ForecastError, Assert.Single(analysis.Gaps).Type);
        Assert.Null(analysis.Gaps[0].Cell);
    }

    [Fact]
    public async Task An_analysis_asked_to_stop_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.AnalyzeAsync(FullWeek(), null, null, cancelled.Token));
    }

    [Fact]
    public void The_service_is_registered_with_the_forecast_services()
    {
        var services = new ServiceCollection();
        services.AddForecast(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISubmissionGapService));

        Assert.Equal(typeof(SubmissionGapService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
