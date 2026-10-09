using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.Collectors.Windows.ClaudeCode;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests;

public class ClaudeCodeTranscriptTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);

    // Synthetic lines shaped like Claude Code transcripts (envelope fields only; contents are placeholders).
    private static string Line(string type, DateTime at, string session = "sess-1", string cwd = @"C:\\src\\billing", string branch = "main") =>
        $$$"""{"type":"{{{type}}}","timestamp":"{{{at.ToString("O")}}}","sessionId":"{{{session}}}","cwd":"{{{cwd}}}","gitBranch":"{{{branch}}}","isSidechain":false,"message":{"role":"{{{type}}}","content":"placeholder"}}""";

    [Fact]
    public void Messages_are_grouped_into_one_segment_per_burst()
    {
        string[] lines =
        [
            """{"type":"ai-title","aiTitle":"placeholder","sessionId":"sess-1"}""",
            Line("user", T0),
            Line("assistant", T0.AddMinutes(1)),
            Line("user", T0.AddMinutes(9)),
            Line("assistant", T0.AddMinutes(12), branch: "feature/x"),
            // 10 min 1 s later: new segment.
            Line("user", T0.AddMinutes(22).AddSeconds(1)),
            Line("assistant", T0.AddMinutes(25)),
        ];

        var segments = ClaudeCodeTranscriptParser.Parse(lines, TimeSpan.FromMinutes(10));

        Assert.Equal(2, segments.Count);
        Assert.Equal(T0, segments[0].StartUtc);
        Assert.Equal(T0.AddMinutes(12), segments[0].EndUtc);
        Assert.Equal("feature/x", segments[0].GitBranch);
        Assert.Equal(@"C:\src\billing", segments[0].Cwd);
        Assert.Equal(T0.AddMinutes(22).AddSeconds(1), segments[1].StartUtc);
        Assert.Equal(T0.AddMinutes(25), segments[1].EndUtc);
    }

    [Fact]
    public void Gap_of_exactly_ten_minutes_does_not_split()
    {
        string[] lines = [Line("user", T0), Line("assistant", T0.AddMinutes(10))];
        Assert.Single(ClaudeCodeTranscriptParser.Parse(lines, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Sessions_are_separated_and_invalid_lines_skipped()
    {
        string[] lines =
        [
            Line("user", T0, session: "a"),
            "{ truncated",
            "",
            Line("user", T0.AddMinutes(1), session: "b", cwd: @"C:\\src\\other"),
            Line("assistant", T0.AddMinutes(2), session: "a"),
            Line("assistant", T0.AddMinutes(3), session: "b", cwd: @"C:\\src\\other"),
            """{"type":"file-history-delta","timestamp":"2026-10-05T13:30:00.000Z","messageId":"x"}""",
        ];

        var segments = ClaudeCodeTranscriptParser.Parse(lines, TimeSpan.FromMinutes(10));

        Assert.Collection(segments,
            s => { Assert.Equal("a", s.SessionId); Assert.Equal(T0.AddMinutes(2), s.EndUtc); },
            s => { Assert.Equal("b", s.SessionId); Assert.Equal(@"C:\src\other", s.Cwd); });
    }

    [Fact]
    public void Activity_holds_no_prompt_text()
    {
        var segment = new ClaudeCodeSegment("sess-1", T0, T0.AddMinutes(5), @"C:\src\billing\", "main");
        var activity = ClaudeCodeTranscriptCollector.ToActivity(segment);

        Assert.Equal(ActivitySource.ClaudeCode, activity.Source);
        Assert.Equal("ai-session", activity.Kind);
        Assert.Equal("billing", activity.Subject);
        Assert.Equal(@"C:\src\billing\", activity.Folder);
        Assert.Equal("main", activity.Title);
        Assert.Equal("sess-1@2026-10-05T13:00:00.0000000Z", activity.ExternalId);
    }

    [Fact]
    public async Task Sync_reads_project_transcripts_in_range()
    {
        var root = Directory.CreateTempSubdirectory("tsh-claude-");
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root.FullName, "C--src-billing"));
            await File.WriteAllLinesAsync(Path.Combine(project.FullName, "sess-1.jsonl"),
            [
                Line("user", T0),
                Line("assistant", T0.AddMinutes(5)),
                Line("user", T0.AddHours(3)),
                Line("assistant", T0.AddHours(3).AddMinutes(2)),
            ]);
            // Subagent transcripts live one level deeper and are ignored.
            var nested = Directory.CreateDirectory(Path.Combine(project.FullName, "sess-1", "subagents"));
            await File.WriteAllLinesAsync(Path.Combine(nested.FullName, "agent.jsonl"), [Line("user", T0), Line("assistant", T0.AddMinutes(1))]);

            var sink = new ListSink();
            var collector = new ClaudeCodeTranscriptCollector(sink, NullLogger<ClaudeCodeTranscriptCollector>.Instance, root.FullName);

            var result = await collector.SyncAsync(T0.AddHours(2), T0.AddHours(4));

            Assert.Equal(1, result.EventCount);
            var activity = Assert.Single(sink.Written);
            Assert.Equal(T0.AddHours(3), activity.StartUtc);
            Assert.Equal("billing", activity.Subject);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Status_reports_missing_folder()
    {
        var collector = new ClaudeCodeTranscriptCollector(new ListSink(), NullLogger<ClaudeCodeTranscriptCollector>.Instance,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.False((await collector.GetStatusAsync()).IsConfigured);
    }

    private sealed class ListSink : IActivitySink
    {
        public List<ActivityEvent> Written { get; } = [];

        public Task WriteAsync(ActivityEvent activity, CancellationToken cancellationToken = default)
        {
            Written.Add(activity);
            return Task.CompletedTask;
        }
    }
}
