namespace TimeSheetHelper.Core.Plugins;

/// <summary>What the user decided for a source plugin: switched on or off, and the settings they saved.</summary>
public sealed class InstalledPlugin
{
    /// <summary>Plugin identifier (the descriptor id), not the package.</summary>
    public required string Id { get; set; }

    public bool Enabled { get; set; }

    /// <summary>JSON object of the settings saved from the UI (lists are newline-separated); null when none.</summary>
    public string? ConfigJson { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
