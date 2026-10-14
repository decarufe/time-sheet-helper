using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Plugins;

public static class PluginApi
{
    /// <summary>Version of this contract. A plugin built for another major version is refused at load time.</summary>
    public const int Version = 1;

    /// <summary>Name of the manifest file at the root of a plugin package.</summary>
    public const string ManifestFileName = "plugin.json";
}

/// <summary>How a source produces activities.</summary>
public enum SourceExecutionMode
{
    /// <summary>Queried for a time range on demand and periodically (<see cref="Core.Collectors.ICollector"/>).</summary>
    Pull,

    /// <summary>Runs in the background while enabled (<see cref="ISourceInstance.StartAsync"/>), e.g. window tracking.</summary>
    Continuous,

    /// <summary>Receives data posted to <c>/ingest/{route}</c> (<see cref="IPushReceiver"/>), e.g. an editor extension.</summary>
    Push,
}

public enum ConfigFieldType
{
    Text,
    Number,
    Boolean,

    /// <summary>A list of text values, one per line in the UI.</summary>
    TextList,
}

/// <summary>A non-secret setting of a source. Bound from the plugin's <see cref="IPluginContext.Configuration"/> section.</summary>
/// <param name="Key">Configuration key (a list is bound as <c>Key:0</c>, <c>Key:1</c>, ...).</param>
public sealed record ConfigField(
    string Key,
    string Label,
    ConfigFieldType Type = ConfigFieldType.Text,
    bool Required = false,
    string? Help = null,
    string? Placeholder = null);

/// <summary>A token or key the source needs, kept in the <see cref="Core.Secrets.ISecretStore"/> and never shown.</summary>
/// <param name="Name">Name in the secret store.</param>
public sealed record SecretRequirement(string Name, string Label, string? Help = null, bool Required = false);

/// <summary>An activity source a plugin emits, with how the timesheet engine should treat it.</summary>
public sealed record EmittedSource(ActivitySource Source, SourceTraits Traits);

/// <summary>
/// Everything the application needs to know about a source without running it: what it is, what it produces,
/// what it needs to be configured and where it can run. This is the requirements contract of a plugin.
/// </summary>
public sealed record SourceDescriptor
{
    /// <summary>Stable unique identifier, lower-case with dashes (e.g. "github").</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Contract version this plugin was built against (<see cref="PluginApi.Version"/>).</summary>
    public int ApiVersion { get; init; } = PluginApi.Version;

    public SourceExecutionMode Mode { get; init; } = SourceExecutionMode.Pull;

    /// <summary>"windows" when the source only works there; null when it runs anywhere.</summary>
    public string? Platform { get; init; }

    /// <summary>Activity sources emitted (their identifiers are stored with the activities).</summary>
    public IReadOnlyList<EmittedSource> Emits { get; init; } = [];

    public IReadOnlyList<ConfigField> Config { get; init; } = [];

    public IReadOnlyList<SecretRequirement> Secrets { get; init; } = [];

    /// <summary>
    /// Section of appsettings.json / appsettings.Local.json used as defaults for the settings, so configuration written
    /// before sources became plugins keeps working. Settings edited in the UI override it.
    /// </summary>
    public string? LegacyConfigSection { get; init; }

    /// <summary>Route under <c>/ingest/</c> for <see cref="SourceExecutionMode.Push"/> sources.</summary>
    public string? PushRoute { get; init; }

    /// <summary>Started the first time the application runs after installation.</summary>
    public bool EnabledByDefault { get; init; } = true;
}
