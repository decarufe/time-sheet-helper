using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TimeSheetHelper.Collectors.Windows.VsCode;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Storage;

// Same namespace as Program.cs so wiring it up takes a single line.
namespace TimeSheetHelper.App;

/// <summary>
/// Local HTTP endpoints fed by editor extensions. Protected by a random token shared through the
/// Credential Manager (source of truth) and a file in %LOCALAPPDATA%\TimeSheetHelper that the extensions read.
/// </summary>
public static class IngestEndpoints
{
    public const string TokenHeader = "X-TimeSheet-Token";
    public const string TokenFileName = "ingest-token";

    public static string TokenFilePath => Path.Combine(DataPaths.Root, TokenFileName);

    public static WebApplication MapIngestEndpoints(this WebApplication app)
    {
        var token = EnsureToken(app.Services.GetRequiredService<ISecretStore>(), app.Logger);
        var expected = Encoding.UTF8.GetBytes(token);

        app.MapPost("/ingest/vscode", async (HttpContext context, VsCodeIngestService ingest, CancellationToken cancellationToken) =>
            {
                // Check the token before touching the body, so anonymous callers never reach the JSON parser.
                if (!IsAuthorized(context.Request, expected))
                    return Results.Unauthorized();

                VsCodeHeartbeat? heartbeat;
                try
                {
                    heartbeat = await context.Request.ReadFromJsonAsync<VsCodeHeartbeat>(cancellationToken);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return Results.BadRequest();
                }
                if (heartbeat is null)
                    return Results.BadRequest();

                await ingest.IngestAsync(heartbeat, cancellationToken);
                return Results.NoContent();
            })
            .DisableAntiforgery();

        return app;
    }

    /// <summary>Returns the ingest token, creating it on first start; always (re)writes the token file.</summary>
    internal static string EnsureToken(ISecretStore secrets, ILogger logger)
    {
        var token = secrets.Get(SecretNames.IngestToken);
        if (string.IsNullOrEmpty(token))
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            secrets.Set(SecretNames.IngestToken, token);
        }

        try
        {
            var path = TokenFilePath;
            if (!File.Exists(path) || File.ReadAllText(path).Trim() != token)
                File.WriteAllText(path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cannot write the ingest token file");
        }

        return token;
    }

    private static bool IsAuthorized(HttpRequest request, byte[] expected)
    {
        var provided = request.Headers[TokenHeader].ToString();
        return provided.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided.Trim()), expected);
    }
}
