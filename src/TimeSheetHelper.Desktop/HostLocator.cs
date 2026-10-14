using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace TimeSheetHelper.Desktop;

/// <summary>Finds the background application the window shows, and starts it when it is not running.</summary>
internal static class HostLocator
{
    public const string DefaultUrl = "http://localhost:5178";
    public const string HostExeName = "TimeSheetHelper.exe";

    /// <summary>
    /// Address of the application: <c>--url</c> on the command line, else the first <c>Urls</c> of
    /// appsettings.Local.json then appsettings.json next to the executable (the host reads the same files), else the default.
    /// </summary>
    public static Uri ResolveUrl(string baseDirectory, IReadOnlyList<string> args)
    {
        var index = args.ToList().FindIndex(a => a.Equals("--url", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Count && TryHttp(args[index + 1], out var fromArgs))
            return fromArgs;

        foreach (var file in new[] { "appsettings.Local.json", "appsettings.json" })
        {
            var path = Path.Combine(baseDirectory, file);
            if (!File.Exists(path))
                continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.TryGetProperty("Urls", out var urls) && urls.ValueKind == JsonValueKind.String
                    && urls.GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } first
                    && TryHttp(first, out var configured))
                    return configured;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // An unreadable settings file must not stop the window from opening: try the next source.
            }
        }

        return new Uri(DefaultUrl);
    }

    /// <summary>True for a navigation that stays on the application (same scheme, host and port).</summary>
    public static bool IsSameOrigin(Uri candidate, Uri origin) =>
        candidate.IsAbsoluteUri
        && string.Equals(candidate.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == origin.Port;

    /// <summary>Links leaving the application open in the default browser, and only web links do.</summary>
    public static bool IsOpenableExternally(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static async Task<bool> IsListeningAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync(url.Host, url.Port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    /// Makes sure the application answers on <paramref name="url"/>, starting <paramref name="hostExe"/> when it does
    /// not. The application keeps running when the window closes: it records activity in the background.
    /// </summary>
    public static async Task EnsureRunningAsync(Uri url, string hostExe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (await IsListeningAsync(url, cancellationToken))
            return;

        if (!File.Exists(hostExe))
            throw new FileNotFoundException($"L'application Time Sheet Helper ({Path.GetFileName(hostExe)}) est introuvable à côté de la fenêtre.", hostExe);

        Process.Start(new ProcessStartInfo(hostExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostExe)!,
            CreateNoWindow = true,
        });

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, cancellationToken);
            if (await IsListeningAsync(url, cancellationToken))
                return;
        }

        throw new TimeoutException("L'application n'a pas répondu à temps. Vérifiez qu'elle n'est pas déjà ouverte sur un autre compte Windows.");
    }

    private static bool TryHttp(string text, out Uri uri) =>
        Uri.TryCreate(text.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out uri!)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
