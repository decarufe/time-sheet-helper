using System.Diagnostics;

namespace TimeSheetHelper.App;

/// <summary>Opens the application's pages in the user's default browser.</summary>
public static class UiLauncher
{
    public const string OpenArgument = "--open";
    public const string DefaultUrl = "http://localhost:5178";

    /// <summary>First address of the <c>Urls</c> setting, reachable from this machine (a wildcard host becomes localhost).</summary>
    public static string ConfiguredUrl(IConfiguration configuration)
    {
        var first = configuration["Urls"]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? DefaultUrl : first.Replace("://*", "://localhost").Replace("://+", "://localhost");
    }

    public static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Settings as the application itself reads them, for a launch that does not build the whole host.</summary>
    public static IConfiguration ReadSettings() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
}
