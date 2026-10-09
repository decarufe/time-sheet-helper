using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.App.Sync;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Background sync of the on-demand collectors; false keeps only the manual buttons.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time between two background runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Delay before the first background run after startup.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(2);
}

public enum CollectorSyncOutcome
{
    Succeeded,

    /// <summary>The collector reported missing credentials/configuration; it was not called.</summary>
    NotConfigured,

    Failed,
}

/// <summary>Last sync of one collector (kept in memory, shown on the Sources page).</summary>
public sealed record CollectorSyncResult(
    string Collector,
    CollectorSyncOutcome Outcome,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    IReadOnlyList<DateOnly> Weeks,
    int EventCount,
    IReadOnlyList<string> Warnings,
    string? Message = null);

/// <summary>UTC range of a local week (Monday 00:00 to next Monday 00:00).</summary>
public sealed record SyncWeek(DateOnly WeekStart, DateTime FromUtc, DateTime ToUtc);

/// <summary>
/// Periodically syncs every configured <see cref="ICollector"/> for the current week (and the previous week on
/// Monday mornings, to catch Friday evening activity). Runs never overlap: the timer skips a tick while a run is in
/// progress, and <see cref="RunNowAsync"/> waits for it. A failing collector never stops the others.
/// </summary>
public sealed class SyncScheduler(
    IEnumerable<ICollector> collectors,
    IOptions<SyncOptions> options,
    TimeProvider timeProvider,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    private readonly IReadOnlyList<ICollector> _collectors = collectors.ToList();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CollectorSyncResult> _lastResults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (from any thread) when a run starts, a collector finishes or a run ends.</summary>
    public event Action? Changed;

    public IReadOnlyList<ICollector> Collectors => _collectors;

    public bool IsRunning { get; private set; }

    /// <summary>Collector being synced right now, if any.</summary>
    public string? CurrentCollector { get; private set; }

    public DateTime? LastRunUtc { get; private set; }

    public DateTime? NextRunUtc { get; private set; }

    public CollectorSyncResult? GetLastResult(string collectorName) =>
        _lastResults.TryGetValue(collectorName, out var result) ? result : null;

    /// <summary>
    /// Syncs one collector (or all when <paramref name="collectorName"/> is null) now. Waits for a run in
    /// progress to finish first, so two runs never overlap.
    /// </summary>
    public async Task<IReadOnlyList<CollectorSyncResult>> RunNowAsync(string? collectorName = null, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            return await RunCoreAsync(collectorName, cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Timer entry point: skips when a run is already in progress. Returns null when skipped.</summary>
    internal async Task<IReadOnlyList<CollectorSyncResult>?> TryRunScheduledAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            logger.LogDebug("Background sync skipped: a run is already in progress");
            return null;
        }

        try
        {
            return await RunCoreAsync(null, cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Weeks to sync at <paramref name="localNow"/>: the current week, plus the previous one on Monday before noon.</summary>
    public static IReadOnlyList<SyncWeek> WeeksToSync(DateTimeOffset localNow, TimeZoneInfo timeZone)
    {
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weeks = new List<SyncWeek>();
        if (localNow.DayOfWeek == DayOfWeek.Monday && localNow.Hour < 12)
            weeks.Add(Week(monday.AddDays(-7), timeZone));
        weeks.Add(Week(monday, timeZone));
        return weeks;
    }

    public static SyncWeek Week(DateOnly monday, TimeZoneInfo timeZone) =>
        new(monday, LocalMidnightUtc(monday, timeZone), LocalMidnightUtc(monday.AddDays(7), timeZone));

    private static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Midnight never falls in a DST gap in the zones this app targets; guard anyway.
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Background sync disabled (Sync:Enabled = false)");
            return;
        }

        var interval = settings.Interval > TimeSpan.Zero ? settings.Interval : TimeSpan.FromMinutes(30);
        try
        {
            NextRunUtc = timeProvider.GetUtcNow().UtcDateTime + settings.InitialDelay;
            await Task.Delay(settings.InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(interval, timeProvider);
            do
            {
                NextRunUtc = timeProvider.GetUtcNow().UtcDateTime + interval;
                await TryRunScheduledAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task<IReadOnlyList<CollectorSyncResult>> RunCoreAsync(string? collectorName, CancellationToken cancellationToken)
    {
        var selected = _collectors
            .Where(c => collectorName is null || c.Name.Equals(collectorName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (collectorName is not null && selected.Count == 0)
            throw new ArgumentException($"Source inconnue : {collectorName}", nameof(collectorName));

        var weeks = WeeksToSync(timeProvider.GetLocalNow(), timeProvider.LocalTimeZone);
        var results = new List<CollectorSyncResult>();
        IsRunning = true;
        Notify();
        try
        {
            foreach (var collector in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CurrentCollector = collector.Name;
                Notify();
                var result = await SyncOneAsync(collector, weeks, cancellationToken);
                _lastResults[collector.Name] = result;
                results.Add(result);
                Notify();
            }
        }
        finally
        {
            CurrentCollector = null;
            IsRunning = false;
            LastRunUtc = timeProvider.GetUtcNow().UtcDateTime;
            Notify();
        }

        return results;
    }

    private async Task<CollectorSyncResult> SyncOneAsync(ICollector collector, IReadOnlyList<SyncWeek> weeks, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow().UtcDateTime;
        var weekStarts = weeks.Select(w => w.WeekStart).ToList();
        try
        {
            var status = await collector.GetStatusAsync(cancellationToken);
            if (!status.IsConfigured)
            {
                return new CollectorSyncResult(collector.Name, CollectorSyncOutcome.NotConfigured, started,
                    timeProvider.GetUtcNow().UtcDateTime, weekStarts, 0, [], status.Message ?? "Non configuré");
            }

            var count = 0;
            var warnings = new List<string>();
            foreach (var week in weeks)
            {
                var result = await collector.SyncAsync(week.FromUtc, week.ToUtc, cancellationToken);
                count += result.EventCount;
                warnings.AddRange(result.Warnings);
            }

            return new CollectorSyncResult(collector.Name, CollectorSyncOutcome.Succeeded, started,
                timeProvider.GetUtcNow().UtcDateTime, weekStarts, count, warnings.Distinct().ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sync of {Collector} failed", collector.Name);
            return new CollectorSyncResult(collector.Name, CollectorSyncOutcome.Failed, started,
                timeProvider.GetUtcNow().UtcDateTime, weekStarts, 0, [], ex.Message);
        }
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sync change handler failed");
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _runGate.Dispose();
    }
}
