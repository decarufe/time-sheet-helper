using System.Text.RegularExpressions;

namespace TimeSheetHelper.Collectors.DevOps.LocalGit;

/// <summary>
/// Normalized remote. GitHub-like hosts give Repo "owner/repo" and Project "owner"; Azure DevOps gives
/// Repo = repository name and Project = "org/project", matching what the online collectors produce.
/// </summary>
public sealed partial record GitRemote(string Repo, string? Project, string? CommitUrlPrefix)
{
    public static GitRemote? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }
        var url = remoteUrl.Trim();

        var match = AzureDevOpsHttps().Match(url);
        if (match.Success)
        {
            return AzureDevOps(match.Groups["org"].Value, match.Groups["project"].Value, match.Groups["repo"].Value);
        }
        match = AzureDevOpsSsh().Match(url);
        if (match.Success)
        {
            return AzureDevOps(match.Groups["org"].Value, match.Groups["project"].Value, match.Groups["repo"].Value);
        }
        match = VisualStudioHttps().Match(url);
        if (match.Success)
        {
            return AzureDevOps(match.Groups["org"].Value, match.Groups["project"].Value, match.Groups["repo"].Value);
        }

        // https://host/owner/repo(.git), ssh://git@host[:port]/owner/repo.git, git@host:owner/repo.git
        match = GenericRemote().Match(url);
        if (!match.Success)
        {
            return null;
        }
        var host = match.Groups["host"].Value;
        var segments = match.Groups["path"].Value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return null;
        }
        var owner = segments[^2];
        var repo = TrimGit(segments[^1]);
        var webHost = host.Contains("github", StringComparison.OrdinalIgnoreCase) || host.Contains("gitlab", StringComparison.OrdinalIgnoreCase);
        return new GitRemote(
            $"{owner}/{repo}",
            owner,
            webHost ? $"https://{host}/{string.Join('/', segments[..^1])}/{repo}/commit/" : null);
    }

    private static GitRemote AzureDevOps(string organization, string project, string repo)
    {
        organization = Uri.UnescapeDataString(organization);
        project = Uri.UnescapeDataString(project);
        repo = Uri.UnescapeDataString(TrimGit(repo));
        return new GitRemote(
            repo,
            $"{organization}/{project}",
            $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repo)}/commit/");
    }

    private static string TrimGit(string name) => name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    [GeneratedRegex(@"^https?://(?:[^@/]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AzureDevOpsHttps();

    [GeneratedRegex(@"^(?:ssh://)?[^@]+@(?:ssh\.dev\.azure\.com|vs-ssh\.visualstudio\.com)[:/]v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AzureDevOpsSsh();

    [GeneratedRegex(@"^https?://(?:[^@/]+@)?(?<org>[^./]+)\.visualstudio\.com/(?:DefaultCollection/)?(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VisualStudioHttps();

    [GeneratedRegex(@"^(?:(?:https?|ssh|git)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?/|[^@/]+@(?<host>[^:/]+):)(?<path>[^?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GenericRemote();
}
