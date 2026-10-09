namespace TimeSheetHelper.Core.Rules;

public enum RuleField
{
    Source,
    ProcessName,
    Title,
    FilePath,
    Folder,
    Url,
    Repo,
    Project,
    Subject,
    Participants,
}

public enum RuleOperator
{
    Equals,
    StartsWith,
    Contains,
    Regex,
}

/// <summary>Maps activities matching a condition to a Forecast task. Lower <see cref="Order"/> wins.</summary>
public sealed class Rule
{
    public int Id { get; set; }
    public int Order { get; set; }
    public required string Name { get; set; }
    public RuleField Field { get; set; }
    public RuleOperator Operator { get; set; }
    public required string Pattern { get; set; }
    public int ForecastTaskId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>"user", "correction" or "claude".</summary>
    public string Origin { get; set; } = "user";

    public DateTime CreatedUtc { get; set; }
}
