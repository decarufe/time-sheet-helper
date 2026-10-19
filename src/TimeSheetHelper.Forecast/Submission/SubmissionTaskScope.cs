using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Forecast;

/// <summary>An hour cell of the draft that is not sent to Forecast because its task is ignored for this submission.</summary>
public sealed record SubmissionIgnoredCell(int ForecastTaskId, string TaskName, DateOnly Date, decimal Hours, bool Automatic);

/// <summary>
/// Which tasks of a draft are left out of the fill. A task flagged <see cref="ForecastTask.ExcludeFromSubmission"/> is left out
/// by default (Forecast's MCP cannot register it); the user may re-include it for one submission, or leave out any other task.
/// Pure.
/// </summary>
public static class SubmissionTaskScope
{
    /// <summary>The tasks left out by their own flag; the special "ignored" task never counts as such.</summary>
    public static IReadOnlySet<int> AutoExcluded(IEnumerable<ForecastTask> tasks) =>
        tasks.Where(t => t.ExcludeFromSubmission && !t.IsIgnored).Select(t => t.Id).ToHashSet();

    /// <summary>(flagged − re-included) ∪ manually ignored.</summary>
    public static IReadOnlySet<int> Effective(
        IEnumerable<ForecastTask> tasks, IEnumerable<int> manuallyIgnored, IEnumerable<int> reincluded)
    {
        var effective = AutoExcluded(tasks).ToHashSet();
        effective.ExceptWith(reincluded);
        effective.UnionWith(manuallyIgnored);
        return effective;
    }

    /// <summary>The cells of the draft (rounded like the fill does) that fall on an ignored task, restricted to <paramref name="days"/> when given.</summary>
    public static IReadOnlyList<SubmissionIgnoredCell> IgnoredCells(
        TimesheetDraft draft,
        IReadOnlyDictionary<int, ForecastTask> tasks,
        IReadOnlySet<int> ignored,
        IReadOnlySet<int> automatic,
        IReadOnlyCollection<DateOnly>? days = null)
    {
        var weekEnd = draft.WeekStart.AddDays(7);
        return draft.Entries
            .Where(e => ignored.Contains(e.ForecastTaskId) && e.Date >= draft.WeekStart && e.Date < weekEnd)
            .Where(e => days is not { Count: > 0 } || days.Contains(e.Date))
            .GroupBy(e => (e.ForecastTaskId, e.Date))
            .Select(g => (g.Key.ForecastTaskId, g.Key.Date, Hours: ForecastHours.RoundToQuarter(g.Sum(e => e.Hours))))
            .Where(c => c.Hours > 0)
            .Select(c => new SubmissionIgnoredCell(
                c.ForecastTaskId,
                tasks.TryGetValue(c.ForecastTaskId, out var task) ? task.DisplayName : $"Tâche #{c.ForecastTaskId}",
                c.Date,
                c.Hours,
                automatic.Contains(c.ForecastTaskId)))
            .OrderBy(c => c.Date).ThenBy(c => c.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
