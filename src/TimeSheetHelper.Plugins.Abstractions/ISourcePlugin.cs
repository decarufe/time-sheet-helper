using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Plugins;

/// <summary>Entry point of a source plugin: one public class with a parameterless constructor per source.</summary>
public interface ISourcePlugin
{
    SourceDescriptor Describe();

    /// <summary>
    /// Builds the running source. Called when the source is enabled or its settings change; the instance is disposed
    /// when it is disabled. Nothing must run before <see cref="ISourceInstance.StartAsync"/>.
    /// </summary>
    ISourceInstance Create(IPluginContext context);
}

/// <summary>What the host gives a source: its settings and the few services it may use.</summary>
public interface IPluginContext
{
    /// <summary>The settings of this source: appsettings defaults overridden by what the user saved.</summary>
    IConfiguration Configuration { get; }

    /// <summary>Where activities are written.</summary>
    IActivitySink Sink { get; }

    /// <summary>Read access to activities already recorded.</summary>
    IActivityHistory History { get; }

    ISecretStore Secrets { get; }

    ILoggerFactory LoggerFactory { get; }

    TimeProvider Time { get; }

    /// <summary>Folder the source may use for its own files (token caches, ...).</summary>
    string DataDirectory { get; }
}

/// <summary>A running source.</summary>
public interface ISourceInstance : IAsyncDisposable
{
    /// <summary>The pull collectors of the source (none for continuous and push sources).</summary>
    IReadOnlyList<ICollector> Collectors => [];

    /// <summary>Starts background work of a continuous source. Called once, after <see cref="IPluginContext"/> is ready.</summary>
    Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops background work; also done by <see cref="IAsyncDisposable.DisposeAsync"/>.</summary>
    Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Current state for the Sources page. The default derives it from the collectors: ready when every collector is.
    /// </summary>
    async Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
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

    /// <summary>Runs one of the actions listed in <see cref="SourceStatus.Actions"/>.</summary>
    Task<SourceActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceActionResult(false, $"Action inconnue : {actionId}"));
}

/// <summary>Implemented by the instance of a <see cref="SourceExecutionMode.Push"/> source.</summary>
public interface IPushReceiver
{
    /// <summary>Handles a request body already authenticated by the host. False when the payload is malformed.</summary>
    Task<bool> ReceiveAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

public sealed record StatusDetail(string Label, string Value);

/// <summary>A button shown on the source card (sign in, pause, ...).</summary>
public sealed record SourceAction(string Id, string Label, bool Primary = false);

public sealed record SourceActionResult(bool Ok, string? Message = null);

/// <param name="IsConfigured">False when something is missing; <paramref name="Message"/> then says what.</param>
/// <param name="Details">Facts shown on the card (current window, last signal, ...).</param>
/// <param name="Actions">Buttons shown on the card.</param>
public sealed record SourceStatus(
    bool IsConfigured,
    string? Message = null,
    IReadOnlyList<StatusDetail>? Details = null,
    IReadOnlyList<SourceAction>? Actions = null);
