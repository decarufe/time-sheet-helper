using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TimeSheetHelper.Collectors.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsCollectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FocusTrackerOptions>(configuration.GetSection(FocusTrackerOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FileContextResolver>();
        services.AddSingleton<FocusTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<FocusTracker>());
        return services;
    }
}
