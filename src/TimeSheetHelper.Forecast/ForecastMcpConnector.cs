using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TimeSheetHelper.Forecast;

public sealed record ForecastToolInfo(string Name, string? Description, JsonElement InputSchema)
{
    /// <summary>Names of the properties of the input schema.</summary>
    public IEnumerable<string> ParameterNames =>
        InputSchema.ValueKind == JsonValueKind.Object && InputSchema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.EnumerateObject().Select(o => o.Name)
            : [];
}

/// <summary>What a tool returned: its text and, when it is JSON (structured content or JSON text), the parsed value.</summary>
public sealed record ForecastToolResult(bool IsError, string Text, JsonElement? Json);

/// <summary>An open session with the Forecast MCP server.</summary>
public interface IForecastToolClient : IAsyncDisposable
{
    Task<IReadOnlyList<ForecastToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<ForecastToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default);
}

public interface IForecastConnector
{
    /// <summary>
    /// Opens a session. Without <paramref name="interactive"/>, only the stored credentials are used and
    /// <see cref="ForecastAuthRequiredException"/> is thrown when they are missing or expired; with it the
    /// system browser is opened for the sign-in.
    /// </summary>
    Task<IForecastToolClient> ConnectAsync(bool interactive, Action<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Forgets the stored sign-in.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    bool HasCredentials { get; }
}

public sealed class ForecastAuthRequiredException()
    : Exception("Connexion à Forecast requise : lancez « TimeSheetHelper forecast login » (ou le bouton « Se connecter » de la page Sources).");

public sealed class ForecastMcpConnector(
    IOptions<ForecastOptions> options,
    ForecastAuthStore store,
    ILoggerFactory loggerFactory,
    ILogger<ForecastMcpConnector> logger) : IForecastConnector
{
    public bool HasCredentials => store.HasCredentials;

    public Task SignOutAsync(CancellationToken cancellationToken = default) => store.ClearAsync(cancellationToken);

    public async Task<IForecastToolClient> ConnectAsync(bool interactive, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (!interactive && !store.HasCredentials)
            throw new ForecastAuthRequiredException();

        var redirectRequested = false;
        ProtectedResourceMetadataFixHandler? metadataFix = null;
        var oauth = new ClientOAuthOptions
        {
            RedirectUri = o.RedirectUri,
            ClientId = store.ClientId,
            ClientSecret = store.ClientSecret,
            Scopes = o.Scopes.Count > 0 ? o.Scopes : null,
            TokenCache = store,
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = o.ClientName,
                ResponseDelegate = async (response, ct) => await store.StoreClientAsync(response.ClientId, response.ClientSecret, ct),
            },
            AuthorizationRedirectDelegate = (authorizationUri, redirectUri, ct) =>
            {
                redirectRequested = true;
                authorizationUri = metadataFix?.RestoreResource(authorizationUri) ?? authorizationUri;
                return interactive
                    ? ForecastLoopbackAuthorization.AuthorizeAsync(authorizationUri, redirectUri, o.LoginTimeout, progress, logger, ct)
                    : Task.FromResult<string?>(null);
            },
        };

        var connectTimeout = interactive ? o.LoginTimeout + TimeSpan.FromSeconds(30) : o.RequestTimeout;
        var endpoint = o.ResolveEndpoint();
        metadataFix = new ProtectedResourceMetadataFixHandler(endpoint, logger) { InnerHandler = new SocketsHttpHandler() };
        var httpClient = new HttpClient(metadataFix);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = "Forecast MCP",
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = connectTimeout,
                OAuth = oauth,
            },
            httpClient,
            loggerFactory,
            ownsHttpClient: true);

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "TimeSheetHelper", Version = "1.0" },
                    InitializationTimeout = connectTimeout,
                },
                loggerFactory,
                cancellationToken);
            return new McpToolClient(client, o.RequestTimeout);
        }
        catch (Exception ex) when (!interactive && IsUnauthorized(ex))
        {
            logger.LogInformation(ex, "Les identifiants Forecast sont refusés par le serveur.");
            throw new ForecastAuthRequiredException();
        }
        catch (Exception ex) when (redirectRequested && !interactive && ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Les identifiants Forecast ont expiré.");
            throw new ForecastAuthRequiredException();
        }
    }

    private static bool IsUnauthorized(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    /// <summary>
    /// The Forecast server advertises "https://mcp.forecast.it" as the protected resource of "https://mcp.forecast.it/mcp",
    /// which the SDK rejects (RFC 9728 wants the exact URL). When the advertised resource is a parent of the endpoint,
    /// the metadata is rewritten to name the endpoint. The authorization server, however, must keep hearing the resource the
    /// server really advertised (the token audience), so the "resource" parameter of the authorization and token requests
    /// is switched back to it.
    /// </summary>
    internal sealed class ProtectedResourceMetadataFixHandler(Uri endpoint, ILogger logger) : DelegatingHandler
    {
        private string? _advertised;

        /// <summary>The authorization URL with its "resource" parameter set back to what the server advertised.</summary>
        internal Uri RestoreResource(Uri authorizationUri)
        {
            if (_advertised is null || RestoreResource(authorizationUri.Query.TrimStart('?'), endpoint, _advertised) is not { } query)
                return authorizationUri;
            return new UriBuilder(authorizationUri) { Query = query }.Uri;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_advertised is not null && request.Method == HttpMethod.Post && request.RequestUri?.Host != endpoint.Host
                && request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
            {
                var form = await request.Content.ReadAsStringAsync(cancellationToken);
                if (RestoreResource(form, endpoint, _advertised) is { } restored)
                    request.Content = new StringContent(restored, Encoding.UTF8, "application/x-www-form-urlencoded");
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                // The optional server-to-client stream (GET) is refused with 405: expected, not worth a log entry.
                logger.Log(
                    request.Method == HttpMethod.Get && response.StatusCode == HttpStatusCode.MethodNotAllowed ? LogLevel.Debug : LogLevel.Information,
                    "Forecast : {Method} {Uri} -> {Status}. WWW-Authenticate : {Challenge}. Corps : {Body}",
                    request.Method, request.RequestUri, (int)response.StatusCode,
                    string.Join(" | ", response.Headers.WwwAuthenticate.Select(h => h.ToString())),
                    detail.Length > 500 ? detail[..500] : detail);
            }
            if (response.IsSuccessStatusCode && request.RequestUri?.AbsolutePath.Contains("/.well-known/oauth-protected-resource", StringComparison.Ordinal) == true)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var fixedBody = Fix(body, endpoint);
                if (fixedBody is not null)
                {
                    _advertised = AdvertisedResource(body);
                    logger.LogInformation("Métadonnées OAuth de Forecast : ressource alignée sur {Endpoint}.", endpoint);
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
                    response.Content = new StringContent(fixedBody, Encoding.UTF8, mediaType);
                }
            }
            return response;
        }

        internal static string? AdvertisedResource(string json)
        {
            try
            {
                return JsonNode.Parse(json) is JsonObject metadata ? metadata["resource"]?.GetValue<string>() : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// The urlencoded <paramref name="parameters"/> with a "resource" equal to <paramref name="endpoint"/> replaced by
        /// <paramref name="original"/>, or null when there is nothing to replace.
        /// </summary>
        internal static string? RestoreResource(string parameters, Uri endpoint, string original)
        {
            var wanted = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
            var changed = false;
            var pairs = parameters.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == "resource"
                    && string.Equals(Uri.UnescapeDataString(kv[1].Replace('+', ' ')).TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    return "resource=" + Uri.EscapeDataString(original);
                }
                return pair;
            }).ToList();
            return changed ? string.Join('&', pairs) : null;
        }

        /// <summary>The metadata with "resource" replaced by <paramref name="endpoint"/>, or null when it needs no change.</summary>
        internal static string? Fix(string json, Uri endpoint)
        {
            try
            {
                if (JsonNode.Parse(json) is not JsonObject metadata
                    || metadata["resource"]?.GetValue<string>() is not { } resource
                    || !Uri.TryCreate(resource, UriKind.Absolute, out var advertised))
                    return null;

                var wanted = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
                var current = advertised.GetLeftPart(UriPartial.Path).TrimEnd('/');
                if (string.Equals(wanted, current, StringComparison.OrdinalIgnoreCase)
                    || !wanted.StartsWith(current + "/", StringComparison.OrdinalIgnoreCase))
                    return null;

                metadata["resource"] = endpoint.GetLeftPart(UriPartial.Path);
                return metadata.ToJsonString();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }

    private sealed class McpToolClient(McpClient client, TimeSpan requestTimeout) : IForecastToolClient
    {
        public async Task<IReadOnlyList<ForecastToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            using var cts = Timeout(cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
            return tools.Select(t => new ForecastToolInfo(t.Name, t.Description, t.JsonSchema)).ToList();
        }

        public async Task<ForecastToolResult> CallToolAsync(
            string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            using var cts = Timeout(cancellationToken);
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cts.Token);

            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            JsonElement? json = null;
            if (result.StructuredContent is { } structured)
                json = JsonSerializer.SerializeToElement(structured);
            else if (TryParseJson(text) is { } parsed)
                json = parsed;
            return new ForecastToolResult(result.IsError ?? false, text, json);
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();

        private CancellationTokenSource Timeout(CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(requestTimeout);
            return cts;
        }

        private static JsonElement? TryParseJson(string text)
        {
            var trimmed = text.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] is not ('{' or '['))
                return null;
            try
            {
                return JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>Authorization code flow with a loopback redirect: opens the system browser and waits for the redirect.</summary>
internal static class ForecastLoopbackAuthorization
{
    public static async Task<string?> AuthorizeAsync(
        Uri authorizationUri, Uri redirectUri, TimeSpan timeout, Action<string>? progress, ILogger logger, CancellationToken cancellationToken)
    {
        var prefix = redirectUri.GetLeftPart(UriPartial.Path);
        if (!prefix.EndsWith('/'))
            prefix += "/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Impossible d'écouter sur {prefix} (port occupé ?). Changez « Forecast:OAuthRedirectPort ». {ex.Message}", ex);
        }

        progress?.Invoke("Ouverture du navigateur pour la connexion à Forecast…");
        progress?.Invoke($"Si rien ne s'ouvre, copiez cette adresse dans votre navigateur : {authorizationUri}");
        try
        {
            Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Navigateur non ouvert.");
        }

        var expectedState = ParseQuery(authorizationUri.Query).GetValueOrDefault("state");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var stop = cts.Token.Register(listener.Stop);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("La connexion à Forecast n'a pas été terminée à temps.");
            }

            var query = ParseQuery(context.Request.Url?.Query ?? "");
            if (!query.ContainsKey("code") && !query.ContainsKey("error"))
            {
                await RespondAsync(context, HttpStatusCode.NotFound, "Page inconnue.");
                continue;
            }

            if (expectedState is not null && query.GetValueOrDefault("state") != expectedState)
            {
                await RespondAsync(context, HttpStatusCode.BadRequest, "Réponse inattendue : la connexion est ignorée.");
                continue;
            }

            if (query.TryGetValue("error", out var error))
            {
                await RespondAsync(context, HttpStatusCode.OK, $"Connexion refusée ({error}). Vous pouvez fermer cet onglet.");
                logger.LogWarning("Autorisation Forecast refusée : {Error} {Description}", error, query.GetValueOrDefault("error_description"));
                return null;
            }

            await RespondAsync(context, HttpStatusCode.OK, "Connexion à Forecast réussie. Vous pouvez fermer cet onglet et revenir à Time Sheet Helper.");
            return query["code"];
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    private static async Task RespondAsync(HttpListenerContext context, HttpStatusCode status, string message)
    {
        var html = $"<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>Time Sheet Helper</title><body style=\"font-family:sans-serif;margin:3em\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
