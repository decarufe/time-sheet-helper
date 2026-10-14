using System.Text.Json;

namespace TimeSheetHelper.App.Plugins;

/// <summary>The <c>plugin.json</c> at the root of a plugin package (a folder or a .zip of that folder).</summary>
public sealed record PluginManifest
{
    /// <summary>Package identifier, also the folder name; lower-case letters, digits and dashes.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    /// <summary>Contract version the package was built against.</summary>
    public required int ApiVersion { get; init; }

    /// <summary>File name of the assembly holding the <c>ISourcePlugin</c> implementations, in the package root.</summary>
    public required string EntryAssembly { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads and validates a manifest; the message of the exception is meant for the user.</summary>
    public static PluginManifest Read(string path)
    {
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new PluginException($"plugin.json illisible : {ex.Message}");
        }

        if (manifest is null)
            throw new PluginException("plugin.json est vide.");
        if (!IsValidId(manifest.Id))
            throw new PluginException($"Identifiant de plugin invalide : « {manifest.Id} » (lettres minuscules, chiffres et tirets).");
        if (manifest.EntryAssembly.IndexOfAny(['/', '\\']) >= 0 || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new PluginException($"entryAssembly doit être un nom de fichier .dll à la racine du paquet : « {manifest.EntryAssembly} ».");
        return manifest;
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && id[0] != '-' && id[^1] != '-';
}

/// <summary>A problem with a plugin package, worded for the user.</summary>
public sealed class PluginException(string message) : Exception(message);
