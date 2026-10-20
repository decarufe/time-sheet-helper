namespace TimeSheetHelper.App.Updates;

public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>GitHub repository ("owner/name") whose releases are checked.</summary>
    public string Repository { get; set; } = "decarufe/time-sheet-helper";
}
