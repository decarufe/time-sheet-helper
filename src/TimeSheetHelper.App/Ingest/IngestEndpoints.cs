using System.Security.Cryptography;
using System.Text;
using TimeSheetHelper.App.Plugins;
using TimeSheetHelper.Core;
using TimeSheetHelper.Core.Secrets;

// Same namespace as Program.cs so wiring it up takes a single line.
namespace TimeSheetHelper.App;

/// <summary>
/// Local HTTP endpoint fed by editor extensions: <c>POST /ingest/{route}</c> goes to the running push source that
/// declares that route. Protected by a random token shared through the Credential Manager (source of truth) and a
/// file in %LOCALAPPDATA%\TimeSheetHelper that the extensions read.
/// </summary>
public static class IngestEndpoints
{
    public const string TokenHeader = "X-TimeSheet-Token";
    public const string TokenFileName = "ingest-token";

    /// <summary>Largest accepted body: a heartbeat is a few hundred bytes.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    public static string TokenFilePath => Path.Combine(DataPaths.Root, TokenFileName);

    public static WebApplication MapIngestEndpoints(this WebApplication app)
    {
        var token = EnsureToken(app.Services.GetRequiredService<ISecretStore>(), app.Logger);
        var expected = Encoding.UTF8.GetBytes(token);

        app.MapPost("/ingest/{route}", async (string route, HttpContext context, PluginHost plugins, CancellationToken cancellationToken) =>
            {
                // Check the token before touching the body, so anonymous callers never reach a plugin's parser.
                if (!IsAuthorized(context.Request, expected))
                    return Results.Unauthorized();

                var receiver = plugins.FindPushReceiver(route);
                if (receiver is null)
                    return Results.NotFound();

                if (context.Request.ContentLength > MaxBodyBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (body.Length + read > MaxBodyBytes)
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    body.Write(buffer, 0, read);
                }

                return await receiver.ReceiveAsync(body.ToArray(), cancellationToken) ? Results.NoContent() : Results.BadRequest();
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
