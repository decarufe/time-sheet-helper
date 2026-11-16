using System.Text.Json;

namespace TimeSheetHelper.Collectors.Windows.Copilot;

/// <summary>
/// Reads the Copilot Chat sessions VS Code keeps under <c>User\workspaceStorage\&lt;hash&gt;\chatSessions</c>.
/// The format is internal and changes between versions: since 1.109 a <c>.jsonl</c> log of patches
/// (<c>{"kind":0,"v":snapshot}</c>, then set/push operations on paths), before that a single <c>.json</c> document.
/// Rather than replaying the patches, every line is scanned for the epoch-millisecond fields VS Code stamps on
/// requests, responses and tool calls; nothing else is read, so prompt and response text is never retained.
/// </summary>
public static partial class VsCodeChatSessionParser
{
    private static readonly string[] TimestampKeys = ["timestamp", "startedAt", "completedAt", "responseTimestamp", "creationDate"];

    // Older than any Copilot Chat session; guards against small numbers sharing a key name.
    private static readonly DateTime MinPlausibleUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Parses a <c>.jsonl</c> session (one patch per line); truncated or invalid lines are skipped.</summary>
    public static CopilotSession ParseLines(IEnumerable<string> lines, string fallbackSessionId, DateTime? nowUtc = null)
    {
        var maxUtc = (nowUtc ?? DateTime.UtcNow).AddDays(1);
        var timestamps = new List<DateTime>();
        string? sessionId = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || !MayContainTimestamp(line))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                // The initial snapshot carries the session id.
                if (sessionId is null && root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Number && kind.GetInt32() == 0
                    && root.TryGetProperty("v", out var snapshot))
                {
                    sessionId = GetString(snapshot, "sessionId");
                }
                Collect(root, timestamps, maxUtc);
            }
            catch (JsonException)
            {
                // VS Code may be appending this line right now.
            }
        }

        return new CopilotSession(sessionId ?? fallbackSessionId, timestamps);
    }

    /// <summary>Parses a legacy <c>.json</c> session (a single document).</summary>
    public static CopilotSession ParseDocument(string json, string fallbackSessionId, DateTime? nowUtc = null)
    {
        var timestamps = new List<DateTime>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Collect(doc.RootElement, timestamps, (nowUtc ?? DateTime.UtcNow).AddDays(1));
            return new CopilotSession(GetString(doc.RootElement, "sessionId") ?? fallbackSessionId, timestamps);
        }
        catch (JsonException)
        {
            return new CopilotSession(fallbackSessionId, []);
        }
    }

    /// <summary>
    /// Reads the folder a workspace storage belongs to from its <c>workspace.json</c>: <c>folder</c> for a folder,
    /// <c>workspace</c> for a .code-workspace file (its directory is used). Remote workspaces have no local folder.
    /// </summary>
    public static string? ReadWorkspaceFolder(string workspaceJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(workspaceJson);
            if (ToLocalPath(GetString(doc.RootElement, "folder")) is { } folder)
                return folder;
            return ToLocalPath(GetString(doc.RootElement, "workspace")) is { } workspaceFile
                ? Path.GetDirectoryName(workspaceFile)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ToLocalPath(string? uri)
    {
        if (uri is null || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        // VS Code escapes the drive colon (file:///c%3A/...), which Uri does not read as a drive letter.
        var unescapedDrive = DriveColonRegex().Replace(uri, "$1:");
        if (!Uri.TryCreate(unescapedDrive, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            return null;

        var path = Path.TrimEndingDirectorySeparator(parsed.LocalPath);
        // Other sources record "C:\..."; VS Code lowercases the drive letter.
        return path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + path[1..] : path;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^(file:///[A-Za-z])%3[Aa]")]
    private static partial System.Text.RegularExpressions.Regex DriveColonRegex();

    private static bool MayContainTimestamp(string line)
    {
        foreach (var key in TimestampKeys)
        {
            if (line.Contains($"\"{key}\"", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void Collect(JsonElement element, List<DateTime> timestamps, DateTime maxUtc)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (IsTimestampKey(property.Name) && property.Value.TryGetInt64(out var ms)
                            && FromEpochMs(ms) is { } at && at >= MinPlausibleUtc && at <= maxUtc)
                        {
                            timestamps.Add(at);
                        }
                    }
                    else
                    {
                        Collect(property.Value, timestamps, maxUtc);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, timestamps, maxUtc);
                break;
        }
    }

    private static bool IsTimestampKey(string name) => Array.IndexOf(TimestampKeys, name) >= 0;

    private static DateTime? FromEpochMs(long ms)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;
}
