using System.Globalization;
using System.Text.Json;

namespace TimeSheetHelper.Forecast;

/// <summary>Values a tool call may need; the builder places each one under the parameter of the tool that fits it.</summary>
public sealed record ForecastArgumentValues
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public DateOnly? Date { get; init; }
    public string? PersonId { get; init; }
    public string? TaskId { get; init; }
    public decimal? Hours { get; init; }
    public string? Notes { get; init; }
}

public sealed record ForecastArguments(IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string> MissingRequired)
{
    public bool IsComplete => MissingRequired.Count == 0;
}

/// <summary>
/// Maps what we know (dates, person, task, hours, notes) onto the parameters declared in a tool's JSON schema,
/// recognising them by name. Parameters it does not know are left out; required ones it cannot fill are reported.
/// </summary>
public static class ForecastArgumentBuilder
{
    public static ForecastArguments Build(ForecastToolInfo tool, ForecastArgumentValues values)
    {
        var args = new Dictionary<string, object?>();
        var schema = tool.InputSchema;
        var required = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToHashSet()
            : [];

        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (Value(prop.Name, prop.Value, values) is { } value)
                    args[prop.Name] = value;
            }
        }

        return new ForecastArguments(args, required.Where(name => !args.ContainsKey(name)).ToList());
    }

    /// <summary>True when the tool can list a date range in one call (as opposed to one day at a time).</summary>
    public static bool AcceptsDateRange(ForecastToolInfo tool) =>
        tool.ParameterNames.Select(Normalize).Any(n => Role(n) == ParameterRole.From);

    private enum ParameterRole { None, From, To, Date, Person, Task, Hours, Notes }

    private static object? Value(string name, JsonElement definition, ForecastArgumentValues v)
    {
        var normalized = Normalize(name);
        switch (Role(normalized))
        {
            case ParameterRole.From:
                return Date(v.From, definition);
            case ParameterRole.To:
                return Date(v.To, definition);
            case ParameterRole.Date:
                return Date(v.Date ?? v.From, definition);
            case ParameterRole.Person:
                return Id(v.PersonId, definition);
            case ParameterRole.Task:
                return Id(v.TaskId, definition);
            case ParameterRole.Notes:
                return v.Notes;
            case ParameterRole.Hours when v.Hours is { } hours:
                return Duration(hours, normalized, definition);
            default:
                return null;
        }
    }

    private static ParameterRole Role(string n)
    {
        if (n is "from" or "start" or "startdate" or "datefrom" or "fromdate" or "after" or "since" or "begin" or "begindate" or "startday" or "weekstart"
            || n.StartsWith("startdate") || n.StartsWith("fromdate"))
            return ParameterRole.From;
        if (n is "to" or "end" or "enddate" or "dateto" or "todate" or "until" or "before" or "endday" or "weekend"
            || n.StartsWith("enddate") || n.StartsWith("todate"))
            return ParameterRole.To;
        if (n is "date" or "day" or "workdate" or "entrydate" or "registrationdate" or "timeregistrationdate" or "dateregistered")
            return ParameterRole.Date;
        if (n is "person" or "user" or "assignee" or "member" or "employee" or "resource"
            || (n.EndsWith("id") && (n.Contains("person") || n.Contains("user") || n.Contains("assignee") || n.Contains("member") || n.Contains("employee"))))
            return ParameterRole.Person;
        if (n is "task" or "card" or "ticket" || (n.EndsWith("id") && (n.Contains("task") || n.Contains("card") || n.Contains("ticket"))))
            return ParameterRole.Task;
        if (n is "hours" or "hour" or "duration" or "timeregistered" or "minutes" or "minute" or "seconds" or "timespent" or "time" or "amount" or "quantity"
            || n.StartsWith("hours") || n.StartsWith("minutesregistered"))
            return ParameterRole.Hours;
        if (n is "notes" or "note" or "comment" or "comments" or "description" or "text" or "message")
            return ParameterRole.Notes;
        return ParameterRole.None;
    }

    private static object? Date(DateOnly? date, JsonElement definition)
    {
        if (date is null)
            return null;
        var text = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Type(definition) is "integer" or "number" ? null : text;
    }

    private static object? Id(string? id, JsonElement definition)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        return Type(definition) is "integer" or "number" && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : id;
    }

    /// <summary>Hours, unless the parameter name or its description says minutes or seconds (Forecast stores registered time in minutes).</summary>
    private static object Duration(decimal hours, string normalizedName, JsonElement definition)
    {
        var description = definition.ValueKind == JsonValueKind.Object && definition.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var factor = normalizedName.Contains("second") || description.Contains("second", StringComparison.OrdinalIgnoreCase) ? 3600m
            : normalizedName.Contains("minute") || normalizedName == "timeregistered" || description.Contains("minute", StringComparison.OrdinalIgnoreCase) ? 60m
            : 1m;
        var amount = hours * factor;
        return Type(definition) switch
        {
            "string" => amount.ToString("0.##", CultureInfo.InvariantCulture),
            "integer" => (long)Math.Round(amount),
            _ => amount,
        };
    }

    private static string? Type(JsonElement definition) =>
        definition.ValueKind == JsonValueKind.Object && definition.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    internal static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
