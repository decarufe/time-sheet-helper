using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>Tooltip text for a task or project: its number and full name, on two lines (task, then project and client).</summary>
public static class TaskTooltips
{
    public const string IgnoredText = "Ignoré : retire ce temps de la feuille de temps";

    public static string? For(ForecastTask? task)
    {
        if (task is null)
            return null;
        if (task.IsIgnored)
            return IgnoredText;

        var code = ForecastCodes.TaskCode.IsMatch(task.ExternalId) ? task.ExternalId : null;
        return For(code, task.TaskName, task.ProjectCode, task.ProjectName, task.ClientName);
    }

    public static string? For(string? taskCode, string? taskName, string? projectCode, string? projectName, string? clientName)
    {
        var taskLine = Join(taskCode, taskName);
        var projectLine = Join(Join(projectCode, projectName), clientName);
        return (taskLine, projectLine) switch
        {
            (null, null) => null,
            (null, _) => projectLine,
            (_, null) => taskLine,
            _ => taskLine + "\n" + projectLine,
        };
    }

    private static string? Join(string? first, string? second)
    {
        var a = string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        var b = string.IsNullOrWhiteSpace(second) ? null : second.Trim();
        return a is null ? b : b is null ? a : $"{a} · {b}";
    }
}
