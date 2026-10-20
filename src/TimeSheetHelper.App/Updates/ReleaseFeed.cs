using System.Text.Json;

namespace TimeSheetHelper.App.Updates;

public sealed record ReleaseInfo(
    AppVersion Version,
    string Tag,
    string PageUrl,
    string MsiName,
    string MsiUrl,
    string? ChecksumsUrl);

/// <summary>Reads the GitHub "list releases" payload and the SHA256SUMS.txt asset.</summary>
public static class ReleaseFeed
{
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>The newest installable release strictly newer than <paramref name="current"/>, or null.</summary>
    public static ReleaseInfo? FindUpdate(string json, AppVersion current)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        ReleaseInfo? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft"))
                continue;
            if (!AppVersion.TryParse(Text(release, "tag_name"), out var version))
                continue;
            // Stable users are never offered a prerelease; prerelease users follow every release.
            if ((version.IsPrerelease || Bool(release, "prerelease")) && !current.IsPrerelease)
                continue;
            if (version <= current || (best is not null && version <= best.Version))
                continue;

            string? msiName = null, msiUrl = null, checksumsUrl = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    var url = Text(asset, "browser_download_url");
                    if (name is null || url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        (msiName, msiUrl) = (name, url);
                    else if (name.Equals(ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                        checksumsUrl = url;
                }
            }
            if (msiName is null || msiUrl is null)
                continue;

            best = new ReleaseInfo(version, Text(release, "tag_name")!, Text(release, "html_url") ?? "", msiName, msiUrl, checksumsUrl);
        }
        return best;
    }

    /// <summary>Looks up <paramref name="fileName"/> in "sha256sum" output ("hash  name" or "hash *name").</summary>
    public static string? FindChecksum(string checksums, string fileName)
    {
        foreach (var raw in checksums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0)
                continue;
            var name = line[(space + 1)..].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return line[..space].ToLowerInvariant();
        }
        return null;
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.True;
}
