using System.Globalization;
using System.Text.Json;

namespace TimeSheetHelper.Collectors.DevOps.Common;

/// <summary>Null-tolerant navigation helpers over API responses.</summary>
internal static class JsonExtensions
{
    public static JsonElement? Path(this JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }
            current = next;
        }
        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }

    public static string? Str(this JsonElement element, params string[] path) => element.Path(path) switch
    {
        { ValueKind: JsonValueKind.String } e => e.GetString(),
        { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    public static long? Long(this JsonElement element, params string[] path)
        => element.Path(path) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var value) ? value : null;

    /// <summary>Parses an ISO-8601 timestamp (any offset) to UTC.</summary>
    public static DateTime? Utc(this JsonElement element, params string[] path)
        => ParseUtc(element.Str(path));

    public static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return null;
        }
        return parsed.UtcDateTime;
    }

    public static IEnumerable<JsonElement> Array(this JsonElement element, params string[] path)
        => element.Path(path) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.AsSpan().Trim();
        var newline = line.IndexOfAny('\r', '\n');
        return (newline >= 0 ? line[..newline] : line).Trim().ToString();
    }

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
