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
    public void The_effective_set_is_the_flag_amended_by_the_explicit_choices_of_the_week()
    {
        var tasks = new[] { Task(1, flagged: true), Task(2, flagged: true), Task(3), Task(4) };

        var effective = SubmissionTaskScope.Effective(tasks, new Dictionary<int, bool> { [1] = false, [3] = true, [4] = false, [99] = true });

        Assert.Equal([2, 3], effective.Order());
    }

    [Fact]
    public void A_choice_is_stored_only_when_it_differs_from_what_the_flag_gives()
    {
        Assert.Null(SubmissionTaskScope.ChoiceToStore(Task(1, flagged: true), ignore: true));
        Assert.Equal(false, SubmissionTaskScope.ChoiceToStore(Task(1, flagged: true), ignore: false));
        Assert.Null(SubmissionTaskScope.ChoiceToStore(Task(2), ignore: false));
        Assert.Equal(true, SubmissionTaskScope.ChoiceToStore(Task(2), ignore: true));
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
