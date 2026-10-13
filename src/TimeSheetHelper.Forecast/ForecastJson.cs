using System.Globalization;
using System.Text.Json;

namespace TimeSheetHelper.Forecast;

/// <summary>A task as the Forecast server describes it.</summary>
public sealed record ForecastRemoteTask(
    string Id, string? Code, string Title, string? ProjectName, string? ProjectCode, string? ClientName, string? ProjectId = null);

/// <summary>A registration of time as the Forecast server describes it.</summary>
public sealed record ForecastRemoteEntry(
    string? TaskId, string? TaskCode, DateOnly Date, decimal Hours, string? PersonId, string? ProjectId = null);

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

            tasks.Add(new ForecastRemoteTask(id, code, StripCode(title, code), StripCode(projectName, projectCode), projectCode, clientName, projectId));
        }
        return tasks;
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

            entries.Add(new ForecastRemoteEntry(taskId, taskCode, date.Value, hours.Value, personId, projectId));
        }
        return entries;
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
        if (Get(item, "hours", "hoursregistered", "hourstime", "totalhours") is { } h && Number(h) is { } hours)
            return hours;
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
}
