using System.Text.RegularExpressions;

namespace TimeSheetHelper.Forecast;

public enum ForecastToolRole
{
    ListTasks,
    ListTimeEntries,
    CreateTimeEntry,
    CurrentUser,

    /// <summary>Changes the duration of one registration, named by its id. forecast-mcp offers none (see <see cref="ForecastToolResolver.KnownName"/>).</summary>
    UpdateTimeEntry,

    /// <summary>Removes one registration, named by its id. forecast-mcp offers none.</summary>
    DeleteTimeEntry,
}

/// <summary>
/// Finds the tool of the Forecast MCP server that does a given job: the forced name (<c>Forecast:Tools:*</c>), else the
/// tool the real server is known to offer for it, else the best match by the words of the tool names
/// (see <c>forecast tools</c> for what the server offers).
/// </summary>
public static class ForecastToolResolver
{
    private static readonly HashSet<string> ReadVerbs = ["list", "get", "search", "find", "fetch", "query", "read", "show", "lookup"];
    private static readonly HashSet<string> WriteVerbs = ["create", "add", "log", "register", "book", "report", "post", "submit", "new", "insert", "track", "record"];
    private static readonly HashSet<string> OtherVerbs = ["update", "delete", "remove", "edit", "modify", "patch", "cancel", "approve", "reject", "archive", "move", "assign", "start", "stop", "confirm"];
    private static readonly HashSet<string> TaskNouns = ["task", "card", "ticket", "workitem"];
    private static readonly HashSet<string> TimeNouns = ["time", "timesheet", "timereg", "registration", "entry", "hour", "timelog", "timeentry", "worklog"];
    private static readonly HashSet<string> SelfNouns = ["me", "whoami", "current", "self", "myself", "profile", "identity"];

    // "edit" is left out on purpose: forecast-mcp's "plan_time_registration_edit" only shifts dates and replaces notes, it cannot change hours.
    private static readonly HashSet<string> UpdateVerbs = ["update", "modify", "patch", "change", "set", "adjust"];
    private static readonly HashSet<string> DeleteVerbs = ["delete", "remove"];

    /// <summary>
    /// The tools of forecast-mcp that do each job. It has none to change the hours of a registration or to remove one
    /// (checked in its catalogue): those roles are only found by configuration or by a tool that clearly does them.
    /// </summary>
    internal static string? KnownName(ForecastToolRole role) => role switch
    {
        ForecastToolRole.ListTasks => "get_person_tasks",
        ForecastToolRole.ListTimeEntries => "get_time_analysis",
        ForecastToolRole.CreateTimeEntry => "plan_time_registration_create",
        ForecastToolRole.CurrentUser => "get_current_person",
        _ => null,
    };

    /// <summary>Key under <c>Forecast:Tools</c> that forces the tool of a role.</summary>
    public static string ConfigKey(ForecastToolRole role) => role.ToString();

    public static ForecastToolInfo? Resolve(IReadOnlyList<ForecastToolInfo> tools, ForecastToolRole role, ForecastToolNames overrides)
    {
        var forced = role switch
        {
            ForecastToolRole.ListTasks => overrides.ListTasks,
            ForecastToolRole.ListTimeEntries => overrides.ListTimeEntries,
            ForecastToolRole.CreateTimeEntry => overrides.CreateTimeEntry,
            ForecastToolRole.CurrentUser => overrides.CurrentUser,
            ForecastToolRole.UpdateTimeEntry => overrides.UpdateTimeEntry,
            ForecastToolRole.DeleteTimeEntry => overrides.DeleteTimeEntry,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(forced))
            return tools.FirstOrDefault(t => string.Equals(t.Name, forced, StringComparison.OrdinalIgnoreCase));

        if (KnownName(role) is { } known && tools.FirstOrDefault(t => string.Equals(t.Name, known, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        return tools
            .Select(t => (Tool: t, Score: Score(Tokens(t.Name), role)))
            .Where(x => x.Score > 0 && FitsSchema(x.Tool, role))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tool.Name.Length)
            .Select(x => x.Tool)
            .FirstOrDefault();
    }

    internal static double Score(IReadOnlyList<string> tokens, ForecastToolRole role)
    {
        var set = tokens.ToHashSet();
        bool Has(HashSet<string> words) => set.Overlaps(words);
        var read = Has(ReadVerbs);
        var write = Has(WriteVerbs);
        var other = Has(OtherVerbs);
        var time = Has(TimeNouns);
        var task = Has(TaskNouns);
        var update = Has(UpdateVerbs);
        var delete = Has(DeleteVerbs);
        var confirm = set.Contains("confirm");
        var brevity = -0.1 * tokens.Count;

        return role switch
        {
            ForecastToolRole.ListTasks when task && !time && !write && !other => 10 + (read ? 3 : 0) + (set.Overlaps(["my", "assigned"]) ? 2 : 0) + brevity,
            ForecastToolRole.ListTimeEntries when time && !write && !other => 10 + (read ? 3 : 0) + (task ? 0 : 1) + brevity,
            ForecastToolRole.CreateTimeEntry when time && write && !other => 10 + brevity,
            ForecastToolRole.CurrentUser when Has(SelfNouns) && !write && !other && !task && !time => 10 + (read ? 2 : 0) + brevity,
            ForecastToolRole.UpdateTimeEntry when time && update && !delete && !write && !confirm => 10 + brevity,
            ForecastToolRole.DeleteTimeEntry when time && delete && !update && !write && !confirm => 10 + brevity,
            _ => 0,
        };
    }

    /// <summary>
    /// A guessed tool that changes or removes a registration must say which one (an entry id parameter) and, to change it, take a
    /// duration: a tool that edits notes or shifts dates is never taken for one that changes hours.
    /// </summary>
    private static bool FitsSchema(ForecastToolInfo tool, ForecastToolRole role) => role switch
    {
        ForecastToolRole.UpdateTimeEntry => ForecastArgumentBuilder.IdentifiesEntry(tool) && ForecastArgumentBuilder.TakesDuration(tool),
        ForecastToolRole.DeleteTimeEntry => ForecastArgumentBuilder.IdentifiesEntry(tool),
        _ => true,
    };

    /// <summary>"list_my_Tasks" / "listMyTasks" / "list-my-tasks" → [list, my, task] (plural "s" removed).</summary>
    internal static IReadOnlyList<string> Tokens(string name)
    {
        var spaced = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
        return Regex.Split(spaced.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(t => t.Length > 0)
            .Select(t => t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss") ? t[..^1] : t)
            .Select(t => t == "entrie" ? "entry" : t)
            .ToList();
    }
}
