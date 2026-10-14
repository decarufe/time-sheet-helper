using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.App.Plugins;

/// <summary>
/// Settings of a source: the appsettings section named by its descriptor gives the defaults, what the user saved
/// from the UI (stored as JSON) overrides them. Lists are stored newline-separated.
/// </summary>
internal static class PluginConfig
{
    public static Dictionary<string, string?> ParseOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeOverrides(IReadOnlyDictionary<string, string?> values) => JsonSerializer.Serialize(values);

    public static IConfiguration Build(SourceDescriptor descriptor, IConfiguration host, IReadOnlyDictionary<string, string?> overrides)
    {
        var defaults = descriptor.LegacyConfigSection is { Length: > 0 } section
            ? new ConfigurationBuilder().AddConfiguration(host.GetSection(section)).Build()
            : new ConfigurationBuilder().Build();

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.Config)
        {
            if (!overrides.TryGetValue(field.Key, out var value))
                continue;

            if (field.Type != ConfigFieldType.TextList)
            {
                data[field.Key] = value;
                continue;
            }

            var items = SplitLines(value);
            for (var i = 0; i < items.Count; i++)
                data[$"{field.Key}:{i}"] = items[i];

            // A shorter saved list must not inherit the tail of the default one.
            foreach (var child in defaults.GetSection(field.Key).GetChildren())
                if (int.TryParse(child.Key, out var index) && index >= items.Count)
                    data[$"{field.Key}:{index}"] = null;
        }

        return new ConfigurationBuilder().AddConfiguration(defaults).AddInMemoryCollection(data).Build();
    }

    /// <summary>Current value of every field, as shown in the settings form.</summary>
    public static Dictionary<string, string?> ReadValues(SourceDescriptor descriptor, IConfiguration effective)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.Config)
        {
            values[field.Key] = field.Type == ConfigFieldType.TextList
                ? string.Join('\n', effective.GetSection(field.Key).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
                : effective[field.Key];
        }

        return values;
    }

    public static List<string> SplitLines(string? value) =>
        (value ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
