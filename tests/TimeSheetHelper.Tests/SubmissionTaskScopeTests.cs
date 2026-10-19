using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public sealed class SubmissionTaskScopeTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static ForecastTask Task(int id, bool flagged = false, string? externalId = null) =>
        new() { Id = id, ExternalId = externalId ?? $"T{id}", ProjectName = "P", TaskName = $"Tâche {id}", ExcludeFromSubmission = flagged };

    private static TimesheetEntry Entry(int task, int day, decimal hours) =>
        new() { ForecastTaskId = task, Date = Monday.AddDays(day), Hours = hours };

    [Fact]
    public void Flagged_tasks_are_ignored_by_default_except_the_special_ignored_task()
    {
        var tasks = new[] { Task(1, flagged: true), Task(2), Task(3, flagged: true, externalId: ForecastTask.IgnoredExternalId) };

        Assert.Equal([1], SubmissionTaskScope.AutoExcluded(tasks));
    }

    [Fact]
    public void The_effective_set_is_the_flagged_minus_the_reincluded_plus_the_manually_ignored()
    {
        var tasks = new[] { Task(1, flagged: true), Task(2, flagged: true), Task(3), Task(4) };

        var effective = SubmissionTaskScope.Effective(tasks, manuallyIgnored: [3], reincluded: [1]);

        Assert.Equal([2, 3], effective.Order());
    }

    [Fact]
    public void Ignored_cells_sum_the_hours_per_day_follow_the_day_scope_and_tell_automatic_from_manual()
    {
        var tasks = new[] { Task(1, flagged: true), Task(2), Task(3) }.ToDictionary(t => t.Id);
        var draft = new TimesheetDraft
        {
            WeekStart = Monday,
            Entries = [Entry(1, 0, 1m), Entry(1, 0, 0.5m), Entry(2, 1, 2m), Entry(3, 0, 4m), Entry(1, 2, 0m)],
        };

        var week = SubmissionTaskScope.IgnoredCells(draft, tasks, new HashSet<int> { 1, 2 }, new HashSet<int> { 1 });
        var monday = SubmissionTaskScope.IgnoredCells(draft, tasks, new HashSet<int> { 1, 2 }, new HashSet<int> { 1 }, [Monday]);

        Assert.Equal([(1, 1.5m, true), (2, 2m, false)], week.Select(c => (c.ForecastTaskId, c.Hours, c.Automatic)));
        Assert.Equal([1], monday.Select(c => c.ForecastTaskId));
    }
}
