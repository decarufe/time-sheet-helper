using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// <summary>Makes what the host provides resolvable in the plugin's own container.</summary>
    public static IServiceCollection AddPluginContext(this IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton(context);
        services.AddSingleton(context.Configuration);
        services.AddSingleton(context.Sink);
        services.AddSingleton(context.History);
        services.AddSingleton(context.Secrets);
        services.AddSingleton(context.LoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(context.Time);
        return services;
    }
}

/// <summary>
/// A source instance backed by the plugin's own service provider, disposed with it. Derive to add background work,
/// status details or actions.
/// </summary>
public class ServiceSourceInstance(ServiceProvider services, IReadOnlyList<ICollector>? collectors = null) : ISourceInstance
{
    protected IServiceProvider Services => services;

    public virtual IReadOnlyList<ICollector> Collectors { get; } = collectors ?? [];

    public virtual Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual async Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        foreach (var collector in Collectors)
        {
            var status = await collector.GetStatusAsync(cancellationToken);
            if (!status.IsConfigured)
                problems.Add(status.Message ?? $"{collector.Name} n'est pas configurée.");
        }

        return problems.Count == 0 ? new SourceStatus(true) : new SourceStatus(false, string.Join(" ", problems));
    }

    public virtual Task<SourceActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceActionResult(false, $"Action inconnue : {actionId}"));

    public virtual async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await services.DisposeAsync();
    }
}
