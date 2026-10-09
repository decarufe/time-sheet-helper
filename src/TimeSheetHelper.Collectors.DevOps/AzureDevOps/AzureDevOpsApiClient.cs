using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TimeSheetHelper.Collectors.DevOps.Common;

namespace TimeSheetHelper.Collectors.DevOps.AzureDevOps;

public enum AzureDevOpsPaging
{
    /// <summary>Single request.</summary>
    None,

    /// <summary>"x-ms-continuationtoken" response header echoed back as "continuationToken".</summary>
    ContinuationToken,

    /// <summary>"$top"/"$skip" query parameters.</summary>
    Skip,

    /// <summary>"searchCriteria.$top"/"searchCriteria.$skip" (git commits).</summary>
    SearchCriteriaSkip,
}

/// <summary>Thin REST client: PAT basic authentication, "value" lists, pagination and throttling detection.</summary>
public sealed class AzureDevOpsApiClient(HttpClient http)
{
    internal const int PageSize = 100;

    public async Task<JsonElement> GetAsync(string pat, string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(pat, new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<JsonElement> PostAsync<T>(string pat, string url, T body, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        using var response = await SendAsync(pat, request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>Enumerates the "value" array of a list endpoint across pages.</summary>
    public async IAsyncEnumerable<JsonElement> GetValuesAsync(
        string pat, string url, AzureDevOpsPaging paging, int maxPages, ICollection<string> warnings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuation = null;
        for (var page = 0; ; page++)
        {
            if (page >= maxPages)
            {
                warnings.Add($"Azure DevOps : résultats tronqués après {maxPages} pages ({Describe(url)}).");
                yield break;
            }

            var pageUrl = paging switch
            {
                AzureDevOpsPaging.Skip => $"{url}&$top={PageSize}&$skip={page * PageSize}",
                AzureDevOpsPaging.SearchCriteriaSkip => $"{url}&searchCriteria.$top={PageSize}&searchCriteria.$skip={page * PageSize}",
                AzureDevOpsPaging.ContinuationToken when continuation is not null => $"{url}&continuationToken={Uri.EscapeDataString(continuation)}",
                _ => url,
            };

            using var response = await SendAsync(pat, new HttpRequestMessage(HttpMethod.Get, pageUrl), cancellationToken);
            var body = await ReadAsync(response, cancellationToken);
            var count = 0;
            foreach (var item in body.Array("value"))
            {
                count++;
                yield return item;
            }

            continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var tokens) ? tokens.FirstOrDefault() : null;
            var hasMore = paging switch
            {
                AzureDevOpsPaging.Skip or AzureDevOpsPaging.SearchCriteriaSkip => count >= PageSize,
                AzureDevOpsPaging.ContinuationToken => !string.IsNullOrEmpty(continuation),
                _ => false,
            };
            if (!hasMore)
            {
                yield break;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string pat, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // An invalid PAT is answered with 203 + an HTML sign-in page instead of 401.
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation
                || (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html"))
            {
                response.Dispose();
                throw new HttpRequestException("Azure DevOps : authentification refusée.", null, HttpStatusCode.Unauthorized);
            }
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null)
                {
                    throw new ApiRateLimitException("Azure DevOps : limitation du débit (429) ; collecte interrompue.");
                }
                throw new HttpRequestException(
                    $"Azure DevOps {(int)response.StatusCode} {response.ReasonPhrase} : {Describe(request.RequestUri?.ToString() ?? "")}",
                    null, response.StatusCode);
            }
        }
    }

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
}
