using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.App.Plugins;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.App.Sync;
using TimeSheetHelper.Core;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.App;

/// <summary>Service registration shared by the tray app and the command line.</summary>
public static class AppServices
{
    public static void AddConfiguration(IConfigurationManager configuration) =>
        // Per-machine settings written by the installer (Microsoft 365 app registration, Azure DevOps organizations, ...).
        configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    public static IServiceCollection AddTimeSheetHelper(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTimeSheetStorage(DataPaths.DatabaseConnectionString);
        services.AddSingleton<ISecretStore, CredentialManagerSecretStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSourcePlugins();
        services.AddForecast(configuration);
        services.AddTimesheetEngine(configuration);
        return services;
    }

    /// <summary>
    /// Sources are plugins: <see cref="PluginHost"/> finds the packages, starts the ones switched on and feeds the
    /// catalog the engine reads the traits of the sources from. Registered before the engine so its catalog wins.
    /// </summary>
    public static IServiceCollection AddSourcePlugins(this IServiceCollection services)
    {
        services.AddSingleton(SourceCatalog.CreateDefault());
        services.AddSingleton<ISourceCatalog>(sp => sp.GetRequiredService<SourceCatalog>());
        services.AddSingleton(new PluginRoots(
            Path.Combine(AppContext.BaseDirectory, "plugins"),
            Path.Combine(DataPaths.Root, "plugins")));
        services.AddSingleton<PluginHost>();
        services.AddSingleton<ICollectorSource>(sp => sp.GetRequiredService<PluginHost>());
        return services;
    }

    public static async Task MigrateDatabaseAsync(IServiceProvider services)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<TimeSheetDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await IgnoredTask.EnsureAsync(db, services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
    }
}
