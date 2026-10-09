namespace TimeSheetHelper.Collectors.Windows;

public sealed class FocusTrackerOptions
{
    public const string SectionName = "FocusTracker";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MinSegmentDuration { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MergeGap { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Processes whose title and files are never recorded (only the process name is kept).</summary>
    public List<string> ExcludedProcesses { get; set; } = [];

    /// <summary>Case-insensitive keywords that redact a window title when present.</summary>
    public List<string> ExcludedTitleKeywords { get; set; } = [];

    /// <summary>
    /// Per-process regexes applied to the window title. Named groups "file" and "folder" are extracted
    /// when no better source (COM, extension) knows the file. Key is the process name without ".exe".
    /// </summary>
    public Dictionary<string, string> TitlePatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Code"] = @"^(?:● )?(?<file>.+?) - (?<folder>.+?) - Visual Studio Code",
        ["devenv"] = @"^(?<folder>.+?) - Microsoft Visual Studio",
        ["notepad"] = @"^\*?(?<file>.+?) - Notepad",
    };
}
