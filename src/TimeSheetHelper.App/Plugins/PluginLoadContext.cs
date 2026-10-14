using System.Reflection;
using System.Runtime.Loader;

namespace TimeSheetHelper.App.Plugins;

/// <summary>
/// Isolated load context of one plugin package: its own dependencies come from its folder, while the contract, the
/// application's core types and the .NET libraries always come from the host so types match across the boundary.
/// Collectible, so an uninstalled package can be unloaded and its files deleted.
/// </summary>
internal sealed class PluginLoadContext(string entryAssemblyPath)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(entryAssemblyPath), isCollectible: true)
{
    private static readonly string[] HostPrefixes =
    [
        "System.", "Microsoft.Extensions.", "Microsoft.AspNetCore.", "Microsoft.Win32.", "netstandard",
        "TimeSheetHelper.Core", "TimeSheetHelper.Plugins",
    ];

    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || IsFromHost(name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromFile(path);
    }

    /// <summary>
    /// Loads from memory rather than from the path: the files stay unlocked, so a package can be replaced or deleted
    /// while the application runs (the symbols are read too, to keep stack traces readable).
    /// </summary>
    public Assembly LoadFromFile(string path)
    {
        using var assembly = new MemoryStream(File.ReadAllBytes(path));
        var symbols = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(symbols))
            return LoadFromStream(assembly);

        using var pdb = new MemoryStream(File.ReadAllBytes(symbols));
        return LoadFromStream(assembly, pdb);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsFromHost(string name) =>
        HostPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
        || Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
}
