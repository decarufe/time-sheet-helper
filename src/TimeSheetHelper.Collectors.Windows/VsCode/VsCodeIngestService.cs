using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Windows.VsCode;

/// <summary>Receives heartbeats from the ingest endpoint, aggregates them and persists the resulting segments.</summary>
public sealed class VsCodeIngestService(IActivitySink sink, TimeProvider time, ILogger<VsCodeIngestService> logger)
{
    // The client runs on the same machine, but never trust a timestamp far in the future.
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VsCodeSessionAggregator _aggregator = new();

    public async Task IngestAsync(VsCodeHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var at = heartbeat.TimestampUtc switch
        {
            { Ticks: 0 } => now,
            { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            var other => DateTime.SpecifyKind(other, DateTimeKind.Utc),
        };
        if (at > now + MaxClockSkew)
            at = now;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var activity in _aggregator.Observe(heartbeat with { TimestampUtc = at }))
            {
                try
                {
                    // Not cancellable: the aggregator already handed out this instance and expects it to be stored.
                    await sink.WriteAsync(activity, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist VS Code segment");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
