using System.Globalization;
using System.Text.Json;

namespace TimeSheetHelper.Collectors.Windows.ClaudeCode;

/// <summary>A run of transcript messages from one session without a gap longer than the split threshold.</summary>
public sealed record ClaudeCodeSegment(string SessionId, DateTime StartUtc, DateTime EndUtc, string? Cwd, string? GitBranch);

/// <summary>
/// Parses Claude Code transcripts (one JSON object per line, under ~/.claude/projects/&lt;project&gt;/&lt;session&gt;.jsonl).
/// Only the envelope is read (type, timestamp, sessionId, cwd, gitBranch); message content is never retained.
/// Lines without a timestamp (titles, settings, snapshots) or that are not valid JSON are skipped.
/// </summary>
public static class ClaudeCodeTranscriptParser
{
    public static readonly TimeSpan DefaultSplitGap = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> ConversationTypes = new(StringComparer.Ordinal) { "user", "assistant", "system", "attachment" };

    public static IReadOnlyList<ClaudeCodeSegment> Parse(IEnumerable<string> lines, TimeSpan splitGap, string? fallbackSessionId = null)
    {
        var bySession = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (TryReadEntry(line, fallbackSessionId) is not { } entry)
                continue;
            if (!bySession.TryGetValue(entry.SessionId, out var list))
                bySession[entry.SessionId] = list = [];
            list.Add(entry);
        }

        var segments = new List<ClaudeCodeSegment>();
        foreach (var (sessionId, entries) in bySession)
        {
            entries.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

            var start = entries[0];
            var last = entries[0];
            string? branch = start.GitBranch;
            foreach (var entry in entries.Skip(1))
            {
                if (entry.TimestampUtc - last.TimestampUtc > splitGap)
                {
                    segments.Add(new ClaudeCodeSegment(sessionId, start.TimestampUtc, last.TimestampUtc, start.Cwd, branch));
                    start = entry;
                    branch = null;
                }
                last = entry;
                branch = entry.GitBranch ?? branch;
            }
            segments.Add(new ClaudeCodeSegment(sessionId, start.TimestampUtc, last.TimestampUtc, start.Cwd, branch));
        }

        segments.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return segments;
    }

    private static Entry? TryReadEntry(string line, string? fallbackSessionId)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"timestamp\"", StringComparison.Ordinal))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (GetString(root, "type") is not { } type || !ConversationTypes.Contains(type))
                return null;

            if (GetString(root, "timestamp") is not { } rawTimestamp
                || !DateTime.TryParse(rawTimestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var timestamp))
                return null;

            var sessionId = GetString(root, "sessionId") ?? fallbackSessionId;
            if (sessionId is null)
                return null;

            return new Entry(sessionId, timestamp, GetString(root, "cwd"), GetString(root, "gitBranch"));
        }
        catch (JsonException)
        {
            // A line being written concurrently may be truncated.
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    private sealed record Entry(string SessionId, DateTime TimestampUtc, string? Cwd, string? GitBranch);
}
