using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.DevOps.LocalGit;

/// <summary>
/// Commits authored by the configured git user in local repositories: those under the configured roots plus
/// those the user had open (Focus / VS Code activities) during the range.
/// </summary>
public sealed class LocalGitCollector(
    IProcessRunner processes,
    IActivitySink sink,
    IActivityHistory history,
    IOptions<LocalGitOptions> options,
    ILogger<LocalGitCollector> logger) : ICollector
{
    internal const string GitMissingMessage = "git introuvable : installez Git ou ajoutez-le au PATH.";
    private static readonly ActivitySource[] FolderSources = [ActivitySource.Focus, ActivitySource.VsCode, ActivitySource.ClaudeCode];

    public string Name => "localgit";

    public async Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await processes.RunAsync("git", ["--version"], cancellationToken: cancellationToken);
        return result is { Succeeded: true }
            ? new CollectorStatus(true)
            : new CollectorStatus(false, GitMissingMessage);
    }

    public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        if (await processes.RunAsync("git", ["--version"], cancellationToken: cancellationToken) is null)
        {
            return new CollectorResult(0, [GitMissingMessage]);
        }

        var warnings = new List<string>();
        var repositories = await FindRepositoriesAsync(fromUtc, toUtc, cancellationToken);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withoutEmail = new List<string>();
        var count = 0;

        foreach (var repository in repositories)
        {
            var email = await GitAsync(repository, ["config", "user.email"], cancellationToken);
            if (email is null)
            {
                withoutEmail.Add(Path.GetFileName(repository));
                continue;
            }

            var remote = GitRemote.Parse(await GitAsync(repository, ["config", "--get", "remote.origin.url"], cancellationToken));
            string[] arguments =
            [
                "log", "--all", "--fixed-strings", $"--author={email}",
                $"--since={JsonExtensions.Iso(fromUtc)}",
                // --since/--until filter on committer date; the slack keeps commits authored in range but rebased later.
                $"--until={JsonExtensions.Iso(toUtc.AddDays(31))}",
                $"--format={GitLogParser.Format}",
            ];
            var log = await GitAsync(repository, arguments, cancellationToken);
            if (log is null)
            {
                continue;
            }

            foreach (var commit in GitLogParser.Parse(log))
            {
                if (commit.AuthorDateUtc < fromUtc || commit.AuthorDateUtc >= toUtc || !emitted.Add(commit.Sha))
                {
                    continue;
                }
                await sink.WriteAsync(ToActivity(commit, repository, remote), cancellationToken);
                count++;
            }
        }

        if (withoutEmail.Count > 0)
        {
            warnings.Add($"Git : user.email non configuré pour {string.Join(", ", withoutEmail)}.");
        }
        return new CollectorResult(count, warnings);
    }

    internal static ActivityEvent ToActivity(GitCommit commit, string repository, GitRemote? remote) => new()
    {
        Source = ActivitySource.LocalGit,
        Kind = "commit",
        StartUtc = commit.AuthorDateUtc,
        Repo = remote?.Repo ?? Path.GetFileName(repository),
        Project = remote?.Project,
        Folder = repository,
        Subject = commit.Subject,
        Url = remote?.CommitUrlPrefix is { } prefix ? prefix + commit.Sha : null,
        ExternalId = commit.Sha,
    };

    internal async Task<IReadOnlyList<string>> FindRepositoriesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var repositories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in RepositoryLocator.Scan(settings.EffectiveRoots, settings.ScanDepth))
        {
            repositories.Add(repository);
        }

        var activities = await history.GetRangeAsync(fromUtc, toUtc, cancellationToken);
        var candidates = activities
            .Where(a => FolderSources.Contains(a.Source))
            .SelectMany(a => new[] { a.Folder, a.FilePath })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            if (RepositoryLocator.FindRepositoryRoot(path) is { } root)
            {
                repositories.Add(root);
            }
        }
        return repositories.ToList();
    }

    /// <summary>Runs git in the repository; returns trimmed stdout, or null on failure.</summary>
    private async Task<string?> GitAsync(string repository, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("git", ["-C", repository, .. arguments], cancellationToken: cancellationToken);
        if (result is not { Succeeded: true })
        {
            if (result is not null && arguments[0] == "log")
            {
                logger.LogDebug("git log failed in {Repository}: {Error}", repository, result.StandardError);
            }
            return null;
        }
        var output = result.StandardOutput.Trim();
        return output.Length == 0 && arguments[0] != "log" ? null : output;
    }
}
