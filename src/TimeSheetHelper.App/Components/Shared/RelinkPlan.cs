using System.Globalization;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>What linking a local task to a Forecast task means for the page: the task to send, and whether it is a merge. Pure.</summary>
public static class RelinkPlan
{
    /// <summary>The Forecast task the user picked, in the shape <see cref="ForecastTaskLinker"/> expects (the task number is its identity).</summary>
    public static ForecastRemoteTask ToRemote(ForecastTaskMatch match) =>
        new(match.Code, match.Code, match.Name, match.ProjectName, match.ProjectCode, match.ClientName,
            match.ProjectId?.ToString(CultureInfo.InvariantCulture), match.Description);

    /// <summary>The other local task that already carries this Forecast number: linking then merges the two (irreversible). Null = a plain link.</summary>
    public static ForecastTask? MergeTarget(IEnumerable<ForecastTask> tasks, int localTaskId, string code) =>
        tasks.FirstOrDefault(t => t.Id != localTaskId && string.Equals(t.ExternalId, code, StringComparison.Ordinal));

    /// <summary>The impact of the merge of <paramref name="local"/> into <paramref name="target"/>: the cells of the draft and the rules that follow.</summary>
    public static SubmitDialogs.MergeImpact ImpactOf(ForecastTask local, ForecastTask target, TimesheetDraft draft, int rules)
    {
        var cells = draft.Entries
            .Where(e => e.ForecastTaskId == local.Id)
            .GroupBy(e => e.Date)
            .Select(g => g.Sum(e => e.Hours))
            .Where(hours => hours > 0)
            .ToList();
        return new SubmitDialogs.MergeImpact(local.TaskName, target.TaskName, target.ExternalId, cells.Count, cells.Sum(), rules);
    }
}
