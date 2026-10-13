using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public sealed class ForecastOptionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tsh-mcp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string content, string name = ".mcp.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Server_url_is_read_from_the_mcp_json_entry_ignoring_case()
    {
        var file = Write("""{"mcpServers": {"Autre": {"url": "https://autre.example/mcp"}, "forecast mcp": {"url": "https://mcp.forecast.it/mcp", "type": "http"}}}""");

        Assert.Equal(new Uri("https://mcp.forecast.it/mcp"), ForecastOptions.TryReadServerUrl(file, "Forecast MCP"));
        Assert.Equal(new Uri("https://autre.example/mcp"), ForecastOptions.TryReadServerUrl(file, "Autre"));
    }

    [Theory]
    [InlineData("""{"mcpServers": {"Forecast MCP": {"type": "stdio", "command": "x"}}}""")]
    [InlineData("""{"mcpServers": {"Forecast MCP": {"url": "ftp://example/mcp"}}}""")]
    [InlineData("""{"mcpServers": {"Forecast MCP": {"url": "pas une url"}}}""")]
    [InlineData("""{"mcpServers": []}""")]
    [InlineData("""{"servers": {}}""")]
    [InlineData("pas du json")]
    public void Unusable_files_give_no_url(string content) =>
        Assert.Null(ForecastOptions.TryReadServerUrl(Write(content), "Forecast MCP"));

    [Fact]
    public void A_missing_file_gives_no_url() =>
        Assert.Null(ForecastOptions.TryReadServerUrl(Path.Combine(_dir, "absent.json"), "Forecast MCP"));

    [Fact]
    public void Endpoint_comes_from_the_configured_mcp_json_else_from_the_url_option()
    {
        var file = Write("""{"mcpServers": {"Forecast MCP": {"url": "https://exemple.test/mcp"}}}""");

        Assert.Equal(new Uri("https://exemple.test/mcp"), new ForecastOptions { McpConfigFile = file }.ResolveEndpoint());
        Assert.Equal(new Uri("https://mcp.forecast.it/mcp"),
            new ForecastOptions { McpConfigFile = Path.Combine(_dir, "absent.json") }.ResolveEndpoint());
        Assert.Equal(new Uri("https://autre.test/mcp"),
            new ForecastOptions { McpConfigFile = Path.Combine(_dir, "absent.json"), McpUrl = "https://autre.test/mcp" }.ResolveEndpoint());
    }

    [Fact]
    public void The_redirect_uri_is_the_fixed_loopback_callback() =>
        Assert.Equal(new Uri("http://localhost:17653/callback"), new ForecastOptions().RedirectUri);
}

public class ForecastTaskReaderTests
{
    [Fact]
    public void A_remote_task_becomes_a_cached_task_keyed_by_its_code()
    {
        var parsed = ForecastTaskReader.ToTask(new ForecastRemoteTask("1234", "T3382", "Développement", "Portail", "P225", "Acme"));

        Assert.Equal(new ParsedForecastTask("T3382", "Portail", "P225", "Acme", "Développement"), parsed);
    }

    [Fact]
    public void Without_a_code_the_key_is_project_slash_title()
    {
        var parsed = ForecastTaskReader.ToTask(new ForecastRemoteTask("1", null, "Support", "Maintenance", null, null));
        Assert.Equal("Maintenance/Support", parsed.ExternalId);
    }

    [Fact]
    public void Without_a_project_the_client_then_a_placeholder_name_the_project()
    {
        Assert.Equal("Globex", ForecastTaskReader.ToTask(new ForecastRemoteTask("1", "T1", "A", null, null, "Globex")).ProjectName);
        Assert.Equal(ForecastCodes.NoProject, ForecastTaskReader.ToTask(new ForecastRemoteTask("1", "T1", "A", " ", null, null)).ProjectName);
    }
}

public class ForecastFindRemoteTests
{
    private static ForecastTask Task(string externalId, string project, string task, string? client = null) =>
        new() { Id = 1, ExternalId = externalId, ProjectName = project, TaskName = task, ClientName = client };

    private static ForecastRemoteTask Remote(string id, string? code, string title, string? project = null, string? client = null) =>
        new(id, code, title, project, null, client);

    [Fact]
    public void The_code_finds_the_task()
    {
        var remote = new[] { Remote("a", "T1", "Autre", "Acme"), Remote("b", "T3382", "Peu importe", "Ailleurs") };
        Assert.Equal("b", ForecastTimesheetWriter.FindRemote(Task("T3382", "Acme", "Dev"), remote)?.Id);
    }

    [Fact]
    public void Without_a_usable_code_project_and_title_are_compared_ignoring_case_accents_and_punctuation()
    {
        var remote = new[] { Remote("a", null, "DÉVELOPPEMENT", "acme - portail") , Remote("b", null, "Développement", "Autre") };
        Assert.Equal("a", ForecastTimesheetWriter.FindRemote(Task("Acme-Portail/Développement", "Acme Portail", "Développement"), remote)?.Id);
    }

    [Fact]
    public void The_client_name_can_stand_for_the_project()
    {
        var remote = new[] { Remote("a", null, "Dev", "Portail", "Acme") };
        Assert.Equal("a", ForecastTimesheetWriter.FindRemote(Task("Acme/Dev", "Acme", "Dev"), remote)?.Id);
    }

    [Fact]
    public void An_ambiguous_match_is_not_guessed()
    {
        var remote = new[] { Remote("a", null, "Dev", "Acme"), Remote("b", null, "Dev", "Acme") };
        Assert.Null(ForecastTimesheetWriter.FindRemote(Task("Acme/Dev", "Acme", "Dev"), remote));
    }

    [Fact]
    public void An_unknown_task_gives_nothing()
    {
        var remote = new[] { Remote("a", "T1", "Dev", "Acme") };
        Assert.Null(ForecastTimesheetWriter.FindRemote(Task("T999", "Acme", "Autre"), remote));
        Assert.Null(ForecastTimesheetWriter.FindRemote(Task("T999", "Acme", "Dev"), []));
    }

    [Fact]
    public void A_code_that_is_not_listed_falls_back_to_the_names()
    {
        var remote = new[] { Remote("a", "T1", "Dev", "Acme") };
        Assert.Equal("a", ForecastTimesheetWriter.FindRemote(Task("T999", "Acme", "Dev"), remote)?.Id);
    }
}
