namespace TimeSheetHelper.Collectors.DevOps.LocalGit;

public sealed class LocalGitOptions
{
    public const string SectionName = "LocalGit";

    /// <summary>Used when <see cref="Roots"/> is empty (a list default would be appended to by configuration binding).</summary>
    public static readonly IReadOnlyList<string> DefaultRoots = [@"C:\git*"];

    /// <summary>
    /// Folders scanned for repositories. The last segment may contain wildcards, e.g. "C:\git*" matches C:\git and C:\git-work.
    /// </summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>How many folder levels below each root are searched for repositories.</summary>
    public int ScanDepth { get; set; } = 2;

    public IReadOnlyList<string> EffectiveRoots => Roots.Count > 0 ? Roots : DefaultRoots;
}
