namespace TimeSheetHelper.Core.Secrets;

/// <summary>Stores tokens and API keys outside configuration files (Windows Credential Manager).</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

public static class SecretNames
{
    public const string GitHubToken = "TimeSheetHelper:GitHub";
    public const string AzureDevOpsPat = "TimeSheetHelper:AzureDevOps";
    public const string AnthropicApiKey = "TimeSheetHelper:Anthropic";
    public const string IngestToken = "TimeSheetHelper:Ingest";
}
