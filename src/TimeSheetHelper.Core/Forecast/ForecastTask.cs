namespace TimeSheetHelper.Core.Forecast;

/// <summary>A task you can log time against in Forecast, cached locally from the timesheet page.</summary>
public sealed class ForecastTask
{
    public int Id { get; set; }

    /// <summary>Identifier used by Forecast (task id when known, otherwise "project/task" path).</summary>
    public required string ExternalId { get; set; }

    public required string ProjectName { get; set; }
    public string? ProjectCode { get; set; }
    public string? ClientName { get; set; }
    public required string TaskName { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Starred by the user: listed first in the task picker. Never touched by a Forecast sync.</summary>
    public bool IsFavorite { get; set; }

    public DateTime LastSeenUtc { get; set; }

    /// <summary>ExternalId of the built-in task that removes slots or meetings from the timesheet.</summary>
    public const string IgnoredExternalId = "ignored";

    public const string IgnoredLabel = "Ignoré";

    public bool IsIgnored => ExternalId == IgnoredExternalId;

    public string DisplayName => IsIgnored ? IgnoredLabel : $"{ClientName ?? ProjectName} / {TaskName}";
}
