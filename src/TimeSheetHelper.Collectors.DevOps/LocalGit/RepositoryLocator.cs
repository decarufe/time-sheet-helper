namespace TimeSheetHelper.Collectors.DevOps.LocalGit;

/// <summary>Finds git working trees on disk (a folder containing a ".git" directory, or a ".git" file for worktrees/submodules).</summary>
public static class RepositoryLocator
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".vs", ".vscode", ".idea", "packages",
    };

    public static bool IsRepositoryRoot(string folder)
    {
        var marker = Path.Combine(folder, ".git");
        return Directory.Exists(marker) || File.Exists(marker);
    }

    /// <summary>Walks up from a file or folder to the enclosing repository root, or null.</summary>
    public static string? FindRepositoryRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try
        {
            var current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path).Directory;
            for (; current is not null; current = current.Parent)
            {
                if (current.Exists && IsRepositoryRoot(current.FullName))
                {
                    return Path.TrimEndingDirectorySeparator(current.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>Expands wildcard roots ("C:\git*") and returns repositories up to <paramref name="depth"/> levels below them.</summary>
    public static IEnumerable<string> Scan(IEnumerable<string> roots, int depth)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.SelectMany(ExpandRoot))
        {
            foreach (var repository in ScanFolder(root, depth))
            {
                if (seen.Add(repository))
                {
                    yield return repository;
                }
            }
        }
    }

    internal static IEnumerable<string> ExpandRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }
        root = Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(root.Trim()));
        var name = Path.GetFileName(root);
        if (name.IndexOfAny(['*', '?']) < 0)
        {
            return Directory.Exists(root) ? [root] : [];
        }
        var parent = Path.GetDirectoryName(root);
        if (parent is null || !Directory.Exists(parent))
        {
            return [];
        }
        try
        {
            return Directory.EnumerateDirectories(parent, name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ScanFolder(string folder, int depth)
    {
        if (IsRepositoryRoot(folder))
        {
            yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            yield break;
        }
        if (depth <= 0)
        {
            yield break;
        }

        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(folder)
                .Where(c => !SkippedFolders.Contains(Path.GetFileName(c)) && !Path.GetFileName(c).StartsWith('.'))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            foreach (var repository in ScanFolder(child, depth - 1))
            {
                yield return repository;
            }
        }
    }
}
