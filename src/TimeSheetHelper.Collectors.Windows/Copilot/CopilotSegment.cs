namespace TimeSheetHelper.Collectors.Windows.Copilot;

/// <summary>A burst of activity in one Copilot conversation, without a gap longer than the split threshold.</summary>
/// <param name="Client">Where the conversation happened: "vscode", "vscode-insiders" or "cli".</param>
public sealed record CopilotSegment(
    string SessionId,
    DateTime StartUtc,
    DateTime EndUtc,
    string? Folder,
    string? Repository,
    string? Branch,
    string Client);

/// <summary>The envelope of one Copilot conversation: its activity timestamps and the project it ran in.</summary>
public sealed record CopilotSession(
    string SessionId,
    IReadOnlyList<DateTime> TimestampsUtc,
    string? Folder = null,
    string? Repository = null,
    string? Branch = null);

public static class CopilotSegmenter
{
    public static readonly TimeSpan DefaultSplitGap = TimeSpan.FromMinutes(10);

    /// <summary>Splits the session's timestamps into segments wherever two consecutive ones are more than <paramref name="splitGap"/> apart.</summary>
    public static IReadOnlyList<CopilotSegment> Split(CopilotSession session, string client, TimeSpan splitGap)
    {
        if (session.TimestampsUtc.Count == 0)
            return [];

        var sorted = session.TimestampsUtc.Order().ToList();
        var segments = new List<CopilotSegment>();
        var start = sorted[0];
        var last = sorted[0];
        foreach (var timestamp in sorted.Skip(1))
        {
            if (timestamp - last > splitGap)
            {
                segments.Add(Segment(start, last));
                start = timestamp;
            }
            last = timestamp;
        }
        segments.Add(Segment(start, last));
        return segments;

        CopilotSegment Segment(DateTime from, DateTime to) =>
            new(session.SessionId, from, to, session.Folder, session.Repository, session.Branch, client);
    }
}
