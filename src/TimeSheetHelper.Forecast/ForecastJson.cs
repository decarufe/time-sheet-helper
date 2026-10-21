using System.Globalization;
using System.Text.Json;

namespace TimeSheetHelper.Forecast;

/// <summary>A task as the Forecast server describes it (<paramref name="Description"/> only when the server sends one).</summary>
public sealed record ForecastRemoteTask(
    string Id, string? Code, string Title, string? ProjectName, string? ProjectCode, string? ClientName, string? ProjectId = null,
    string? Description = null);

/// <summary>A project as "search_projects" describes it: <paramref name="Id"/> is the company project id (the number of "P1078").</summary>
public sealed record ForecastRemoteProject(string Id, string? Code, string Name, string? ClientName, string? Status);

/// <summary>
/// A registration of time as the Forecast server describes it. <paramref name="TaskId"/> is an internal id that matches no
/// task number ("T3382"): entries are matched to a task by project and day.
/// </summary>
public sealed record ForecastRemoteEntry(
    string? TaskId, string? TaskCode, DateOnly Date, decimal Hours, string? PersonId, string? ProjectId = null,
    string? EntryId = null, string? Notes = null, string? ApprovalStatus = null, bool Invoiced = false, string? ProjectName = null)
{
    public bool IsApproved => string.Equals(ApprovalStatus, "APPROVED", StringComparison.OrdinalIgnoreCase);

    public bool IsSubmitted => string.Equals(ApprovalStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>State of a task as "list_project_tasks" describes it (<paramref name="Code"/> is the "T…" number).</summary>
public sealed record ForecastRemoteTaskState(
    string Code, string Title, bool IsDone, bool IsBlocked, string? StatusName, string? EstimationUnit,
    decimal? Estimate, decimal? Remaining, decimal? ActualHours);

/// <summary>What a write tool answered: "ok", and when it is false the "outcome" (refused, failed or unknown).</summary>
public sealed record ForecastToolOutcome(bool Ok, string? Outcome, string? Message);

/// <summary>
/// Reads the JSON the Forecast tools return without depending on one exact shape: the list is found wherever it is
/// (root, "items", "data", ...), and fields are looked up by name ignoring case and separators.
/// </summary>
public static class ForecastJson
{
    private static readonly string[] ContainerKeys =
        ["items", "data", "results", "result", "tasks", "entries", "timeentries", "timeregistrations", "registrations", "records", "rows", "content", "values"];

    public static IReadOnlyList<JsonElement> FindItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (root.ValueKind != JsonValueKind.Object)
            return [];

        // "find_task" answers one match as {"task": {...}} whose own lists (assignees) must not be taken for the items.
        if (Get(root, "task") is { ValueKind: JsonValueKind.Object } single)
            return [single];

        foreach (var key in ContainerKeys)
        {
            if (Get(root, key) is { } value)
            {
                var inner = FindItems(value);
                if (inner.Count > 0)
                    return inner;
            }
        }

        var list = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object))
            .Select(p => p.Value).FirstOrDefault();
        if (list.ValueKind == JsonValueKind.Array)
            return FindItems(list);

        return Get(root, "id") is not null ? [root] : [];
    }

    public static IReadOnlyList<ForecastRemoteTask> ParseTasks(JsonElement root)
    {
        var tasks = new List<ForecastRemoteTask>();
        foreach (var item in FindItems(root))
        {
            var id = Text(Get(item, "id", "taskid", "cardid"));
            var title = Text(Get(item, "title", "name", "taskname", "summary"));
            if (id is null || title is null)
                continue;

            var project = Get(item, "project");
            var projectName = Text(project is { ValueKind: JsonValueKind.Object } p ? Get(p, "name", "title") : Get(item, "projectname", "projecttitle"))
                ?? (project is { ValueKind: JsonValueKind.String } ps ? ps.GetString() : null);
            var projectCodeRaw = project is { ValueKind: JsonValueKind.Object } po
                ? Get(po, "companyprojectid", "projectcode", "code", "key", "number")
                : Get(item, "companyprojectid", "projectcode");
            var client = Get(item, "client", "company", "customer");
            var clientName = Text(client is { ValueKind: JsonValueKind.Object } c ? Get(c, "name", "title") : Get(item, "clientname", "customername"))
                ?? (client is { ValueKind: JsonValueKind.String } cs ? cs.GetString() : null);

            // Forecast's own task_id is the number shown in the interface as "T<number>".
            var code = Code(Get(item, "companytaskid", "taskcode", "code", "key", "number", "identifier", "taskid"), 'T', ForecastCodes.TaskCode)
                ?? FirstMatch(ForecastCodes.TaskCode, title);
            var projectCode = Code(projectCodeRaw, 'P', ForecastCodes.ProjectCode)
                ?? (projectName is null ? null : FirstMatch(ForecastCodes.ProjectCode, projectName));

            var projectId = Text(project is { ValueKind: JsonValueKind.Object } pi ? Get(pi, "id", "projectid") : Get(item, "projectid"));

            var description = Text(Get(item, "description"));

            tasks.Add(new ForecastRemoteTask(id, code, StripCode(title, code), StripCode(projectName, projectCode), projectCode, clientName, projectId, description));
        }
        return tasks;
    }

    /// <summary>
    /// Reads the answer of "search_projects": one project as a flat object, several under "matches", and nothing when
    /// "found" is false (the "all_projects" list sent then is a suggestion, not a result).
    /// </summary>
    public static IReadOnlyList<ForecastRemoteProject> ParseProjects(JsonElement root)
    {
        IEnumerable<JsonElement> items;
        if (root.ValueKind == JsonValueKind.Array)
            items = root.EnumerateArray();
        else if (root.ValueKind != JsonValueKind.Object || Get(root, "found") is { ValueKind: JsonValueKind.False })
            return [];
        else if (Get(root, "matches", "projects") is { ValueKind: JsonValueKind.Array } list)
            items = list.EnumerateArray();
        else
            items = [root];

        var projects = new List<ForecastRemoteProject>();
        foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
        {
            // "id" is an opaque Relay id here: the number is "project_id".
            var id = Text(Get(item, "projectid")) ?? (Text(Get(item, "id")) is { } raw && raw.All(char.IsDigit) ? raw : null);
            var name = Text(Get(item, "name", "title"));
            if (id is null || name is null)
                continue;

            var code = Code(Get(item, "customprojectid", "companyprojectid", "projectcode"), 'P', ForecastCodes.ProjectCode);
            var client = Get(item, "client", "customer");
            var clientName = client is { ValueKind: JsonValueKind.Object } c ? Text(Get(c, "name", "title")) : Text(client);
            projects.Add(new ForecastRemoteProject(id, code, name.Trim(), clientName?.Trim(), Text(Get(item, "status", "stage"))));
        }
        return projects;
    }

    public static IReadOnlyList<ForecastRemoteEntry> ParseTimeEntries(JsonElement root)
    {
        var entries = new List<ForecastRemoteEntry>();
        foreach (var item in FindItems(root))
        {
            var date = ParseDate(Text(Get(item, "date", "day", "workdate", "entrydate", "registrationdate", "dateregistered")));
            var hours = ParseHours(item);
            if (date is null || hours is null)
                continue;

            var task = Get(item, "task", "card");
            var taskId = Text(Get(item, "taskid", "cardid")) ?? (task is { ValueKind: JsonValueKind.Object } t ? Text(Get(t, "id")) : Text(task));
            var taskCode = task is { ValueKind: JsonValueKind.Object } tc
                ? Code(Get(tc, "companytaskid", "taskcode", "code", "key", "number"), 'T', ForecastCodes.TaskCode)
                : null;
            var person = Get(item, "person", "user");
            var personId = Text(Get(item, "personid", "userid")) ?? (person is { ValueKind: JsonValueKind.Object } p ? Text(Get(p, "id")) : Text(person));

            var project = Get(item, "project");
            var projectId = Text(project is { ValueKind: JsonValueKind.Object } pr ? Get(pr, "id", "projectid") : Get(item, "projectid"));
            var projectName = project is { ValueKind: JsonValueKind.Object } pn ? Text(Get(pn, "name", "title")) : null;

            entries.Add(new ForecastRemoteEntry(
                taskId, taskCode, date.Value, hours.Value, personId, projectId,
                EntryId: Text(Get(item, "entryid", "registrationid", "timeregid")),
                Notes: Text(Get(item, "notes", "note", "comment"))?.Trim(),
                ApprovalStatus: Text(Get(item, "approvalstatus")),
                Invoiced: Get(item, "invoiced", "isinvoiced") is { ValueKind: JsonValueKind.True },
                ProjectName: projectName?.Trim()));
        }
        return entries;
    }

    /// <summary>
    /// Whether the answer of "get_time_analysis" holds every registration: "has_more" is true, or "entry_count" is above the
    /// entries listed, when the server caps the list (200 by default).
    /// </summary>
    public static (bool HasMore, int? EntryCount, string? Note) ParseCompleteness(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (false, null, null);

        var count = Get(root, "entrycount", "totalcount") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var c) ? c : (int?)null;
        return (Get(root, "hasmore") is { ValueKind: JsonValueKind.True }, count, Text(Get(root, "paginationnote")));
    }

    /// <summary>Reads the tasks of "list_project_tasks": completion, blocking, estimate and hours already registered.</summary>
    public static IReadOnlyList<ForecastRemoteTaskState> ParseTaskStates(JsonElement root)
    {
        var states = new List<ForecastRemoteTaskState>();
        foreach (var item in FindItems(root))
        {
            var code = Code(Get(item, "taskid", "companytaskid", "id"), 'T', ForecastCodes.TaskCode);
            if (code is null)
                continue;

            var category = Text(Get(item, "statuscategory"));
            var done = Get(item, "isdone") is { ValueKind: JsonValueKind.True }
                || string.Equals(category, "DONE", StringComparison.OrdinalIgnoreCase);
            states.Add(new ForecastRemoteTaskState(
                code,
                Text(Get(item, "taskname", "name", "title")) ?? "",
                done,
                Get(item, "blocked") is { ValueKind: JsonValueKind.True },
                Text(Get(item, "statuscolumnname")),
                Text(Get(item, "estimationunit")),
                Number(Get(item, "estimate")),
                Number(Get(item, "remaining")),
                Number(Get(item, "actualhours"))));
        }
        return states;
    }

    /// <summary>The "warnings" a plan tool returned (strings, or objects with a message).</summary>
    public static IReadOnlyList<string> ParseWarnings(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj || Get(obj, "warnings") is not { ValueKind: JsonValueKind.Array } list)
            return [];

        return list.EnumerateArray()
            .Select(w => w.ValueKind == JsonValueKind.Object ? Text(Get(w, "message", "text", "warning", "detail")) ?? w.GetRawText() : Text(w) ?? w.GetRawText())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .ToList();
    }

    /// <summary>
    /// The outcome of a write tool: "ok": false (with its "outcome": refused, failed or unknown) means the write did not succeed.
    /// An answer with neither field is taken as a success.
    /// </summary>
    public static ForecastToolOutcome ParseOutcome(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj)
            return new ForecastToolOutcome(true, null, null);

        var outcome = Text(Get(obj, "outcome"))?.ToLowerInvariant();
        var failed = Get(obj, "ok") is { ValueKind: JsonValueKind.False } || outcome is "refused" or "failed" or "unknown";
        return failed
            ? new ForecastToolOutcome(false, outcome ?? "failed", Text(Get(obj, "message", "error", "detail")))
            : new ForecastToolOutcome(true, outcome, null);
    }

    /// <summary>Id of the signed-in person, from the "current user" tool result.</summary>
    public static string? ParsePersonId(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (Text(Get(root, "id", "personid", "userid")) is { } id)
                return id;
            foreach (var key in new[] { "person", "user", "me", "data", "result" })
            {
                if (Get(root, key) is { ValueKind: JsonValueKind.Object } inner && ParsePersonId(inner) is { } found)
                    return found;
            }
        }
        return null;
    }

    private static decimal? ParseHours(JsonElement item)
    {
        // Forecast rounds these hours to one decimal (0.25 → 0.2, 0.75 → 0.8): snap back to the quarter hour that was registered.
        if (Get(item, "hours", "hoursregistered", "hourstime", "totalhours") is { } h && Number(h) is { } hours)
            return ForecastHours.RoundToQuarter(hours);
        // Forecast registers time in minutes.
        if (Get(item, "timeregistered", "minutes", "minutesregistered", "duration", "timespent", "time") is { } m)
        {
            if (m.ValueKind == JsonValueKind.Number && Number(m) is { } minutes)
                return minutes / 60m;
            if (m.ValueKind == JsonValueKind.String && ForecastHours.TryParse(m.GetString(), out var parsed))
                return parsed;
        }
        return null;
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (text is null)
            return null;
        var head = text.Length >= 10 ? text[..10] : text;
        return DateOnly.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>"3382" → "T3382"; "T3382" stays; anything else is not a code.</summary>
    private static string? Code(JsonElement? raw, char prefix, System.Text.RegularExpressions.Regex pattern)
    {
        var text = Text(raw);
        if (text is null)
            return null;
        if (text.All(char.IsDigit))
            return prefix + text;
        return FirstMatch(pattern, text);
    }

    private static string? FirstMatch(System.Text.RegularExpressions.Regex pattern, string text) =>
        pattern.Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>"T3382 - Dev" → "Dev" when the name starts with its own code.</summary>
    private static string StripCode(string? name, string? code)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        if (code is not null && name.StartsWith(code, StringComparison.OrdinalIgnoreCase))
            return name[code.Length..].TrimStart(' ', '-', '–', ':', '.').Trim() is { Length: > 0 } rest ? rest : name;
        return name.Trim();
    }

    internal static JsonElement? Get(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (ForecastArgumentBuilder.Normalize(property.Name) == name && property.Value.ValueKind != JsonValueKind.Null)
                    return property.Value;
            }
        }
        return null;
    }

    internal static string? Text(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString() is { Length: > 0 } s ? s : null,
        JsonValueKind.Number => value.Value.GetRawText(),
        _ => null,
    };

    private static decimal? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDecimal(out var d) ? d : null,
        JsonValueKind.String => ForecastHours.TryParse(value.GetString(), out var h) ? h : null,
        _ => null,
    };

    private static decimal? Number(JsonElement? value) => value is { } v ? Number(v) : null;
}
