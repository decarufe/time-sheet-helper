using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using TimeSheetHelper.Collectors.DevOps.Common;

namespace TimeSheetHelper.Collectors.DevOps.GitHub;

/// <summary>Thin REST client: authentication, Link-header pagination and rate-limit detection.</summary>
public sealed partial class GitHubApiClient(HttpClient http)
{
    public async Task<JsonElement> GetAsync(string token, string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(token, url, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>
    /// Enumerates every item of a paginated listing. <paramref name="itemsProperty"/> names the array inside
    /// the response object ("items" for search, "workflow_runs" for Actions); null when the body is the array.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string token, string url, string? itemsProperty, int maxPages, ICollection<string> warnings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? next = url;
        for (var page = 0; next is not null; page++)
        {
            if (page >= maxPages)
            {
                warnings.Add($"GitHub : résultats tronqués après {maxPages} pages ({Describe(url)}).");
                yield break;
            }

            using var response = await SendAsync(token, next, cancellationToken);
            var body = await ReadAsync(response, cancellationToken);
            if (body.Str("incomplete_results") == "true")
            {
                warnings.Add($"GitHub : la recherche a renvoyé des résultats incomplets ({Describe(url)}).");
            }

            var items = itemsProperty is null ? body : body.Path(itemsProperty);
            if (items is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    yield return item;
                }
            }
            next = NextLink(response);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (IsRateLimited(response, out var resetUtc))
            {
                var when = resetUtc is { } reset ? $" (réinitialisation à {reset.ToLocalTime():HH:mm})" : "";
                throw new ApiRateLimitException($"GitHub : limite d'appels API atteinte{when}; collecte interrompue.", resetUtc);
            }
            throw new HttpRequestException($"GitHub {(int)response.StatusCode} {response.ReasonPhrase} : {Describe(url)}", null, response.StatusCode);
        }
    }

    internal static bool IsRateLimited(HttpResponseMessage response, out DateTime? resetUtc)
    {
        resetUtc = null;
        if (Header(response, "x-ratelimit-reset") is { } reset && long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            resetUtc = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }
        // Primary limit: 403 with remaining = 0. Secondary limit: 403 with Retry-After.
        return response.StatusCode == HttpStatusCode.Forbidden
            && (Header(response, "x-ratelimit-remaining") == "0" || response.Headers.RetryAfter is not null);
    }

    internal static string? NextLink(HttpResponseMessage response)
    {
        if (Header(response, "Link") is not { } link)
        {
            return null;
        }
        var match = NextLinkRegex().Match(link);
        return match.Success ? match.Groups["url"].Value : null;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static string Describe(string url)
    {
        var path = url.Split('?')[0];
        return Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath.TrimStart('/') : path;
    }

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"next\"")]
    private static partial Regex NextLinkRegex();
}
