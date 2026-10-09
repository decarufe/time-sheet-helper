using System.Net;
using System.Text;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Tests.DevOps;

internal static class Fixture
{
    public static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));
}

/// <summary>Routes requests to canned responses; unmatched requests get 404. Records every request URL.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> Bodies { get; } = [];

    /// <summary>Matches when the unescaped absolute path ends with <paramref name="pathSuffix"/> and the unescaped query contains every fragment.</summary>
    public FakeHttpMessageHandler On(string pathSuffix, string? json, params string[] queryContains)
        => On(pathSuffix, queryContains, _ => Json(json ?? "{}"));

    public FakeHttpMessageHandler OnFixture(string pathSuffix, string fixture, params string[] queryContains)
        => On(pathSuffix, Fixture.Read(fixture), queryContains);

    public FakeHttpMessageHandler On(string pathSuffix, string[] queryContains, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((request =>
        {
            var uri = request.RequestUri!;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var query = Uri.UnescapeDataString(uri.Query);
            return path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase)
                && queryContains.All(q => query.Contains(q, StringComparison.OrdinalIgnoreCase));
        }, respond));
        return this;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                return respond(request);
            }
        }
        return Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
    }
}

internal sealed class RecordingSink : IActivitySink
{
    public List<ActivityEvent> Events { get; } = [];

    public Task WriteAsync(ActivityEvent activity, CancellationToken cancellationToken = default)
    {
        Events.Add(activity);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSecretStore(Dictionary<string, string>? values = null) : ISecretStore
{
    private readonly Dictionary<string, string> _values = values ?? [];

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public void Set(string name, string value) => _values[name] = value;
    public void Delete(string name) => _values.Remove(name);
}

internal sealed class FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult?> handler) : IProcessRunner
{
    public List<string> Calls { get; } = [];

    public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"{fileName} {string.Join(' ', arguments)}");
        return Task.FromResult(handler(fileName, arguments));
    }

    public static ProcessResult Ok(string output) => new(0, output, "");
    public static ProcessResult Fail() => new(1, "", "error");
}

internal sealed class FakeActivityHistory(params ActivityEvent[] activities) : IActivityHistory
{
    public Task<IReadOnlyList<ActivityEvent>> GetRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActivityEvent>>(activities);
}
