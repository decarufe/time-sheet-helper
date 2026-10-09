namespace TimeSheetHelper.Collectors.DevOps.AzureDevOps;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    /// <summary>Organization names, e.g. "contoso" for https://dev.azure.com/contoso.</summary>
    public List<string> Organizations { get; set; } = [];

    /// <summary>
    /// Projects always scanned for pull requests, builds and commits (names, any organization).
    /// Others are discovered from my work items, pull requests and local clones, so large organizations are not crawled.
    /// </summary>
    public List<string> Projects { get; set; } = [];

    /// <summary>When nothing points to a project, scan only the most recently updated ones.</summary>
    public int MaxProjects { get; set; } = 10;

    public string BaseUrl { get; set; } = "https://dev.azure.com/";

    /// <summary>Safety cap per paginated listing (100 items per page).</summary>
    public int MaxPages { get; set; } = 10;

    /// <summary>Scans every repository of the selected projects for commits (local clones are already covered by "localgit").</summary>
    public bool CollectCommits { get; set; } = true;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
