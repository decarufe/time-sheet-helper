using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests;

/// <summary>States, operations and send buttons of one day (contracts §1.5): no write is ever planned over something Forecast already holds.</summary>
public sealed class DayPlannerTests
{
    private static readonly DateOnly Week = new(2026, 10, 12);
    private static readonly DateOnly Day = Week.AddDays(1);

    private static readonly ForecastCapabilities All = new(ForecastCapability.Available, ForecastCapability.Available, ForecastCapability.Available, null);

    private static readonly ForecastCapabilities CreateOnly = new(
        ForecastCapability.Available, ForecastCapability.Unavailable, ForecastCapability.Unavailable, "Pas de modification.");

    private readonly Dictionary<int, ForecastTask> _tasks = new()
    {
        [1] = new ForecastTask { Id = 1, ExternalId = "T1001", ProjectName = "Alpha", TaskName = "Dev" },
        [2] = new ForecastTask { Id = 2, ExternalId = "T1002", ProjectName = "Beta", TaskName = "Test" },
        [3] = new ForecastTask { Id = 3, ExternalId = "T1003", ProjectName = "Alpha", TaskName = "Revue" },
    };

    private static TimesheetEntry Draft(int taskId, decimal hours, DateOnly? date = null) =>
        new() { Date = date ?? Day, ForecastTaskId = taskId, Hours = hours };

    private static ForecastRemoteEntry Remote(string? taskCode, decimal hours, string? entryId = null, string? approval = null, string? project = null) =>
        new(null, taskCode, Day, hours, null, EntryId: entryId, ApprovalStatus: approval, ProjectName: project);

    private static ForecastDayRead Read(params ForecastRemoteEntry[] entries) => new(Day, entries, true, null);

    private static DaySubmission Sent(DayStatus status, params (int TaskId, decimal Hours, string? EntryId)[] cells) => new()
    {
        WeekStart = Week,
        Date = Day,
        Status = status,
        Cells = cells.Select(c => new DaySubmissionCell { ForecastTaskId = c.TaskId, SentHours = c.Hours, ForecastEntryId = c.EntryId }).ToList(),
    };

    private DayPlan Plan(
        IReadOnlyList<TimesheetEntry> draft, ForecastDayRead forecast, DaySubmission? previous = null,
        IReadOnlySet<int>? ignored = null, ForecastCapabilities? capabilities = null) =>
        DayPlanner.Plan(Week, Day, draft, forecast, previous, ignored ?? new HashSet<int>(), _tasks, capabilities ?? All);

    private static CellPlan Cell(DayPlan plan, int taskId) => plan.Cells.Single(c => c.ForecastTaskId == taskId);

    [Fact]
    public void Draft_hours_absent_from_Forecast_are_created()
    {
        var plan = Plan([Draft(1, 4m), Draft(2, 2m, Day.AddDays(1))], Read());

        var cell = Assert.Single(plan.Cells);
        Assert.Equal((1, CellState.New, CellOperation.Create, 4m, (decimal?)null), (cell.ForecastTaskId, cell.State, cell.Operation, cell.DraftHours, cell.ForecastHours));
        Assert.Equal((DayGate.Send, DayStatus.NotAttempted, true), (plan.Gate, plan.Status, plan.IsReady));
    }

