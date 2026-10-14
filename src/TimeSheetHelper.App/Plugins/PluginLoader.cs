using System.Reflection;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.App.Plugins;

/// <summary>A plugin package found on disk, with the source plugins it exports.</summary>
internal sealed class LoadedPackage
{
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required bool IsBundled { get; init; }
    public required PluginLoadContext Context { get; init; }
    public required IReadOnlyList<(ISourcePlugin Plugin, SourceDescriptor Descriptor)> Plugins { get; init; }
}

/// <summary>A folder that could not be loaded as a package, with the reason.</summary>
public sealed record PluginPackageIssue(string Directory, string Message);

internal static class PluginLoader
{
    /// <summary>Loads the package in <paramref name="directory"/>; throws <see cref="PluginException"/> when it is unusable.</summary>
    public static LoadedPackage Load(string directory, bool isBundled)
    {
        var manifestPath = Path.Combine(directory, PluginApi.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new PluginException($"{PluginApi.ManifestFileName} introuvable.");

        var manifest = PluginManifest.Read(manifestPath);
        if (manifest.ApiVersion != PluginApi.Version)
            throw new PluginException($"« {manifest.Name} » est prévu pour la version {manifest.ApiVersion} de l'API des plugins ; cette application utilise la version {PluginApi.Version}.");

        var assemblyPath = Path.Combine(directory, manifest.EntryAssembly);
        if (!File.Exists(assemblyPath))
            throw new PluginException($"Assembly introuvable : {manifest.EntryAssembly}.");

        var context = new PluginLoadContext(assemblyPath);
        try
        {
            var assembly = context.LoadFromFile(assemblyPath);
            var plugins = new List<(ISourcePlugin, SourceDescriptor)>();
            foreach (var type in ExportedTypes(assembly))
            {
                if (!typeof(ISourcePlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                    continue;

                var plugin = (ISourcePlugin)Activator.CreateInstance(type)!;
                var descriptor = plugin.Describe();
                Validate(descriptor, manifest);
                plugins.Add((plugin, descriptor));
            }

            if (plugins.Count == 0)
                throw new PluginException($"{manifest.EntryAssembly} n'exporte aucun plugin de source.");
            if (plugins.GroupBy(p => p.Item2.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
                throw new PluginException($"Le plugin « {duplicate.Key} » est déclaré deux fois.");

            return new LoadedPackage { Manifest = manifest, Directory = directory, IsBundled = isBundled, Context = context, Plugins = plugins };
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static void Validate(SourceDescriptor descriptor, PluginManifest manifest)
    {
        if (!PluginManifest.IsValidId(descriptor.Id))
            throw new PluginException($"Identifiant de source invalide dans « {manifest.Name} » : « {descriptor.Id} ».");
        if (descriptor.ApiVersion != PluginApi.Version)
            throw new PluginException($"La source « {descriptor.Id} » est prévue pour la version {descriptor.ApiVersion} de l'API des plugins.");
        if (descriptor.Mode == SourceExecutionMode.Push && string.IsNullOrWhiteSpace(descriptor.PushRoute))
            throw new PluginException($"La source « {descriptor.Id} » reçoit des données (Push) mais ne déclare pas de PushRoute.");
        if (descriptor.PushRoute is { } route && !PluginManifest.IsValidId(route))
            throw new PluginException($"PushRoute invalide pour « {descriptor.Id} » : « {route} ».");
        if (descriptor.Config.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new PluginException($"La source « {descriptor.Id} » déclare deux réglages avec la même clé.");
    }

    private static Type[] ExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A missing optional dependency must not hide the types that did load.
            return ex.Types.OfType<Type>().ToArray();
        }
    }

    /// <summary>Package folders (one level down) of a plugins root; folders starting with a dot are the application's own.</summary>
    public static IEnumerable<string> PackageDirectories(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.'))
            : [];

    /// <summary>Marker left in a package folder that could not be deleted while in use; cleaned at the next start.</summary>
    public const string RemovedMarker = ".removed";
}
