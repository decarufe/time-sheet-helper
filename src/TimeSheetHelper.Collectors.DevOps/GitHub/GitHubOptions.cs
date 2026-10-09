namespace TimeSheetHelper.Collectors.DevOps.GitHub;

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>When non-empty, only activity in repositories owned by these organizations/users is kept.</summary>
    public List<string> Organizations { get; set; } = [];

    /// <summary>REST API root; change for GitHub Enterprise Server.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>Safety cap per paginated listing (100 items per page; search API never returns more than 10 pages).</summary>
    public int MaxPages { get; set; } = 10;

    public bool IsIncluded(string? repo)
    {
        if (Organizations.Count == 0)
        {
            return true;
        }
        var owner = GitHubMapper.Owner(repo);
        return owner is not null && Organizations.Contains(owner, StringComparer.OrdinalIgnoreCase);
    }
}
