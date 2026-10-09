using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TimeSheetHelper.Collectors.Microsoft365;

public sealed class GraphRequestException(HttpStatusCode statusCode, string? errorCode, string message)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>Minimal Microsoft Graph REST client: bearer token, throttling retry and @odata.nextLink paging.</summary>
internal sealed class GraphClient(IHttpClientFactory httpFactory, IGraphAccessTokenProvider tokens, ILogger<GraphClient> logger)
{
    public const string HttpClientName = "MicrosoftGraph";
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/";

    private const int MaxAttempts = 4;

    public async Task<JsonElement> GetAsync(string url, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var token = await tokens.GetAccessTokenAsync(cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ToAbsolute(url));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
            }

            if (attempt < MaxAttempts && IsTransient(response.StatusCode))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogInformation("Graph throttled ({Status}), retrying in {Delay}", (int)response.StatusCode, delay);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            throw await ToExceptionAsync(response, cancellationToken);
        }
    }

    /// <summary>Yields each page's "value" items, following @odata.nextLink. Stop enumerating to stop paging.</summary>
    public async IAsyncEnumerable<IReadOnlyList<JsonElement>> GetPagesAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        string? next = url;
        while (next is not null)
        {
            var page = await GetAsync(next, cancellationToken, headers);
            yield return page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray()]
                : [];
            next = page.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
        }
    }

    public async IAsyncEnumerable<JsonElement> GetAllAsync(
        string url,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        await foreach (var page in GetPagesAsync(url, cancellationToken, headers))
        {
            foreach (var item in page)
                yield return item;
        }
    }

    private static Uri ToAbsolute(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute : new Uri(new Uri(BaseUrl), url);

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static async Task<GraphRequestException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body; keep the status code only.
        }

        return new GraphRequestException(
            response.StatusCode,
            code,
            $"Microsoft Graph {(int)response.StatusCode} {code}: {message ?? response.ReasonPhrase}");
    }
}
