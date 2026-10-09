using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Collectors.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsCollectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FocusTrackerOptions>(configuration.GetSection(FocusTrackerOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretStore, CredentialManagerSecretStore>();
        services.AddSingleton<FileContextResolver>();
        services.AddSingleton<FocusTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<FocusTracker>());
        return services;
    }
}
