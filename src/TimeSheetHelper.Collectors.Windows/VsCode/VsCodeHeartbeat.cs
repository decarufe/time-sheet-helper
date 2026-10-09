namespace TimeSheetHelper.Collectors.Windows.VsCode;

/// <summary>Payload POSTed by the VS Code extension to /ingest/vscode (camelCase JSON).</summary>
public sealed record VsCodeHeartbeat
{
    public string? SessionId { get; init; }
    public DateTime TimestampUtc { get; init; }
    public bool WindowFocused { get; init; }
    public string? WorkspaceName { get; init; }
    public IReadOnlyList<string>? WorkspaceFolders { get; init; }
    public string? ActiveFile { get; init; }
    public string? LanguageId { get; init; }
    public string? GitRemote { get; init; }
    public string? GitBranch { get; init; }
    public int EditsSinceLast { get; init; }
    public bool Debugging { get; init; }
    public bool AiActivity { get; init; }
}
