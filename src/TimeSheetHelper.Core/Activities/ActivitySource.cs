namespace TimeSheetHelper.Core.Activities;

/// <summary>
/// Identifies where an activity comes from ("Focus", "GitHub", ...). A string key rather than an enum so that source
/// plugins can add their own; it is stored as that string. How the engine treats a source is described by
/// <see cref="SourceTraits"/>, not by comparing identifiers.
/// </summary>
public readonly record struct ActivitySource(string Id) : IComparable<ActivitySource>
{
    public static readonly ActivitySource Focus = new("Focus");
    public static readonly ActivitySource Idle = new("Idle");
    public static readonly ActivitySource Locked = new("Locked");
    public static readonly ActivitySource VsCode = new("VsCode");
    public static readonly ActivitySource ClaudeCode = new("ClaudeCode");
    public static readonly ActivitySource GitHub = new("GitHub");
    public static readonly ActivitySource AzureDevOps = new("AzureDevOps");
    public static readonly ActivitySource LocalGit = new("LocalGit");
    public static readonly ActivitySource Calendar = new("Calendar");
    public static readonly ActivitySource Email = new("Email");
    public static readonly ActivitySource Teams = new("Teams");

    public override string ToString() => Id ?? "";

    public int CompareTo(ActivitySource other) => string.CompareOrdinal(Id, other.Id);
}
