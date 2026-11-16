using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.Collectors.Windows.Copilot;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests;

public class CopilotCollectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);

    private static long Ms(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();

    // Synthetic lines shaped like VS Code chatSessions patches (contents are placeholders).
    private static string VsSnapshot(DateTime created, string session = "vs-1") =>
        $$$"""{"kind":0,"v":{"version":3,"creationDate":{{{Ms(created)}}},"sessionId":"{{{session}}}","requests":[],"inputState":{"inputText":"placeholder","selections":[{"startLineNumber":2}]} } }""";

    private static string VsRequest(DateTime at, DateTime toolStart, DateTime toolEnd) =>
        $$$"""{"kind":2,"k":["requests"],"v":[{"requestId":"r","timestamp":{{{Ms(at)}}},"timeSpentWaiting":{{{Ms(at.AddHours(9))}}},"message":{"text":"placeholder"},"response":[{"kind":"toolInvocation","startedAt":{{{Ms(toolStart)}}},"completedAt":{{{Ms(toolEnd)}}} }]}]}""";

    private static string VsCompleted(DateTime at) =>
        $$$"""{"kind":1,"k":["requests",0,"modelState"],"v":{"value":1,"completedAt":{{{Ms(at)}}} } }""";

    // Synthetic lines shaped like Copilot CLI events.jsonl.
    private static string CliStart(DateTime at, string session = "cli-1", string cwd = @"C:\\src\\billing", string repository = "contoso/billing", string branch = "main") =>
        $$$"""{"type":"session.start","data":{"sessionId":"{{{session}}}","context":{"cwd":"{{{cwd}}}","repository":"{{{repository}}}","branch":"{{{branch}}}"}},"id":"e0","timestamp":"{{{at:O}}}","parentId":null}""";

    private static string CliEvent(string type, DateTime at) =>
        $$$"""{"type":"{{{type}}}","data":{"content":"placeholder"},"id":"e","timestamp":"{{{at:O}}}"}""";

    [Fact]
    public void Segmenter_splits_on_gaps_longer_than_the_threshold()
    {
        var session = new CopilotSession("s", [T0.AddMinutes(10), T0, T0.AddMinutes(20).AddSeconds(1), T0.AddMinutes(25)], @"C:\src\a");

        var segments = CopilotSegmenter.Split(session, "vscode", Gap);

        Assert.Collection(segments,
            s => { Assert.Equal(T0, s.StartUtc); Assert.Equal(T0.AddMinutes(10), s.EndUtc); },
            s => { Assert.Equal(T0.AddMinutes(20).AddSeconds(1), s.StartUtc); Assert.Equal(T0.AddMinutes(25), s.EndUtc); });
        Assert.All(segments, s => Assert.Equal(@"C:\src\a", s.Folder));
    }

    [Fact]
    public void VsCode_patches_yield_request_tool_and_completion_timestamps()
    {
        string[] lines =
        [
            VsSnapshot(T0),
            """{"kind":1,"k":["inputState","inputText"],"v":"placeholder"}""",
            VsRequest(T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(4)),
            "{ truncated",
            VsCompleted(T0.AddMinutes(5)),
            """{"kind":1,"k":["requests",0,"elapsedMs"],"v":12345}""",
        ];

        var session = VsCodeChatSessionParser.ParseLines(lines, "fallback", T0.AddDays(1));

        Assert.Equal("vs-1", session.SessionId);
        // timeSpentWaiting is not a timestamp; elapsedMs and selections are ignored.
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(4), T0.AddMinutes(5)], session.TimestampsUtc.Order());
    }

    [Fact]
    public void VsCode_implausible_numbers_are_ignored_and_file_name_is_the_fallback_id()
    {
        string[] lines = ["""{"kind":1,"k":["x"],"v":{"timestamp":42,"startedAt":99999999999999}}"""];

        var session = VsCodeChatSessionParser.ParseLines(lines, "from-file", T0);

        Assert.Equal("from-file", session.SessionId);
        Assert.Empty(session.TimestampsUtc);
    }

    [Fact]
    public void VsCode_legacy_document_is_read()
    {
        var json = $$$"""
            {
              "version": 2,
              "sessionId": "legacy-1",
              "creationDate": {{{Ms(T0)}}},
              "requests": [ { "timestamp": {{{Ms(T0.AddMinutes(3))}}}, "message": { "text": "placeholder" } } ]
            }
            """;

        var session = VsCodeChatSessionParser.ParseDocument(json, "fallback", T0.AddDays(1));

        Assert.Equal("legacy-1", session.SessionId);
        Assert.Equal([T0, T0.AddMinutes(3)], session.TimestampsUtc.Order());
        Assert.Empty(VsCodeChatSessionParser.ParseDocument("{ broken", "x").TimestampsUtc);
    }

    [Theory]
    [InlineData("""{"folder":"file:///c%3A/git/Equipe%20Laurence"}""", @"C:\git\Equipe Laurence")]
    [InlineData("""{"workspace":"file:///c%3A/git/mono/all.code-workspace"}""", @"C:\git\mono")]
    [InlineData("""{"folder":"vscode-remote://wsl%2Bubuntu/home/me/app"}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""not json""", null)]
    public void VsCode_workspace_folder_is_read_from_workspace_json(string json, string? expected)
    {
        var folder = VsCodeChatSessionParser.ReadWorkspaceFolder(json);

        if (expected is null)
            Assert.Null(folder);
        else
            Assert.Equal(expected, folder, ignoreCase: true);
    }

    [Fact]
    public void Cli_keeps_conversation_events_and_the_project_context()
    {
        string[] lines =
        [
            CliStart(T0),
            CliEvent("session.model_change", T0.AddMinutes(30)),
            CliEvent("user.message", T0.AddMinutes(1)),
            CliEvent("assistant.turn_start", T0.AddMinutes(2)),
            CliEvent("tool.execution_complete", T0.AddMinutes(3)),
            CliEvent("session.autopilot_objective_changed", T0.AddHours(2)),
            CliEvent("session.usage_record", T0.AddHours(3)),
            "{ truncated",
        ];

        var session = CopilotCliEventsParser.Parse(lines, "dir-name");

        Assert.Equal("cli-1", session.SessionId);
        Assert.Equal(@"C:\src\billing", session.Folder);
        Assert.Equal("contoso/billing", session.Repository);
        Assert.Equal("main", session.Branch);
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(3)], session.TimestampsUtc.Order());
    }

    [Fact]
    public void Cli_workspace_yaml_is_the_fallback_for_the_project()
    {
        string[] yaml =
        [
            "id: cli-2",
            @"cwd: C:\git\slim",
            "repository: NexusInnovation/slim",
            "branch: feature/x",
            "summary: |",
            "  cwd: not-this-one",
            "mc_pending_repository_warning: null",
        ];

        var (folder, repository, branch) = CopilotCliEventsParser.ReadWorkspaceYaml(yaml);

        Assert.Equal(@"C:\git\slim", folder);
        Assert.Equal("NexusInnovation/slim", repository);
        Assert.Equal("feature/x", branch);
    }

    [Theory]
    [InlineData("contoso/billing", "contoso/billing")]
    [InlineData("org/project/repo", "repo")]
    [InlineData("repo", "repo")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Cli_repository_is_normalized_like_git_remotes(string? repository, string? expected) =>
        Assert.Equal(expected, CopilotCollector.NormalizeRepository(repository));

    [Fact]
    public void Activity_holds_no_conversation_text()
    {
        var segment = new CopilotSegment("s-1", T0, T0.AddMinutes(5), @"C:\nowhere\billing\", "contoso/billing", "main", "copilot-cli");

        var activity = CopilotCollector.ToActivity(segment);

        Assert.Equal(ActivitySource.Copilot, activity.Source);
        Assert.Equal("ai-session", activity.Kind);
        Assert.Equal("copilot-cli", activity.ProcessName);
        Assert.Equal("billing", activity.Subject);
        Assert.Equal("contoso/billing", activity.Repo);
        Assert.Equal("main", activity.Title);
        Assert.Equal("s-1@2026-10-05T13:00:00.0000000Z", activity.ExternalId);
    }

    [Fact]
    public async Task Sync_reads_vscode_insiders_and_cli_in_range_and_counts_a_session_once()
    {
        var root = Directory.CreateTempSubdirectory("tsh-copilot-");
        try
        {
            // VS Code stable: a folder workspace with a session in range and one before it.
            var stable = Path.Combine(root.FullName, "Code", "User");
            var ws = Directory.CreateDirectory(Path.Combine(stable, "workspaceStorage", "hash1", "chatSessions"));
            await File.WriteAllTextAsync(Path.Combine(ws.Parent!.FullName, "workspace.json"), """{"folder":"file:///c%3A/nowhere/billing"}""");
            await File.WriteAllLinesAsync(Path.Combine(ws.FullName, "vs-1.jsonl"),
                [VsSnapshot(T0.AddHours(3)), VsRequest(T0.AddHours(3).AddMinutes(1), T0.AddHours(3).AddMinutes(2), T0.AddHours(3).AddMinutes(6))]);
            await File.WriteAllLinesAsync(Path.Combine(ws.FullName, "old.jsonl"), [VsSnapshot(T0, "old"), VsCompleted(T0.AddMinutes(5))]);
            // Transcripts duplicate chatSessions and are not read.
            var transcripts = Directory.CreateDirectory(Path.Combine(ws.Parent!.FullName, "GitHub.copilot-chat", "transcripts"));
            await File.WriteAllLinesAsync(Path.Combine(transcripts.FullName, "vs-1.jsonl"), [CliEvent("user.message", T0.AddHours(3))]);

            // VS Code Insiders: a remote workspace (ignored) and a legacy .json session.
            var insiders = Path.Combine(root.FullName, "Code - Insiders", "User");
            var remote = Directory.CreateDirectory(Path.Combine(insiders, "workspaceStorage", "remote", "chatSessions"));
            await File.WriteAllTextAsync(Path.Combine(remote.Parent!.FullName, "workspace.json"), """{"folder":"vscode-remote://wsl%2Bubuntu/home/me"}""");
            await File.WriteAllLinesAsync(Path.Combine(remote.FullName, "r.jsonl"), [VsSnapshot(T0.AddHours(3), "r"), VsCompleted(T0.AddHours(3).AddMinutes(4))]);
            var legacy = Directory.CreateDirectory(Path.Combine(insiders, "workspaceStorage", "hash2", "chatSessions"));
            await File.WriteAllTextAsync(Path.Combine(legacy.Parent!.FullName, "workspace.json"), """{"folder":"file:///c%3A/nowhere/docs"}""");
            await File.WriteAllTextAsync(Path.Combine(legacy.FullName, "legacy.json"),
                $$$"""{"sessionId":"legacy","creationDate":{{{Ms(T0.AddHours(2).AddMinutes(30))}}},"requests":[{"timestamp":{{{Ms(T0.AddHours(2).AddMinutes(40))}}} }]}""");

            // Copilot CLI: one session, plus one reusing a VS Code session id (counted once).
            var cli = Path.Combine(root.FullName, "session-state");
            var s1 = Directory.CreateDirectory(Path.Combine(cli, "cli-1"));
            await File.WriteAllLinesAsync(Path.Combine(s1.FullName, "events.jsonl"),
                [CliStart(T0.AddHours(3)), CliEvent("user.message", T0.AddHours(3).AddMinutes(1)), CliEvent("assistant.turn_end", T0.AddHours(3).AddMinutes(8))]);
            var dup = Directory.CreateDirectory(Path.Combine(cli, "vs-1"));
            await File.WriteAllLinesAsync(Path.Combine(dup.FullName, "events.jsonl"),
                [CliStart(T0.AddHours(3), session: "vs-1"), CliEvent("user.message", T0.AddHours(3).AddMinutes(2))]);
            // A session folder without an event log is skipped.
            Directory.CreateDirectory(Path.Combine(cli, "empty"));

            var sink = new ListSink();
            var collector = new CopilotCollector(sink, NullLogger<CopilotCollector>.Instance,
                [new(stable, "vscode"), new(insiders, "vscode-insiders")], cli);

            var result = await collector.SyncAsync(T0.AddHours(2), T0.AddHours(4));

            Assert.Empty(result.Warnings);
            Assert.Equal(3, result.EventCount);
            var bySession = sink.Written.ToDictionary(a => a.ExternalId!.Split('@')[0]);
            Assert.Equal(["cli-1", "legacy", "vs-1"], bySession.Keys.Order());
            Assert.Equal("copilot-cli", bySession["cli-1"].ProcessName);
            Assert.Equal("contoso/billing", bySession["cli-1"].Repo);
            Assert.Equal("vscode-insiders", bySession["legacy"].ProcessName);
            Assert.Equal("docs", bySession["legacy"].Subject);
            // The CLI copy of vs-1 is read first and wins.
            Assert.Equal("copilot-cli", bySession["vs-1"].ProcessName);
            Assert.All(sink.Written, a => Assert.Equal(ActivitySource.Copilot, a.Source));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Status_reports_when_no_copilot_folder_exists()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var collector = new CopilotCollector(new ListSink(), NullLogger<CopilotCollector>.Instance,
            [new(Path.Combine(missing, "Code", "User"), "vscode")], Path.Combine(missing, "session-state"));

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
