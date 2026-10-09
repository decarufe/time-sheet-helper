using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Collectors.DevOps.LocalGit;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests.DevOps;

public sealed class LocalGitCollectorTests : IDisposable
{
    private static readonly DateTime From = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsh-localgit-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _sink = new();

    public LocalGitCollectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeRepo(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    [Theory]
    [InlineData("https://github.com/acme/api.git", "acme/api", "acme", "https://github.com/acme/api/commit/")]
    [InlineData("git@github.com:acme/api.git", "acme/api", "acme", "https://github.com/acme/api/commit/")]
    [InlineData("ssh://git@github.com/acme/api", "acme/api", "acme", "https://github.com/acme/api/commit/")]
    [InlineData("https://contoso@dev.azure.com/contoso/Time%20Sheets/_git/timesheet-api", "timesheet-api", "contoso/Time Sheets", "https://dev.azure.com/contoso/Time%20Sheets/_git/timesheet-api/commit/")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Timesheets/timesheet-api", "timesheet-api", "contoso/Timesheets", "https://dev.azure.com/contoso/Timesheets/_git/timesheet-api/commit/")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Timesheets/_git/timesheet-api", "timesheet-api", "contoso/Timesheets", "https://dev.azure.com/contoso/Timesheets/_git/timesheet-api/commit/")]
    [InlineData("https://git.example.com/team/tool.git", "team/tool", "team", null)]
    public void Remote_urls_are_normalized(string url, string repo, string project, string? commitPrefix)
    {
        var remote = GitRemote.Parse(url);

        Assert.NotNull(remote);
        Assert.Equal(repo, remote.Repo);
        Assert.Equal(project, remote.Project);
        Assert.Equal(commitPrefix, remote.CommitUrlPrefix);
    }

    [Fact]
    public void Git_log_output_is_parsed()
    {
        var output = "a1b2c3d4e5\u001f2026-10-06T10:15:00-04:00\u001fAdd export\u001e\n"
                   + "ffeeddccbb\u001f2026-10-07T18:30:00+00:00\u001fSubject with \u001f separator?\u001e\n"
                   + "garbage\u001e";

        var commits = GitLogParser.Parse(output).ToList();

        Assert.Equal(2, commits.Count);
        Assert.Equal(new GitCommit("a1b2c3d4e5", new DateTime(2026, 10, 6, 14, 15, 0, DateTimeKind.Utc), "Add export"), commits[0]);
        Assert.Equal("ffeeddccbb", commits[1].Sha);
    }

    [Fact]
    public void Scan_finds_repositories_two_levels_deep_and_expands_wildcards()
    {
        var direct = MakeRepo("git-work", "api");
        var nested = MakeRepo("git-work", "client", "web");
        MakeRepo("git-work", "a", "b", "too-deep");
        MakeRepo("other", "ignored");
        var inner = Path.Combine(direct, "src", "nested-repo");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        var found = RepositoryLocator.Scan([Path.Combine(_root, "git*")], depth: 2).ToList();

        Assert.Equal([direct, nested], found.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repository_root_is_found_from_a_file_path()
    {
        var repo = MakeRepo("projects", "api");
        var file = Path.Combine(repo, "src", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");

        Assert.Equal(repo, RepositoryLocator.FindRepositoryRoot(file));
        Assert.Null(RepositoryLocator.FindRepositoryRoot("api"));
    }

    [Fact]
    public async Task Sync_emits_commits_from_scanned_and_focused_repositories()
    {
        var scanned = MakeRepo("git", "api");
        var focused = MakeRepo("elsewhere", "web");
        var focusedFile = Path.Combine(focused, "src", "app.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(focusedFile)!);
        File.WriteAllText(focusedFile, "");

        var processes = new FakeProcessRunner((file, args) =>
        {
            if (args is ["--version"])
            {
                return FakeProcessRunner.Ok("git version 2.50.0");
            }
            var repo = args[1];
            return args.Skip(2).ToArray() switch
            {
                ["config", "user.email"] => FakeProcessRunner.Ok("eric@example.com\n"),
                ["config", "--get", "remote.origin.url"] when repo == scanned => FakeProcessRunner.Ok("git@github.com:acme/api.git\n"),
                ["config", "--get", "remote.origin.url"] => FakeProcessRunner.Fail(),
                ["log", ..] when repo == scanned => FakeProcessRunner.Ok(
                    "1111111111111111111111111111111111111111\u001f2026-10-06T10:00:00-04:00\u001fFirst\u001e\n" +
                    "2222222222222222222222222222222222222222\u001f2026-10-01T10:00:00-04:00\u001fBefore range (rebased later)\u001e\n"),
                ["log", ..] => FakeProcessRunner.Ok(
                    "1111111111111111111111111111111111111111\u001f2026-10-06T10:00:00-04:00\u001fFirst\u001e\n" +
                    "3333333333333333333333333333333333333333\u001f2026-10-08T09:00:00Z\u001fSecond\u001e\n"),
                _ => FakeProcessRunner.Fail(),
            };
        });
        var history = new FakeActivityHistory(
            new ActivityEvent { Source = ActivitySource.Focus, FilePath = focusedFile, StartUtc = From.AddDays(1) },
            new ActivityEvent { Source = ActivitySource.Focus, Folder = "web", StartUtc = From.AddDays(1) },
            new ActivityEvent { Source = ActivitySource.Calendar, Folder = scanned, StartUtc = From.AddDays(1) });
        var collector = new LocalGitCollector(processes, _sink, history,
            Options.Create(new LocalGitOptions { Roots = [Path.Combine(_root, "git*")] }), NullLogger<LocalGitCollector>.Instance);

        var result = await collector.SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.EventCount);
        var first = _sink.Events.Single(e => e.ExternalId == "1111111111111111111111111111111111111111");
        Assert.Equal(ActivitySource.LocalGit, first.Source);
        Assert.Equal("commit", first.Kind);
        Assert.Equal(new DateTime(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc), first.StartUtc);
        var second = _sink.Events.Single(e => e.ExternalId == "3333333333333333333333333333333333333333");
        Assert.Equal("web", second.Repo);
        Assert.Equal(focused, second.Folder);
        Assert.Null(second.Url);

        var log = Assert.Single(processes.Calls, c => c.Contains($"-C {scanned} log"));
        Assert.Contains("--author=eric@example.com", log);
        Assert.Contains("--fixed-strings", log);
        Assert.Contains("--since=2026-10-05T00:00:00Z", log);
    }

    [Fact]
    public void Commit_activity_uses_normalized_remote()
    {
        var activity = LocalGitCollector.ToActivity(
            new GitCommit("abc123", From, "Subject"), @"C:\git\api", GitRemote.Parse("https://github.com/acme/api"));

        Assert.Equal("acme/api", activity.Repo);
        Assert.Equal("acme", activity.Project);
        Assert.Equal(@"C:\git\api", activity.Folder);
        Assert.Equal("https://github.com/acme/api/commit/abc123", activity.Url);
        Assert.Equal("abc123", activity.ExternalId);
    }

    [Fact]
    public async Task Missing_git_is_reported()
    {
        var collector = new LocalGitCollector(new FakeProcessRunner((_, _) => null), _sink, new FakeActivityHistory(),
            Options.Create(new LocalGitOptions()), NullLogger<LocalGitCollector>.Instance);

        var status = await collector.GetStatusAsync();
        var result = await collector.SyncAsync(From, To);

        Assert.False(status.IsConfigured);
        Assert.Contains("git introuvable", status.Message);
        Assert.Contains("git introuvable", Assert.Single(result.Warnings));
    }
}
