using System.Text.RegularExpressions;

namespace TimeSheetHelper.Collectors.Windows;

/// <summary>Git remote helpers shared by the VS Code and Claude Code collectors.</summary>
public static partial class GitRemote
{
    /// <summary>
    /// Normalizes a remote URL to the repo identifier stored in <c>ActivityEvent.Repo</c>:
    /// "owner/repo" for GitHub-like hosts, the repository name for Azure DevOps (as the Core model documents).
    /// Returns null for empty or unrecognized input.
    /// </summary>
    public static string? Normalize(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var url = remoteUrl.Trim();
        if (url.EndsWith('/'))
            url = url.TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        string path;
        var scp = ScpLikeRegex().Match(url);
        if (scp.Success)
        {
            path = scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Azure DevOps: https://dev.azure.com/org/project/_git/repo, https://org.visualstudio.com/project/_git/repo
        var gitMarker = Array.FindIndex(segments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (gitMarker >= 0 && gitMarker + 1 < segments.Length)
            return segments[gitMarker + 1];

        // Azure DevOps over SSH: git@ssh.dev.azure.com:v3/org/project/repo
        if (segments.Length == 4 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
            return segments[3];

        return segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : null;
    }

    /// <summary>
    /// Reads the "origin" remote (or the first remote) from the repository containing <paramref name="folder"/>
    /// by parsing .git/config directly: no git process is started. Returns null when not in a repository.
    /// </summary>
    public static string? FindRemoteUrl(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;

        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");
                string? configPath = null;
                if (Directory.Exists(gitPath))
                {
                    configPath = Path.Combine(gitPath, "config");
                }
                else if (File.Exists(gitPath))
                {
                    configPath = ResolveWorktreeConfig(gitPath, dir.FullName);
                }

                if (configPath is not null)
                    return File.Exists(configPath) ? ParseRemoteUrl(File.ReadAllLines(configPath)) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    internal static string? ParseRemoteUrl(IEnumerable<string> configLines)
    {
        string? currentRemote = null;
        string? firstUrl = null;
        foreach (var raw in configLines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var section = RemoteSectionRegex().Match(line);
                currentRemote = section.Success ? section.Groups["name"].Value : null;
                continue;
            }

            if (currentRemote is null)
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = line[(eq + 1)..].Trim();
            if (currentRemote == "origin")
                return url;
            firstUrl ??= url;
        }
        return firstUrl;
    }

    // A worktree or submodule has a ".git" file "gitdir: <path>"; the remotes live in the common dir's config.
    private static string? ResolveWorktreeConfig(string gitFile, string workDir)
    {
        var content = File.ReadAllText(gitFile).Trim();
        if (!content.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            return null;

        var gitDir = content["gitdir:".Length..].Trim();
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.GetFullPath(Path.Combine(workDir, gitDir));

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            var common = File.ReadAllText(commonDirFile).Trim();
            gitDir = Path.IsPathRooted(common) ? common : Path.GetFullPath(Path.Combine(gitDir, common));
        }
        return Path.Combine(gitDir, "config");
    }

    [GeneratedRegex(@"^[\w.-]+@[\w.-]+:(?<path>[^/].*)$")]
    private static partial Regex ScpLikeRegex();

    [GeneratedRegex("""^\[\s*remote\s+"(?<name>[^"]+)"\s*\]$""")]
    private static partial Regex RemoteSectionRegex();
}
