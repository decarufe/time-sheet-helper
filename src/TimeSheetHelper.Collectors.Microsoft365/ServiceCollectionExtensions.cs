using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.Microsoft365;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the calendar, email and Teams collectors and <see cref="GraphAuthenticator"/> (for the UI sign-in).
    /// Requires an <c>IActivitySink</c> (see AddTimeSheetStorage).
    /// </summary>
    public static IServiceCollection AddMicrosoft365Collectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Microsoft365Options>(configuration.GetSection(Microsoft365Options.SectionName));
        services.AddHttpClient(GraphClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(100));
        services.AddSingleton<GraphAuthenticator>();
        services.AddSingleton<IGraphAccessTokenProvider>(sp => sp.GetRequiredService<GraphAuthenticator>());
        services.AddSingleton<GraphClient>();
        services.AddSingleton<ICollector, CalendarCollector>();
        services.AddSingleton<ICollector, EmailCollector>();
        services.AddSingleton<ICollector, TeamsCollector>();
        return services;
    }
}
