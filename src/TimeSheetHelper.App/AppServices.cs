using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Collectors.DevOps;
using TimeSheetHelper.Collectors.Microsoft365;
using TimeSheetHelper.Collectors.Windows;
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
        services.AddWindowsCollectors(configuration);
        services.AddMicrosoft365Collectors(configuration);
        services.AddDevOpsCollectors(configuration);
        services.AddForecast(configuration);
        services.AddTimesheetEngine(configuration);
        return services;
    }

    public static async Task MigrateDatabaseAsync(IServiceProvider services)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<TimeSheetDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        await IgnoredTask.EnsureAsync(db, services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
    }
}
