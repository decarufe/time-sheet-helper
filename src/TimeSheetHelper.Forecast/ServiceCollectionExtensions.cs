using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TimeSheetHelper.Forecast;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the connection to Forecast through its MCP server. Requires <c>AddTimeSheetStorage</c>.</summary>
    public static IServiceCollection AddForecast(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForecastOptions>(configuration.GetSection(ForecastOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ForecastAuthStore>();
        services.AddSingleton<IForecastConnector, ForecastMcpConnector>();
        services.AddSingleton<ForecastApi>();
        services.AddSingleton<ForecastTaskReader>();
        services.AddSingleton<ForecastTaskLinker>();
        services.AddSingleton<ForecastTaskImporter>();
        services.AddSingleton<ForecastTimesheetWriter>();
        services.AddSingleton<ForecastCommands>();
        return services;
    }
}
