using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.App.Sync;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Core.Plugins;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Plugins;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.App.Plugins;

/// <summary>Where plugin packages are looked for: the ones shipped with the application, then the ones the user installed.</summary>
public sealed record PluginRoots(string BundledRoot, string UserRoot);

public enum PluginRunState
{
    Disabled,
    Running,
    Failed,

    /// <summary>The source cannot run on this platform.</summary>
    Unsupported,
}

/// <summary>One source plugin: what it declares, whether it is on, and its running instance.</summary>
public sealed class PluginState
{
    internal PluginState(ISourcePlugin plugin, SourceDescriptor descriptor, PluginManifest package, bool isBundled)
    {
        Plugin = plugin;
        Descriptor = descriptor;
        Package = package;
        IsBundled = isBundled;
    }

    internal ISourcePlugin Plugin { get; }
    internal ISourceInstance? Instance { get; set; }
    internal Dictionary<string, string?> Overrides { get; set; } = [];

    public SourceDescriptor Descriptor { get; }
    public PluginManifest Package { get; }
    public bool IsBundled { get; }
    public bool Enabled { get; internal set; }
    public PluginRunState State { get; internal set; }
    public string? Error { get; internal set; }

    public string Id => Descriptor.Id;

    public IReadOnlyList<ICollector> Collectors => Instance?.Collectors ?? [];
}

public sealed record PluginPackageInfo(PluginManifest Manifest, string Directory, bool IsBundled);

public sealed record PluginInstallResult(string PackageId, string Message);

