using System.Text.Json.Nodes;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastMetadataFixTests
{
    private static readonly Uri Endpoint = new("https://mcp.forecast.it/mcp");

    private static string? Fix(string json) => ForecastMcpConnector.ProtectedResourceMetadataFixHandler.Fix(json, Endpoint);

    [Theory]
    [InlineData("https://mcp.forecast.it")]
    [InlineData("https://mcp.forecast.it/")]
    [InlineData("HTTPS://MCP.FORECAST.IT")]
    public void A_parent_resource_is_replaced_by_the_endpoint(string advertised)
    {
        var fixedJson = Fix($$"""{"resource":"{{advertised}}","authorization_servers":["https://auth.forecast.it"],"scopes_supported":["forecast"]}""");

        var node = JsonNode.Parse(fixedJson!)!;
        Assert.Equal("https://mcp.forecast.it/mcp", (string?)node["resource"]);
        Assert.Equal("https://auth.forecast.it", (string?)node["authorization_servers"]![0]);
        Assert.Equal("forecast", (string?)node["scopes_supported"]![0]);
    }

    private static string? Restore(string parameters) =>
        ForecastMcpConnector.ProtectedResourceMetadataFixHandler.RestoreResource(parameters, Endpoint, "https://mcp.forecast.it");

    [Fact]
    public void The_resource_parameter_goes_back_to_what_the_server_advertised()
    {
        Assert.Equal(
            "grant_type=refresh_token&resource=https%3A%2F%2Fmcp.forecast.it&client_id=abc",
            Restore("grant_type=refresh_token&resource=https%3A%2F%2Fmcp.forecast.it%2Fmcp&client_id=abc"));
        Assert.Equal(
            "client_id=abc&resource=https%3A%2F%2Fmcp.forecast.it",
            Restore("client_id=abc&resource=https%3a%2f%2fmcp.forecast.it%2fmcp"));
    }

    [Theory]
    [InlineData("client_id=abc&scope=forecast")]
    [InlineData("resource=https%3A%2F%2Fautre.example%2Fmcp")]
    [InlineData("other_resource=https%3A%2F%2Fmcp.forecast.it%2Fmcp")]
    public void Other_parameters_are_left_alone(string parameters) => Assert.Null(Restore(parameters));

    [Fact]
    public void The_advertised_resource_is_read_from_the_metadata()
    {
        Assert.Equal("https://mcp.forecast.it",
            ForecastMcpConnector.ProtectedResourceMetadataFixHandler.AdvertisedResource("""{"resource":"https://mcp.forecast.it"}"""));
        Assert.Null(ForecastMcpConnector.ProtectedResourceMetadataFixHandler.AdvertisedResource("pas du json"));
    }

    [Theory]
    [InlineData("""{"resource":"https://mcp.forecast.it/mcp"}""")]
    [InlineData("""{"resource":"https://mcp.forecast.it/mcp/"}""")]
    [InlineData("""{"resource":"https://autre.example"}""")]
    [InlineData("""{"resource":"https://mcp.forecast.it/mcpx"}""")]
    [InlineData("""{"resource":"pas une url"}""")]
    [InlineData("""{"authorization_servers":[]}""")]
    [InlineData("pas du json")]
    public void Anything_else_is_left_alone(string json) => Assert.Null(Fix(json));
}
