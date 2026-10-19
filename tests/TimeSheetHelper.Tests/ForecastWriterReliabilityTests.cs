using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

/// <summary>The simulation and the fill facing a slow, capped, throttled, locked or failing Forecast.</summary>
public sealed class ForecastWriterReliabilityTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeForecastClient _server = new();
    private readonly FakeForecastConnector _connector;
    private readonly ForecastTask _dev = new() { ExternalId = "T3382", ProjectName = "Portail", ClientName = "Acme", TaskName = "Développement" };
    private readonly ForecastTask _review = new() { ExternalId = "T3383", ProjectName = "Portail", TaskName = "Revue" };
    private readonly ForecastTask _meetings = new() { ExternalId = "T200", ProjectName = "Interne", TaskName = "Réunions" };

    public ForecastWriterReliabilityTests()
    {
        _connector = new FakeForecastConnector(_server);
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
        db.ForecastTasks.AddRange(_dev, _review, _meetings);
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private ForecastTimesheetWriter Writer(ForecastOptions? options = null) =>
        new(new ForecastApi(_connector, Options.Create(options ?? new ForecastOptions { RetryBaseDelay = TimeSpan.Zero }), NullLogger<ForecastApi>.Instance),
            new Factory(CreateDb), NullLogger<ForecastTimesheetWriter>.Instance);

    private static TimesheetEntry Entry(ForecastTask task, int day, decimal hours, string? notes = null) =>
        new() { ForecastTaskId = task.Id, Date = Monday.AddDays(day), Hours = hours, Notes = notes };

    private static TimesheetDraft Draft(params TimesheetEntry[] entries) => new() { WeekStart = Monday, Entries = [.. entries] };

    private static ForecastCellResult Cell(ForecastFillReport report, ForecastTask task, int day) =>
        report.Cells.Single(c => c.ForecastTaskId == task.Id && c.Date == Monday.AddDays(day));

    private int ReadCalls => _server.Calls.Count(c => c.Tool == "get_time_analysis");

    // ── E17: a capped read ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_capped_week_is_read_again_day_by_day_so_no_registration_is_missed()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));
        _server.Entries.Add((FakeForecastClient.MeetingsProject, Monday, 60, FakeForecastClient.MyId));
        _server.Entries.Add((FakeForecastClient.DevProject, Monday.AddDays(1), 60, FakeForecastClient.MyId));
        _server.PageSize = 2;

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_meetings, 0, 1m), Entry(_dev, 1, 1m)), ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        Assert.False(report.ReadTruncated);
        Assert.Empty(_server.Creates);
        Assert.True(ReadCalls > 1, "la semaine doit être relue jour par jour");
        Assert.All(report.Cells, c => Assert.Equal(ForecastCellStatus.Ok, c.Status));
    }

    [Theory]
    [InlineData(ForecastSubmitMode.DryRun)]
    [InlineData(ForecastSubmitMode.Fill)]
    public async Task A_read_that_stays_capped_refuses_the_simulation_and_registers_nothing(ForecastSubmitMode mode)
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));
        _server.AlwaysHasMore = true;

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 2m), Entry(_meetings, 1, 1m)), mode);

        Assert.True(report.ReadTruncated);
        Assert.False(report.Success);
        Assert.All(report.Cells, c =>
        {
            Assert.Equal(ForecastCellStatus.Unknown, c.Status);
            Assert.Equal(ForecastCellIssue.ReadIncomplete, c.Issue);
        });
        Assert.Empty(_server.Creates);
        Assert.Contains("incomplète", report.Summary);
    }

    // ── E32: a timeout in the middle of the fill ───────────────────────────────────────────────────

    [Fact]
    public async Task A_timeout_in_the_middle_of_the_fill_still_produces_a_report_with_a_state_per_cell_and_the_fill_can_resume()
    {
        var confirms = 0;
        _server.Intercept = (tool, _) =>
        {
            if (tool == "confirm_time_registration_create" && ++confirms == 2)
                throw new OperationCanceledException("délai du client dépassé"); // the client's own timeout, not the caller's
            return null;
        };
        var draft = Draft(Entry(_dev, 0, 1m), Entry(_dev, 1, 1m), Entry(_meetings, 0, 1m));

        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.True(report.Interrupted);
        Assert.False(report.Success);
        Assert.Equal(3, report.Cells.Count);
        var entered = report.Cells.Where(c => c.EntryState == ForecastEntryState.Entered).ToList();
        Assert.Single(entered);
        var unknown = report.Cells.Single(c => c.Status == ForecastCellStatus.Unknown);
        Assert.Equal(ForecastEntryState.Unknown, unknown.EntryState);
        Assert.Equal(ForecastCellIssue.OutcomeUnknown, unknown.Issue);
        var skipped = report.Cells.Single(c => c.Issue == ForecastCellIssue.NotAttempted);
        Assert.Equal(ForecastEntryState.NotEntered, skipped.EntryState);
        Assert.True(report.IsPartial);
        Assert.Single(_server.Entries);

        // Resume: what is in Forecast is recognised, only the rest is registered.
        _server.Intercept = null;
        var resumed = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.True(resumed.Success, resumed.Summary);
        Assert.Equal(3, _server.Entries.Count);
        Assert.Equal(3, _server.Entries.Sum(e => e.Minutes) / 60);
    }

    [Fact]
    public async Task A_timeout_while_reading_the_week_is_a_report_not_an_exception()
    {
        _server.Intercept = (tool, _) => tool == "get_time_analysis" ? throw new OperationCanceledException("délai") : null;

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.NotNull(report.GlobalError);
        Assert.Contains("délai", report.GlobalError);
        Assert.Equal(ForecastCellStatus.Error, Assert.Single(report.Cells).Status);
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_in_the_middle_of_the_fill_keeps_what_was_done_in_the_report()
    {
        using var cts = new CancellationTokenSource();
        var confirms = 0;
        _server.Intercept = (tool, _) =>
        {
            if (tool == "confirm_time_registration_create" && ++confirms == 2)
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            return null;
        };

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 1m), Entry(_dev, 1, 1m), Entry(_meetings, 0, 1m)), ForecastSubmitMode.Fill, cancellationToken: cts.Token);

        Assert.True(report.Interrupted);
        Assert.Equal(3, report.Cells.Count);
        Assert.All(report.Cells, c => Assert.NotEqual(ForecastCellStatus.Planned, c.Status));
        // Whatever the server applied is read back and reported, cancellation or not.
        Assert.Equal(_server.Entries.Count, report.Cells.Count(c => c.EntryState == ForecastEntryState.Entered));
    }

    // ── E33–E36: answers of the plan and confirm tools ─────────────────────────────────────────────

    [Fact]
    public async Task The_warnings_of_the_plan_step_are_reported_on_the_cell_and_in_the_report()
    {
        _server.PlanWarnings.Add("La semaine du 5 octobre est approuvée.");

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Ok, cell.Status);
        var finding = Assert.Single(cell.Findings, f => f.Kind == ForecastFindingKind.PlanWarning);
        Assert.Contains("approuvée", finding.Message);
        Assert.Contains(report.Warnings, w => w.Contains("approuvée") && w.Contains("2026-10-05"));
    }

    [Fact]
    public async Task A_confirmation_refused_without_an_error_flag_is_an_error_on_the_cell()
    {
        _server.ConfirmReply = ("""{"ok":false,"outcome":"refused","message":"semaine approuvée"}""", false);

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Error, cell.Status);
        Assert.Equal(ForecastCellIssue.WriteRefused, cell.Issue);
        Assert.Equal(ForecastEntryState.NotEntered, cell.EntryState);
        Assert.Contains("semaine approuvée", cell.Message);
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task A_failed_confirmation_is_an_error_on_the_cell()
    {
        _server.ConfirmReply = ("""{"ok":false,"outcome":"failed","message":"boum"}""", false);

        var cell = Assert.Single((await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill)).Cells);

        Assert.Equal(ForecastCellIssue.WriteFailed, cell.Issue);
        Assert.Contains("boum", cell.Message);
    }

    [Fact]
    public async Task An_unknown_outcome_is_checked_by_reading_the_week_before_concluding_applied()
    {
        _server.ConfirmReply = ("""{"ok":false,"outcome":"unknown","message":"délai"}""", true);

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Ok, cell.Status);
        Assert.Equal(ForecastEntryState.Entered, cell.EntryState);
        Assert.Contains("confirmé par la relecture", cell.Message);
        Assert.Single(_server.Entries);
    }

    [Fact]
    public async Task An_unknown_outcome_with_nothing_found_on_reading_stays_unknown_and_is_never_called_a_success()
    {
        _server.ConfirmReply = ("""{"ok":false,"outcome":"unknown","message":"délai"}""", false);

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Unknown, cell.Status);
        Assert.Equal(ForecastEntryState.Unknown, cell.EntryState);
        Assert.Contains("risque de doublon", cell.Message);
        Assert.False(report.Success);
    }

    [Fact]
    public async Task A_throttled_read_is_retried_with_a_delay_then_succeeds()
    {
        var throttled = 0;
        _server.Intercept = (tool, _) => tool == "get_time_analysis" && throttled++ < 2
            ? new ForecastToolResult(true, "429 Too Many Requests", null)
            : null;

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.True(report.Success, report.Summary);
        Assert.Null(report.GlobalError);
        Assert.Equal(3, ReadCalls);
    }

    [Fact]
    public async Task A_read_throttled_beyond_the_retries_is_reported_as_a_global_error()
    {
        _server.Intercept = (tool, _) => tool == "get_time_analysis" ? new ForecastToolResult(true, "rate limit exceeded", null) : null;

        var report = await Writer(new ForecastOptions { RetryBaseDelay = TimeSpan.Zero, RetryCount = 2 })
            .FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.NotNull(report.GlobalError);
        Assert.Equal(3, ReadCalls); // the first try and two retries
        Assert.Equal(ForecastCellStatus.Error, Assert.Single(report.Cells).Status);
    }

    [Fact]
    public async Task A_throttled_confirmation_is_retried_without_registering_twice()
    {
        var confirms = 0;
        _server.Intercept = (tool, _) => tool == "confirm_time_registration_create" && confirms++ == 0
            ? new ForecastToolResult(true, "429 Too Many Requests", null)
            : null;

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        Assert.Single(_server.Entries);
        Assert.Equal(2, _server.Confirms);
    }

    // ── E28 / E19 ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ForecastSubmitMode.DryRun)]
    [InlineData(ForecastSubmitMode.Fill)]
    public async Task An_entry_of_a_task_unknown_locally_is_an_error_cell_not_a_vanished_one(ForecastSubmitMode mode)
    {
        var draft = Draft(Entry(_dev, 0, 1m), new TimesheetEntry { ForecastTaskId = 9999, Date = Monday.AddDays(2), Hours = 1.5m });

        var report = await Writer().FillWeekAsync(draft, mode);

        var unknown = report.Cells.Single(c => c.ForecastTaskId == 9999);
        Assert.Equal(ForecastCellStatus.Error, unknown.Status);
        Assert.Equal(ForecastCellIssue.TaskUnknownLocally, unknown.Issue);
        Assert.Equal(1.5m, unknown.Expected);
        Assert.False(report.Success);
        Assert.Equal(mode == ForecastSubmitMode.Fill ? ForecastCellStatus.Ok : ForecastCellStatus.Planned, report.Cells.Single(c => c.ForecastTaskId == _dev.Id).Status);
        Assert.Equal(mode == ForecastSubmitMode.Fill ? 1 : 0, _server.Creates.Count());
    }

    [Fact]
    public async Task A_day_with_approved_registrations_blocks_the_cells_to_register_on_it()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));
        _server.ApprovedDays.Add((FakeForecastClient.DevProject, Monday));

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_meetings, 0, 1m), Entry(_meetings, 1, 1m)), ForecastSubmitMode.Fill);

        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _dev, 0).Status); // already there: nothing to write
        var locked = Cell(report, _meetings, 0);
        Assert.Equal(ForecastCellStatus.Locked, locked.Status);
        Assert.Equal(ForecastEntryState.NotEntered, locked.EntryState);
        Assert.Contains("approuvées", locked.Message);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _meetings, 1).Status); // another day: free
        Assert.Single(_server.Creates);
        Assert.False(report.Success);
    }

    [Fact]
    public async Task An_invoiced_registration_locks_the_day_too_and_a_submitted_one_only_warns()
    {
        _server.Entries.Add((FakeForecastClient.MeetingsProject, Monday, 60, FakeForecastClient.MyId));
        _server.InvoicedDays.Add((FakeForecastClient.MeetingsProject, Monday));
        _server.Entries.Add((FakeForecastClient.MeetingsProject, Monday.AddDays(1), 60, FakeForecastClient.MyId));
        _server.SubmittedDays.Add((FakeForecastClient.MeetingsProject, Monday.AddDays(1)));

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m), Entry(_dev, 1, 1m)), ForecastSubmitMode.DryRun);

        Assert.Equal(ForecastCellStatus.Locked, Cell(report, _dev, 0).Status);
        Assert.Contains("facturées", Cell(report, _dev, 0).Message);
        var submitted = Cell(report, _dev, 1);
        Assert.Equal(ForecastCellStatus.Planned, submitted.Status);
        Assert.Contains(submitted.Findings, f => f.Kind == ForecastFindingKind.PendingApproval);
    }

    // ── E11 / E12: comparison by project and day ──────────────────────────────────────────────────

    [Fact]
    public async Task The_registrations_found_are_kept_on_the_cell_and_the_comparison_is_marked_approximate()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 30, FakeForecastClient.MyId));
        _server.Notes[(FakeForecastClient.DevProject, Monday)] = "Revue de code";

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 2m)), ForecastSubmitMode.DryRun);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Mismatch, cell.Status);
        Assert.Equal(ForecastComparisonLevel.ProjectAndDay, cell.Comparison);
        var entry = Assert.Single(cell.ExistingEntries);
        Assert.Equal("Revue de code", entry.Notes);
        Assert.Equal(0.5m, entry.Hours);
        Assert.NotNull(entry.EntryId);
        Assert.Equal("NOT_SUBMITTED", entry.ApprovalStatus);
    }

    [Fact]
    public async Task After_a_fill_interrupted_between_two_tasks_of_one_project_the_cell_done_is_recognised_and_the_other_is_still_planned()
    {
        // The first task (2 h) is in Forecast; the second (1 h) is not. Forecast holds 2 h for the project that day.
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));
        var draft = Draft(Entry(_dev, 0, 2m), Entry(_review, 0, 1m));

        var dry = await Writer().FillWeekAsync(draft, ForecastSubmitMode.DryRun);

        Assert.True(dry.Success, dry.Summary);
        Assert.Equal(1, dry.Cells.Count(c => c.Status == ForecastCellStatus.Ok));
        Assert.Equal(1, dry.Cells.Count(c => c.Status == ForecastCellStatus.Planned));

        var fill = await Writer().FillWeekAsync(draft, ForecastSubmitMode.Fill);

        Assert.True(fill.Success, fill.Summary);
        Assert.Single(_server.Creates);
        Assert.Equal(180, _server.Entries.Sum(e => e.Minutes));
    }

    [Fact]
    public async Task Two_tasks_of_a_project_judged_together_on_a_real_conflict_share_one_group()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 30, FakeForecastClient.MyId));

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 2m), Entry(_review, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.All(report.Cells, c =>
        {
            Assert.Equal(ForecastCellStatus.Mismatch, c.Status);
            Assert.Equal(3m, c.GroupExpected);
            Assert.Equal(0.5m, c.Actual);
        });
        Assert.Single(report.Cells.Select(c => c.GroupKey).Distinct());
    }

    // ── E2 / E3 / E4 / E8: read-only checks ───────────────────────────────────────────────────────

    [Fact]
    public async Task Without_the_tools_of_the_checks_the_report_says_what_could_not_be_verified()
    {
        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.Contains(report.PreflightLimits, l => l.Contains("list_project_tasks"));
        Assert.Contains(report.PreflightLimits, l => l.Contains("search_projects"));
        Assert.Empty(Assert.Single(report.Cells).Findings);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task The_checks_flag_a_finished_blocked_task_a_closed_project_and_an_exceeded_estimate_without_stopping_the_fill()
    {
        var server = new FakeForecastClient().AddPreflightTools();
        server.Projects.Add((740, "Portail", "Acme", "DONE"));
        server.ProjectTasks.Add((3382, "Développement", true, true, "hours", 10, 9.5));
        var connector = new FakeForecastConnector(server);
        var writer = new ForecastTimesheetWriter(
            new ForecastApi(connector, Options.Create(new ForecastOptions { RetryBaseDelay = TimeSpan.Zero }), NullLogger<ForecastApi>.Instance),
            new Factory(CreateDb), NullLogger<ForecastTimesheetWriter>.Instance);

        var report = await writer.FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Planned, cell.Status);
        var kinds = cell.Findings.Select(f => f.Kind).ToList();
        Assert.Contains(ForecastFindingKind.ProjectClosed, kinds);
        Assert.Contains(ForecastFindingKind.TaskClosed, kinds);
        Assert.Contains(ForecastFindingKind.TaskBlocked, kinds);
        Assert.Contains(ForecastFindingKind.EstimateExceeded, kinds);
        Assert.True(report.Success);
    }

    [Fact]
    public async Task An_estimate_in_points_is_not_checked_and_is_said_so()
    {
        var server = new FakeForecastClient().AddPreflightTools();
        server.ProjectTasks.Add((3382, "Développement", false, false, "points", 3, 3));
        var writer = new ForecastTimesheetWriter(
            new ForecastApi(new FakeForecastConnector(server), Options.Create(new ForecastOptions { RetryBaseDelay = TimeSpan.Zero }), NullLogger<ForecastApi>.Instance),
            new Factory(CreateDb), NullLogger<ForecastTimesheetWriter>.Instance);

        var report = await writer.FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.DoesNotContain(Assert.Single(report.Cells).Findings, f => f.Kind == ForecastFindingKind.EstimateExceeded);
        Assert.Contains(report.PreflightLimits, l => l.Contains("points"));
    }

    [Fact]
    public async Task A_local_task_that_is_switched_off_but_still_has_hours_is_flagged()
    {
        await using (var db = CreateDb())
        {
            (await db.ForecastTasks.FirstAsync(t => t.Id == _dev.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.Contains(Assert.Single(report.Cells).Findings, f => f.Kind == ForecastFindingKind.TaskInactive);
    }

    [Fact]
    public async Task Two_tasks_with_the_same_name_in_one_project_are_reported_as_ambiguous_not_missing()
    {
        // No code in the local task: only the name matches, and two tasks of Forecast bear it.
        var twin = new ForecastTask { ExternalId = "Portail/Revue", ProjectName = "Portail", TaskName = "Revue" };
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(twin);
            await db.SaveChangesAsync();
        }
        var remote = new List<ForecastRemoteTask>
        {
            new("1", "T1", "Revue", "Portail", "P740", null, "740"),
            new("2", "T2", "Revue", "Portail", "P740", null, "740"),
        };

        var (found, ambiguous) = ForecastTimesheetWriter.Resolve(twin, remote);

        Assert.Null(found);
        Assert.True(ambiguous);
    }

    // ── Idle / internal time and the report as the page reads it ──────────────────────────────────

    [Fact]
    public async Task Time_without_a_project_is_listed_on_the_report_and_never_compared_or_written()
    {
        _server.Entries.Add(("", Monday, 60, FakeForecastClient.MyId));
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 60, FakeForecastClient.MyId));

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m), Entry(_meetings, 0, 1m)), ForecastSubmitMode.Fill);

        var idle = Assert.Single(report.NonProjectEntries);
        Assert.Equal(1m, idle.Hours);
        Assert.Null(idle.ProjectId);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _dev, 0).Status);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _meetings, 0).Status);
        Assert.Single(_server.Creates); // the meetings only: the idle hour matched nothing
    }

    [Fact]
    public async Task A_report_with_a_locked_day_a_conflict_and_idle_time_gives_the_gaps_the_page_shows()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 30, FakeForecastClient.MyId));
        _server.Entries.Add((FakeForecastClient.MeetingsProject, Monday.AddDays(1), 60, FakeForecastClient.MyId));
        _server.ApprovedDays.Add((FakeForecastClient.MeetingsProject, Monday.AddDays(1)));
        _server.Entries.Add(("", Monday.AddDays(2), 60, FakeForecastClient.MyId));
        var draft = Draft(Entry(_dev, 0, 2m), Entry(_meetings, 1, 1m), Entry(_review, 1, 1m));

        var report = await Writer().FillWeekAsync(draft, ForecastSubmitMode.DryRun);
        var analysis = await new SubmissionGapService().AnalyzeAsync(draft, report);

        Assert.Contains(analysis.Gaps, g => g.Type == SubmissionGapType.ForecastLower && g.Severity == SubmissionGapSeverity.Blocking && g.IsApproximate);
        Assert.Contains(analysis.Gaps, g => g.Type == SubmissionGapType.CellLocked && g.Cell!.ForecastTaskId == _review.Id);
        Assert.Contains(analysis.Gaps, g => g.Type == SubmissionGapType.ForecastTimeWithoutProject);
        Assert.False(analysis.Summary.CanFill);
        Assert.Equal(1m, analysis.Summary.ForecastNonProjectHours);
    }
}