/// <summary>
/// Discovers plugin packages, keeps track of which sources are switched on, and starts or stops them on demand
/// without restarting the application. A plugin that fails never prevents the others from running.
/// </summary>
public sealed class PluginHost(
    PluginRoots roots,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    IConfiguration configuration,
    IActivitySink sink,
    IActivityHistory history,
    ISecretStore secrets,
    ILoggerFactory loggerFactory,
    TimeProvider time,
    SourceCatalog catalog,
    ILogger<PluginHost> logger) : IHostedService, ICollectorSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LoadedPackage> _packages = [];
    private readonly List<PluginState> _states = [];
    private readonly List<PluginPackageIssue> _issues = [];

    /// <summary>Raised (from any thread) when a plugin is added, removed, switched or reconfigured.</summary>
    public event Action? Changed;

    public IReadOnlyList<PluginState> Plugins { get { lock (_states) return _states.OrderBy(s => s.Descriptor.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(); } }

    public IReadOnlyList<PluginPackageInfo> Packages { get { lock (_states) return _packages.Select(p => new PluginPackageInfo(p.Manifest, p.Directory, p.IsBundled)).ToList(); } }

    public IReadOnlyList<PluginPackageIssue> Issues { get { lock (_states) return _issues.ToList(); } }

    /// <summary>Pull collectors of every running source.</summary>
    public IReadOnlyList<ICollector> Collectors => Plugins.Where(p => p.State == PluginRunState.Running).SelectMany(p => p.Collectors).ToList();

    public PluginState? Find(string pluginId) => Plugins.FirstOrDefault(p => p.Id == pluginId);

    /// <summary>The plugin that owns a pull collector, for grouping on the Sources page.</summary>
    public PluginState? OwnerOf(string collectorName) =>
        Plugins.FirstOrDefault(p => p.Collectors.Any(c => c.Name.Equals(collectorName, StringComparison.OrdinalIgnoreCase)));

    public Task StartAsync(CancellationToken cancellationToken) => LoadAsync(pullOnly: false, cancellationToken);

    /// <summary>
    /// Loads the packages and starts only the enabled pull sources: a command-line run must not start window tracking
    /// or listen for editor extensions.
    /// </summary>
    public Task StartForCommandLineAsync(CancellationToken cancellationToken) => LoadAsync(pullOnly: true, cancellationToken);

    private async Task LoadAsync(bool pullOnly, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // User-installed packages first: they replace a bundled package of the same id.
            foreach (var directory in PluginLoader.PackageDirectories(roots.UserRoot))
                TryAdd(directory, isBundled: false);
            foreach (var directory in PluginLoader.PackageDirectories(roots.BundledRoot))
                TryAdd(directory, isBundled: true);

            var rows = await LoadRowsAsync(cancellationToken);
            foreach (var state in Plugins)
                ApplyRow(state, rows.GetValueOrDefault(state.Id));

            foreach (var state in Plugins.Where(s => s.Enabled && (!pullOnly || s.Descriptor.Mode == SourceExecutionMode.Pull)))
                await ActivateAsync(state, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var state in Plugins)
                await DeactivateAsync(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Find(pluginId) ?? throw new ArgumentException($"Plugin inconnu : {pluginId}", nameof(pluginId));
            await SaveRowAsync(state.Id, enabled, null, cancellationToken);
            state.Enabled = enabled;
            if (enabled)
                await ActivateAsync(state, cancellationToken);
            else
                await DeactivateAsync(state);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Current value of each setting of a source, as the form shows it.</summary>
    public IReadOnlyDictionary<string, string?> GetConfigValues(string pluginId) =>
        Find(pluginId) is { } state
            ? PluginConfig.ReadValues(state.Descriptor, PluginConfig.Build(state.Descriptor, configuration, state.Overrides))
            : new Dictionary<string, string?>();

    /// <summary>
    /// Saves the settings given (the others keep their saved or default value) and restarts the source when it is
    /// running. A null or blank value for a setting without default clears it.
    /// </summary>
    public async Task SaveConfigAsync(string pluginId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Find(pluginId) ?? throw new ArgumentException($"Plugin inconnu : {pluginId}", nameof(pluginId));
            var known = state.Descriptor.Config.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
            var merged = new Dictionary<string, string?>(state.Overrides, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values.Where(kv => known.ContainsKey(kv.Key)))
                merged[key] = value?.Trim();

            var effective = PluginConfig.ReadValues(state.Descriptor, PluginConfig.Build(state.Descriptor, configuration, merged));
            foreach (var field in state.Descriptor.Config.Where(f => f.Required))
                if (string.IsNullOrWhiteSpace(effective.GetValueOrDefault(field.Key)))
                    throw new ArgumentException($"« {field.Label} » est obligatoire.");
            foreach (var field in state.Descriptor.Config.Where(f => f.Type == ConfigFieldType.Number))
                if (effective.GetValueOrDefault(field.Key) is { Length: > 0 } text && !double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException($"« {field.Label} » doit être un nombre.");

            await SaveRowAsync(state.Id, null, merged, cancellationToken);
            state.Overrides = merged;
            if (state.State is PluginRunState.Running or PluginRunState.Failed)
            {
                await DeactivateAsync(state);
                await ActivateAsync(state, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task<SourceStatus?> GetStatusAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var instance = Find(pluginId)?.Instance;
        if (instance is null)
            return null;
        try
        {
            return await instance.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new SourceStatus(false, $"État indisponible : {ex.Message}");
        }
    }

    public async Task<SourceActionResult> ExecuteActionAsync(string pluginId, string actionId, CancellationToken cancellationToken = default)
    {
        var instance = Find(pluginId)?.Instance ?? throw new InvalidOperationException("Cette source n'est pas active.");
        var result = await instance.ExecuteActionAsync(actionId, cancellationToken);
        Changed?.Invoke();
        return result;
    }

    /// <summary>The running push source listening on <c>/ingest/{route}</c>, if any.</summary>
    public IPushReceiver? FindPushReceiver(string route) =>
        Plugins.Where(p => p.State == PluginRunState.Running && string.Equals(p.Descriptor.PushRoute, route, StringComparison.Ordinal))
            .Select(p => p.Instance as IPushReceiver)
            .FirstOrDefault(r => r is not null);

    /// <summary>Installs a package from a folder or a .zip; a package of the same id is replaced.</summary>
    public async Task<PluginInstallResult> InstallAsync(string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            var source = path;
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                staging = Path.Combine(roots.UserRoot, ".staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ZipFile.ExtractToDirectory(path, staging);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    throw new PluginException($"Archive illisible : {ex.Message}");
                }

                source = FindPackageRoot(staging);
            }
            else if (!Directory.Exists(path))
                throw new PluginException("Indiquez un dossier de plugin ou un fichier .zip.");

            // Validate before touching anything installed: load it in a throw-away context.
            var probe = PluginLoader.Load(source, isBundled: false);
            var manifest = probe.Manifest;
            probe.Context.Unload();

            if (_packages.FirstOrDefault(p => p.Manifest.Id == manifest.Id) is { } existing)
                await RemovePackageAsync(existing);

            var target = Path.Combine(roots.UserRoot, manifest.Id);
            if (Directory.Exists(target))
                DeleteDirectory(target);
            CopyDirectory(source, target);

            var added = TryAdd(target, isBundled: false) ?? throw new PluginException(_issues.LastOrDefault()?.Message ?? "Chargement impossible.");
            var rows = await LoadRowsAsync(cancellationToken);
            foreach (var state in Plugins.Where(s => s.Package.Id == added.Manifest.Id))
            {
                ApplyRow(state, rows.GetValueOrDefault(state.Id));
                if (state.Enabled)
                    await ActivateAsync(state, cancellationToken);
            }

            logger.LogInformation("Paquet de plugin installé : {Package} {Version}", manifest.Id, manifest.Version);
            return new PluginInstallResult(manifest.Id, $"« {manifest.Name} » {manifest.Version} installé.");
        }
        finally
        {
            if (staging is not null)
                TryDelete(staging);
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Removes a user-installed package. Bundled packages can only be switched off.</summary>
    public async Task UninstallAsync(string packageId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var package = _packages.FirstOrDefault(p => p.Manifest.Id == packageId)
                ?? throw new PluginException($"Paquet inconnu : {packageId}");
            if (package.IsBundled)
                throw new PluginException("Un plugin fourni avec l'application ne peut pas être désinstallé ; désactivez-le.");

            await RemovePackageAsync(package);
            if (Directory.Exists(package.Directory) && !DeleteDirectory(package.Directory))
                File.WriteAllText(Path.Combine(package.Directory, PluginLoader.RemovedMarker), "");
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private LoadedPackage? TryAdd(string directory, bool isBundled)
    {
        var name = Path.GetFileName(directory);
        if (File.Exists(Path.Combine(directory, PluginLoader.RemovedMarker)))
        {
            DeleteDirectory(directory);
            return null;
        }

        try
        {
            var package = PluginLoader.Load(directory, isBundled);
            lock (_states)
            {
                if (_packages.Any(p => p.Manifest.Id == package.Manifest.Id))
                {
                    package.Context.Unload();
                    return null;
                }

                if (package.Plugins.FirstOrDefault(p => _states.Any(s => s.Id == p.Descriptor.Id)) is { Descriptor: { } clash })
                {
                    package.Context.Unload();
                    _issues.Add(new(directory, $"La source « {clash.Id} » existe déjà dans un autre paquet."));
                    return null;
                }

                _packages.Add(package);
                foreach (var (plugin, descriptor) in package.Plugins)
                {
                    _states.Add(new PluginState(plugin, descriptor, package.Manifest, isBundled));
                    foreach (var emitted in descriptor.Emits)
                        catalog.Register(emitted.Source, emitted.Traits);
                }
            }

            return package;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paquet de plugin ignoré : {Directory}", directory);
            var message = ex is PluginException ? ex.Message : $"{ex.GetType().Name} : {ex.Message}";
            lock (_states)
                _issues.Add(new(name, message));
            return null;
        }
    }

    private async Task RemovePackageAsync(LoadedPackage package)
    {
        foreach (var state in Plugins.Where(s => s.Package.Id == package.Manifest.Id))
        {
            await DeactivateAsync(state);
            lock (_states)
                _states.Remove(state);
        }

        lock (_states)
            _packages.Remove(package);
        package.Context.Unload();
        // A collectible context is only released once nothing references it; give the GC a chance before deleting files.
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private void ApplyRow(PluginState state, InstalledPlugin? row)
    {
        state.Enabled = row?.Enabled ?? state.Descriptor.EnabledByDefault;
        state.Overrides = PluginConfig.ParseOverrides(row?.ConfigJson);
    }

    private async Task ActivateAsync(PluginState state, CancellationToken cancellationToken)
    {
        if (state.Instance is not null)
            return;

        if (state.Descriptor.Platform is { } platform && !IsSupportedPlatform(platform))
        {
            state.State = PluginRunState.Unsupported;
            state.Error = $"Cette source ne fonctionne pas sur cette plateforme ({platform}).";
            return;
        }

        ISourceInstance? instance = null;
        try
        {
            var context = new PluginContext(
                PluginConfig.Build(state.Descriptor, configuration, state.Overrides),
                sink, history, secrets, loggerFactory, time,
                Path.Combine(roots.UserRoot, ".data", state.Id));
            instance = state.Plugin.Create(context);
            await instance.StartAsync(cancellationToken);
            state.Instance = instance;
            state.State = PluginRunState.Running;
            state.Error = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "La source {Plugin} n'a pas pu démarrer", state.Id);
            state.State = PluginRunState.Failed;
            state.Error = ex.Message;
            state.Instance = null;
            if (instance is not null)
                await DisposeQuietlyAsync(instance, state.Id);
        }
    }

    private static bool IsSupportedPlatform(string platform) =>
        string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows();

    private async Task DeactivateAsync(PluginState state)
    {
        var instance = state.Instance;
        state.Instance = null;
        if (instance is not null)
            await DisposeQuietlyAsync(instance, state.Id);
        state.State = state.State == PluginRunState.Unsupported ? PluginRunState.Unsupported : PluginRunState.Disabled;
        state.Error = state.State == PluginRunState.Unsupported ? state.Error : null;
    }

    private async Task DisposeQuietlyAsync(ISourceInstance instance, string pluginId)
    {
        try
        {
            await instance.StopAsync(CancellationToken.None);
            await instance.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Arrêt de la source {Plugin} en erreur", pluginId);
        }
    }

    private async Task<Dictionary<string, InstalledPlugin>> LoadRowsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.InstalledPlugins.AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);
    }

    private async Task SaveRowAsync(string pluginId, bool? enabled, IReadOnlyDictionary<string, string?>? config, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.InstalledPlugins.FirstOrDefaultAsync(p => p.Id == pluginId, cancellationToken);
        if (row is null)
        {
            var state = Find(pluginId);
            row = new InstalledPlugin { Id = pluginId, Enabled = state?.Enabled ?? state?.Descriptor.EnabledByDefault ?? true };
            db.InstalledPlugins.Add(row);
        }

        if (enabled is { } value)
            row.Enabled = value;
        if (config is not null)
            row.ConfigJson = PluginConfig.SerializeOverrides(config);
        row.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The folder holding plugin.json: the root of an extracted archive, or its only subfolder.</summary>
    private static string FindPackageRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, PluginApi.ManifestFileName)))
            return extracted;
        var inner = Directory.GetDirectories(extracted);
        if (inner.Length == 1 && File.Exists(Path.Combine(inner[0], PluginApi.ManifestFileName)))
            return inner[0];
        throw new PluginException($"{PluginApi.ManifestFileName} introuvable dans l'archive.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static bool DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string directory) => DeleteDirectory(directory);
}
