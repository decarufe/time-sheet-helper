using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.Microsoft365;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests.Microsoft365;

internal static class GraphFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Microsoft365", name));

    public static JsonElement Load(string name) => JsonSerializer.Deserialize<JsonElement>(Read(name));

    public static IReadOnlyList<JsonElement> Values(string name) => [.. Load(name).GetProperty("value").EnumerateArray()];
}

/// <summary>Serves recorded Graph responses by URL and records every request; unknown URLs return 404.</summary>
internal sealed class FakeGraphHandler(Func<Uri, string?> route) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var fixture = route(request.RequestUri!);
        var response = fixture is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"code":"NotFound","message":"no fixture"}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GraphFixtures.Read(fixture), Encoding.UTF8, "application/json"),
            };
        return Task.FromResult(response);
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class FakeTokenProvider(bool configured = true, bool signedIn = true) : IGraphAccessTokenProvider
{
    public bool IsConfigured => configured;
    public Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default) => Task.FromResult(signedIn);

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        signedIn ? Task.FromResult("token") : throw new Microsoft365SignInRequiredException("Connexion Microsoft 365 requise");
}

internal sealed class ListSink : IActivitySink
{
    public List<ActivityEvent> Items { get; } = [];

    public Task WriteAsync(ActivityEvent activity, CancellationToken cancellationToken = default)
    {
        Items.Add(activity);
        return Task.CompletedTask;
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class GraphTestClient
{
    public static GraphClient Client(HttpMessageHandler handler, IGraphAccessTokenProvider? tokens = null) =>
        new(new FakeHttpClientFactory(handler), tokens ?? new FakeTokenProvider(), NullLogger<GraphClient>.Instance);
}
