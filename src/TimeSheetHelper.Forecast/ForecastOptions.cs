using System.Text.Json;
using TimeSheetHelper.Core;

namespace TimeSheetHelper.Forecast;

public sealed class ForecastOptions
{
    public const string SectionName = "Forecast";

    /// <summary>MCP server of Forecast (Streamable HTTP). Overridden by the server found in <see cref="McpConfigFile"/>.</summary>
    public string McpUrl { get; set; } = "https://mcp.forecast.it/mcp";

    /// <summary>
    /// A ".mcp.json" file ({"mcpServers": {"Forecast MCP": {"url": ..., "type": "http"}}}) used as the base of the connection.
    /// Null looks for ".mcp.json" in the application folder and the current directory and its parents; no file means <see cref="McpUrl"/>.
    /// </summary>
    public string? McpConfigFile { get; set; }

    /// <summary>Name of the server entry read in <see cref="McpConfigFile"/>.</summary>
    public string McpServerName { get; set; } = "Forecast MCP";

    /// <summary>Fixed loopback port of the OAuth redirect (http://localhost:port/callback), registered once with the server.</summary>
    public int OAuthRedirectPort { get; set; } = 17653;

    /// <summary>Name shown on the Forecast consent page.</summary>
    public string ClientName { get; set; } = "Time Sheet Helper";

    /// <summary>OAuth scopes to request. Empty uses the ones the server advertises.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>How long to wait for the user to finish the sign-in in the browser.</summary>
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Timeout of one MCP request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many times a request refused with "429 Too Many Requests" (or a transient 5xx on a read) is retried.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Wait before the first retry; it doubles after each one (capped at 30 s).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>"max_results" asked of the tool that lists the time registrations (the server caps it at 200 by default).</summary>
    public int TimeEntriesMaxResults { get; set; } = 1000;

    /// <summary>Read-only checks before a simulation or a fill: task finished or blocked, project closed, estimate exceeded.</summary>
    public bool PreflightChecks { get; set; } = true;

    /// <summary>How long a project/task search of the "add task" dialog is remembered (zero disables the cache).</summary>
    public TimeSpan LookupCacheDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Your Forecast person id, only needed when the server cannot tell who you are.</summary>
    public string? PersonId { get; set; }

    /// <summary>Tool names to use instead of the ones guessed from the server's catalogue (see "forecast tools").</summary>
    public ForecastToolNames Tools { get; set; } = new();

    /// <summary>File that stores the registered client and the tokens, protected with DPAPI.</summary>
    public string? AuthFile { get; set; }

    internal string ResolvedAuthFile => AuthFile ?? Path.Combine(DataPaths.Root, "forecast-mcp-auth.bin");

    internal Uri RedirectUri => new($"http://localhost:{OAuthRedirectPort}/callback");

    /// <summary>The MCP endpoint: the one of the ".mcp.json" server entry when there is one, otherwise <see cref="McpUrl"/>.</summary>
    internal Uri ResolveEndpoint()
    {
        foreach (var file in CandidateConfigFiles())
        {
            if (TryReadServerUrl(file, McpServerName) is { } url)
                return url;
        }
        return new Uri(McpUrl);
    }

    private IEnumerable<string> CandidateConfigFiles()
    {
        if (!string.IsNullOrWhiteSpace(McpConfigFile))
        {
            yield return McpConfigFile;
            yield break;
        }

        yield return Path.Combine(AppContext.BaseDirectory, ".mcp.json");
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, ".mcp.json");
    }

    internal static Uri? TryReadServerUrl(string file, string serverName)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var server in servers.EnumerateObject())
            {
                if (!string.Equals(server.Name, serverName, StringComparison.OrdinalIgnoreCase)
                    || !server.Value.TryGetProperty("url", out var url)
                    || url.ValueKind != JsonValueKind.String)
                    continue;
                return Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file falls back to the configured URL.
        }
        return null;
    }
}

/// <summary>Optional overrides of the tools used by the integration; null means "guess from the catalogue".</summary>
public sealed class ForecastToolNames
{
    public string? ListTasks { get; set; }
    public string? ListTimeEntries { get; set; }
    public string? CreateTimeEntry { get; set; }
    public string? CurrentUser { get; set; }

    /// <summary>Tool that changes the hours of one registration ("Forecast:Tools:UpdateTimeEntry"); forecast-mcp has none.</summary>
    public string? UpdateTimeEntry { get; set; }

    /// <summary>Tool that removes one registration ("Forecast:Tools:DeleteTimeEntry"); forecast-mcp has none.</summary>
    public string? DeleteTimeEntry { get; set; }
}
