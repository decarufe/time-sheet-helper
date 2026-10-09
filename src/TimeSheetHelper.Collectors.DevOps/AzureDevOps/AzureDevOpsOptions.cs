namespace TimeSheetHelper.Collectors.DevOps.AzureDevOps;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    /// <summary>Organization names, e.g. "contoso" for https://dev.azure.com/contoso.</summary>
    public List<string> Organizations { get; set; } = [];

    public string BaseUrl { get; set; } = "https://dev.azure.com/";

    /// <summary>Safety cap per paginated listing (100 items per page).</summary>
    public int MaxPages { get; set; } = 10;

    /// <summary>Scans every repository of every project for commits; disable when an organization has too many repositories.</summary>
    public bool CollectCommits { get; set; } = true;
}
