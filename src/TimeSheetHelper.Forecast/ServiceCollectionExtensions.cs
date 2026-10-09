using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TimeSheetHelper.Forecast;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Forecast browser automation. Requires <c>AddTimeSheetStorage</c>.</summary>
    public static IServiceCollection AddForecast(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForecastOptions>(configuration.GetSection(ForecastOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ForecastSession>();
        services.AddSingleton<ForecastDiscovery>();
        services.AddSingleton<ForecastTaskReader>();
        services.AddSingleton<ForecastTimesheetWriter>();
        services.AddSingleton<ForecastCommands>();
        return services;
    }
}
