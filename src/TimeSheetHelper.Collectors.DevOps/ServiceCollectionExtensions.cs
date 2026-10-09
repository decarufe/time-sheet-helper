using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.AzureDevOps;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Collectors.DevOps.GitHub;
using TimeSheetHelper.Collectors.DevOps.LocalGit;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.DevOps;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GitHub, Azure DevOps and local git collectors. Requires an <see cref="Core.Secrets.ISecretStore"/>,
    /// an <see cref="Core.Activities.IActivitySink"/> and the storage <see cref="Storage.ActivityStore"/>.
    /// </summary>
    public static IServiceCollection AddDevOpsCollectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
        services.Configure<LocalGitOptions>(configuration.GetSection(LocalGitOptions.SectionName));

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IActivityHistory, ActivityStoreHistory>();
        services.AddSingleton<GitHubTokenProvider>();

        services.AddHttpClient<GitHubApiClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<GitHubOptions>>().Value.ApiBaseUrl;
            http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TimeSheetHelper", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        services.AddHttpClient<AzureDevOpsApiClient>(http =>
        {
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TimeSheetHelper", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // Singleton collectors keep the typed client they were built with; acceptable for a long-running desktop process.
        services.AddSingleton<ICollector, GitHubCollector>();
        services.AddSingleton<ICollector, AzureDevOpsCollector>();
        services.AddSingleton<ICollector, LocalGitCollector>();
        return services;
    }
}