    [Fact]
    public void Hours_Forecast_already_holds_are_never_written_again()
    {
        var plan = Plan([Draft(1, 2m), Draft(1, 2m)], Read(Remote("T1001", 4m, "e1")));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.Unchanged, CellOperation.None, (decimal?)4m), (cell.State, cell.Operation, cell.ForecastHours));
        Assert.Equal(DayGate.NothingNew, plan.Gate);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void A_sent_cell_changed_in_the_draft_is_updated_by_its_id()
    {
        var plan = Plan([Draft(1, 6m)], Read(Remote(null, 4m, "e1")), Sent(DayStatus.Sent, (1, 4m, "e1")));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.Modified, CellOperation.Update, "e1", (decimal?)4m, (decimal?)4m),
            (cell.State, cell.Operation, cell.ForecastEntryId, cell.ForecastHours, cell.SentHours));
        Assert.Equal((DayGate.SendChanges, DayStatus.Modified), (plan.Gate, plan.Status));
    }

    [Fact]
    public void A_modified_cell_without_the_update_tool_is_blocked_never_recreated()
    {
        var plan = Plan([Draft(1, 6m)], Read(Remote(null, 4m, "e1")), Sent(DayStatus.Sent, (1, 4m, "e1")), capabilities: CreateOnly);

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.Modified, CellOperation.None), (cell.State, cell.Operation));
        Assert.NotNull(cell.BlockedReason);
        Assert.Equal(DayGate.ResolveConflicts, plan.Gate);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void A_sent_cell_removed_from_the_draft_is_deleted()
    {
        var plan = Plan([], Read(Remote("T1001", 4m, "e1")), Sent(DayStatus.Sent, (1, 4m, "e1")));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.Deleted, CellOperation.Delete, 0m), (cell.State, cell.Operation, cell.DraftHours));
        Assert.Equal(DayGate.SendChanges, plan.Gate);
    }

    [Fact]
    public void Forecast_changed_elsewhere_is_a_conflict_with_its_resolutions()
    {
        var plan = Plan([Draft(1, 6m)], Read(Remote(null, 5m, "e1")), Sent(DayStatus.Sent, (1, 4m, "e1")));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.Conflict, CellOperation.None), (cell.State, cell.Operation));
        Assert.Equal([ConflictResolution.Keep, ConflictResolution.AcceptForecast, ConflictResolution.Complement, ConflictResolution.Remove],
            cell.AllowedResolutions);
        Assert.Equal((DayGate.ResolveConflicts, DayStatus.Conflict), (plan.Gate, plan.Status));
    }

    [Fact]
    public void Hours_in_Forecast_never_sent_by_the_application_are_a_conflict_not_a_creation()
    {
        var plan = Plan([Draft(1, 4m)], Read(Remote("T1001", 3m, "e9")));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.Conflict, CellOperation.None, (decimal?)3m), (cell.State, cell.Operation, cell.ForecastHours));
        Assert.DoesNotContain(ConflictResolution.Complement, Plan([Draft(1, 2m)], Read(Remote("T1001", 3m, "e9"))).Cells[0].AllowedResolutions);
    }

    [Fact]
    public void Project_hours_that_cannot_be_split_between_tasks_block_both_tasks()
    {
        var plan = Plan([Draft(1, 2m), Draft(3, 2m)], Read(Remote(null, 5m, "e1", project: "Alpha")));

        Assert.All(plan.Cells, c =>
        {
            Assert.Equal((CellState.Conflict, CellOperation.None, (decimal?)5m), (c.State, c.Operation, c.ForecastHours));
            Assert.Equal([ConflictResolution.Keep], c.AllowedResolutions);
        });
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void Project_hours_that_cannot_be_split_share_one_ambiguous_group()
    {
        var plan = Plan([Draft(1, 2m), Draft(3, 2m)], Read(Remote(null, 5m, "e1", project: "Alpha")));

        Assert.NotNull(plan.Cells[0].AmbiguousGroup);
        Assert.Equal(plan.Cells[0].AmbiguousGroup, plan.Cells[1].AmbiguousGroup);
    }

    [Fact]
    public void A_registration_of_another_task_of_the_same_project_is_not_attributed_to_a_task_of_the_day()
    {
        var plan = Plan([Draft(1, 2m)], Read(Remote("T9999", 2m, "e1", project: "Alpha")));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.New, CellOperation.Create, (decimal?)null), (cell.State, cell.Operation, cell.ForecastHours));
    }

    [Fact]
    public void Ignored_tasks_are_left_out_and_out_of_the_fingerprint()
    {
        var plan = Plan([Draft(1, 4m)], Read(), ignored: new HashSet<int> { 1 });

        Assert.Equal((CellState.Ignored, CellOperation.None), (Cell(plan, 1).State, Cell(plan, 1).Operation));
        Assert.Equal(DayGate.NothingToSend, plan.Gate);
        Assert.Equal(DayFingerprint.Compute(Day, []), plan.CurrentFingerprint);
    }

    [Fact]
    public void An_approved_day_is_locked()
    {
        var plan = Plan([Draft(1, 4m), Draft(2, 1m)], Read(Remote("T1001", 3m, "e1", approval: "APPROVED")));

        Assert.All(plan.Cells, c => Assert.Equal((CellState.Locked, CellOperation.None), (c.State, c.Operation)));
        Assert.Equal((DayGate.Locked, DayStatus.Locked), (plan.Gate, plan.Status));
    }

    [Fact]
    public void An_incomplete_read_compares_nothing_and_writes_nothing()
    {
        var plan = Plan([Draft(1, 4m)], new ForecastDayRead(Day, [], false, "page 2 manquante"), Sent(DayStatus.Partial));

        var cell = Cell(plan, 1);
        Assert.Equal((CellState.NotAttempted, CellOperation.None), (cell.State, cell.Operation));
        Assert.Contains("page 2 manquante", cell.BlockedReason);
        Assert.Equal((DayGate.ReadForecastFirst, DayStatus.Partial, false), (plan.Gate, plan.Status, plan.ForecastReadComplete));
    }

    [Fact]
    public void A_partial_day_resumes_with_only_the_missing_cells()
    {
        var plan = Plan([Draft(1, 4m), Draft(2, 2m)], Read(Remote("T1001", 4m, "e1")), Sent(DayStatus.Partial, (1, 4m, "e1")));

        Assert.Equal(CellOperation.None, Cell(plan, 1).Operation);
        Assert.Equal(CellOperation.Create, Cell(plan, 2).Operation);
        Assert.Equal(DayGate.Resume, plan.Gate);
    }

    [Fact]
    public void A_back_filled_day_matching_Forecast_becomes_sent()
    {
        var plan = Plan([Draft(1, 4m)], Read(Remote("T1001", 4m, "e1")), Sent(DayStatus.SentUnknownFingerprint));

        Assert.Equal((DayStatus.Sent, DayGate.NothingNew), (plan.Status, plan.Gate));
    }

    [Fact]
    public void A_day_in_progress_elsewhere_offers_no_send()
    {
        var plan = Plan([Draft(1, 4m)], Read(), Sent(DayStatus.InProgress));

        Assert.Equal((DayStatus.InProgress, DayGate.InProgress, false), (plan.Status, plan.Gate, plan.IsReady));
    }
}
