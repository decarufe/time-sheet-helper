using System.Text;
using Microsoft.Extensions.Configuration;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.TestPlugin;

/// <summary>A pull source whose collector reports what it was configured with.</summary>
public sealed class EchoSourcePlugin : ISourcePlugin
{
    public static readonly ActivitySource Echo = new("Echo");

    public SourceDescriptor Describe() => new()
    {
        Id = "echo",
        DisplayName = "Écho",
        Description = "Source de test.",
        Emits = [new(Echo, new("Écho", SourceBehavior.Presence, DefaultWeight: 2.5))],
        Config =
        [
            new("Greeting", "Salutation", ConfigFieldType.Text, Required: true),
            new("Items", "Éléments", ConfigFieldType.TextList),
            new("Retries", "Essais", ConfigFieldType.Number),
        ],
        Secrets = [new("TimeSheetHelper:Echo", "Jeton d'écho")],
        LegacyConfigSection = "Echo",
    };

    public ISourceInstance Create(IPluginContext context) => new Instance(context);

    private sealed class Instance(IPluginContext context) : ISourceInstance
    {
        public IReadOnlyList<ICollector> Collectors { get; } = [new EchoCollector(context.Configuration)];

        public Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceStatus(true, null, [new("Salutation", context.Configuration["Greeting"] ?? "")], [new("ping", "Ping")]));

        public Task<SourceActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(actionId == "ping" ? new SourceActionResult(true, "pong") : new SourceActionResult(false, "inconnue"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EchoCollector(IConfiguration configuration) : ICollector
    {
        public string Name => "echo";

        public Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CollectorStatus(true));

        public Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        {
            var items = configuration.GetSection("Items").GetChildren().Select(c => c.Value).Where(v => v is not null).ToList();
            return Task.FromResult(new CollectorResult(items.Count, [$"{configuration["Greeting"]}|{string.Join(',', items)}|{configuration["Retries"]}"]));
        }
    }
}

/// <summary>A source that cannot start.</summary>
public sealed class FailingSourcePlugin : ISourcePlugin
{
    public SourceDescriptor Describe() => new() { Id = "failing", DisplayName = "En panne" };

    public ISourceInstance Create(IPluginContext context) => throw new InvalidOperationException("boom");
}

/// <summary>A source for another platform.</summary>
public sealed class OtherPlatformSourcePlugin : ISourcePlugin
{
    public SourceDescriptor Describe() => new() { Id = "elsewhere", DisplayName = "Ailleurs", Platform = "linux" };

    public ISourceInstance Create(IPluginContext context) => throw new InvalidOperationException("must not be created");
}

/// <summary>A push source that accepts any non-empty body.</summary>
public sealed class PushSourcePlugin : ISourcePlugin
{
    public SourceDescriptor Describe() => new()
    {
        Id = "pusher",
        DisplayName = "Réception",
        Mode = SourceExecutionMode.Push,
        PushRoute = "pusher",
        EnabledByDefault = false,
    };

    public ISourceInstance Create(IPluginContext context) => new Instance();

    private sealed class Instance : ISourceInstance, IPushReceiver
    {
        public Task<bool> ReceiveAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
            Task.FromResult(Encoding.UTF8.GetString(body.Span).Trim().Length > 0);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
