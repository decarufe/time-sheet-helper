using System.Globalization;
using System.Text.Json;

namespace TimeSheetHelper.Collectors.Windows.Copilot;

/// <summary>
/// Reads the event logs of Copilot CLI (also used by Copilot autopilot and the VS Code agent host):
/// <c>~\.copilot\session-state\&lt;session&gt;\events.jsonl</c>, one <c>{"type","timestamp","data"}</c> object per line.
/// Only the timestamps of conversation events are kept, plus the folder, repository and branch announced by
/// <c>session.start</c> / <c>session.resume</c>; message and tool content is never retained.
/// </summary>
public static class CopilotCliEventsParser
{
    // Conversation events; bookkeeping (usage records, autopilot objectives, assets, ...) can fire while nobody works.
    private static readonly string[] ActivityTypePrefixes = ["user.", "assistant.", "tool.", "subagent.", "permission.", "skill."];
    private static readonly HashSet<string> ActivityTypes = new(StringComparer.Ordinal) { "session.start", "session.resume", "abort" };

    public static CopilotSession Parse(IEnumerable<string> lines, string fallbackSessionId)
    {
        var timestamps = new List<DateTime>();
        string? sessionId = null, folder = null, repository = null, branch = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"timestamp\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") is not { } type || !IsActivity(type))
                    continue;

                if (type is "session.start" or "session.resume" && root.TryGetProperty("data", out var data))
                {
                    sessionId ??= GetString(data, "sessionId");
                    if (data.TryGetProperty("context", out var context))
                    {
                        folder = GetString(context, "cwd") ?? folder;
                        repository = GetString(context, "repository") ?? repository;
                        branch = GetString(context, "branch") ?? branch;
                    }
                }

                if (GetString(root, "timestamp") is { } raw
                    && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var timestamp))
                {
                    timestamps.Add(timestamp);
                }
            }
            catch (JsonException)
            {
                // The CLI may be appending this line right now.
            }
        }

        return new CopilotSession(sessionId ?? fallbackSessionId, timestamps, folder, repository, branch);
    }

    /// <summary>
    /// Reads <c>cwd</c>, <c>repository</c> and <c>branch</c> from the session's <c>workspace.yaml</c> (flat
    /// <c>key: value</c> lines), the fallback for logs without a <c>session.start</c> event.
    /// </summary>
    public static (string? Folder, string? Repository, string? Branch) ReadWorkspaceYaml(IEnumerable<string> lines)
    {
        string? folder = null, repository = null, branch = null;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
                continue;
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (value.Length == 0 || value == "null")
                continue;
            switch (line[..colon])
            {
                case "cwd": folder = value; break;
                case "repository": repository = value; break;
                case "branch": branch = value; break;
            }
        }
        return (folder, repository, branch);
    }

    private static bool IsActivity(string type)
    {
        if (ActivityTypes.Contains(type))
            return true;
        foreach (var prefix in ActivityTypePrefixes)
        {
            if (type.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;
}
