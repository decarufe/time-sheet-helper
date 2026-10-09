using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.App.Sync;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Tests.App;

public sealed class SyncSchedulerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public async Task Concurrent_runs_never_overlap()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new FakeCollector("a") { Gate = gate.Task };
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), collector);

        var first = scheduler.RunNowAsync();
        var second = scheduler.RunNowAsync("a");
        await collector.Started.Task;
        Assert.True(scheduler.IsRunning);
        Assert.Equal("a", scheduler.CurrentCollector);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, collector.SyncCalls);
        Assert.Equal(1, collector.MaxConcurrency);
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task Scheduled_tick_is_skipped_while_a_run_is_in_progress()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new FakeCollector("a") { Gate = gate.Task };
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), collector);

        var manual = scheduler.RunNowAsync();
        await collector.Started.Task;

        var skipped = await scheduler.TryRunScheduledAsync(CancellationToken.None);
        Assert.Null(skipped);

        gate.SetResult();
        await manual;
        Assert.Equal(1, collector.SyncCalls);

        var ran = await scheduler.TryRunScheduledAsync(CancellationToken.None);
        Assert.NotNull(ran);
        Assert.Equal(2, collector.SyncCalls);
    }

    [Fact]
    public async Task A_failing_collector_does_not_stop_the_others()
    {
        var failing = new FakeCollector("broken") { Error = new HttpRequestException("boom") };
        var ok = new FakeCollector("ok") { Count = 3, Warnings = ["attention"] };
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), failing, ok);

        var results = await scheduler.RunNowAsync();

        Assert.Equal(2, results.Count);
        var broken = scheduler.GetLastResult("broken");
        Assert.NotNull(broken);
        Assert.Equal(CollectorSyncOutcome.Failed, broken.Outcome);
        Assert.Equal("boom", broken.Message);

        var good = scheduler.GetLastResult("OK");
        Assert.NotNull(good);
        Assert.Equal(CollectorSyncOutcome.Succeeded, good.Outcome);
        Assert.Equal(3, good.EventCount);
        Assert.Equal(["attention"], good.Warnings);
    }

    [Fact]
    public async Task A_status_check_that_throws_is_isolated_too()
    {
        var failing = new FakeCollector("broken") { StatusError = new InvalidOperationException("statut") };
        var ok = new FakeCollector("ok");
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), failing, ok);

        await scheduler.RunNowAsync();

        Assert.Equal(CollectorSyncOutcome.Failed, scheduler.GetLastResult("broken")!.Outcome);
        Assert.Equal(CollectorSyncOutcome.Succeeded, scheduler.GetLastResult("ok")!.Outcome);
    }

    [Fact]
    public async Task Unconfigured_collectors_are_skipped_with_their_message()
    {
        var missing = new FakeCollector("github") { Status = new CollectorStatus(false, "Jeton manquant") };
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), missing);

        await scheduler.RunNowAsync();

        Assert.Equal(0, missing.SyncCalls);
        var result = scheduler.GetLastResult("github")!;
        Assert.Equal(CollectorSyncOutcome.NotConfigured, result.Outcome);
        Assert.Equal("Jeton manquant", result.Message);
    }

    [Fact]
    public async Task Run_now_can_target_one_collector()
    {
        var a = new FakeCollector("a");
        var b = new FakeCollector("b");
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), a, b);

        await scheduler.RunNowAsync("b");

        Assert.Equal(0, a.SyncCalls);
        Assert.Equal(1, b.SyncCalls);
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RunNowAsync("inconnu"));
    }

    [Fact]
    public async Task Monday_morning_also_syncs_the_previous_week()
    {
        var collector = new FakeCollector("a");
        var scheduler = CreateScheduler(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero), collector);

        await scheduler.RunNowAsync();

        Assert.Equal(
            [
                (new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)),
                (new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)),
            ],
            collector.Ranges);
        Assert.Equal([new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5)], scheduler.GetLastResult("a")!.Weeks);
    }

    [Theory]
    [InlineData("2026-10-05T11:59:00", 2)] // Monday morning
    [InlineData("2026-10-05T12:00:00", 1)] // Monday afternoon
    [InlineData("2026-10-07T08:00:00", 1)] // Wednesday
    [InlineData("2026-10-11T23:00:00", 1)] // Sunday night
    public void Weeks_to_sync(string localNow, int expected)
    {
        var now = new DateTimeOffset(DateTime.Parse(localNow, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);

        var weeks = SyncScheduler.WeeksToSync(now, Utc);

        Assert.Equal(expected, weeks.Count);
        Assert.Equal(new DateOnly(2026, 10, 5), weeks[^1].WeekStart);
        Assert.All(weeks, w => Assert.Equal(TimeSpan.FromDays(7), w.ToUtc - w.FromUtc));
    }

    [Fact]
    public void Week_bounds_follow_the_local_time_zone()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        var week = SyncScheduler.Week(new DateOnly(2026, 10, 5), eastern);

        Assert.Equal(new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc), week.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc), week.ToUtc);
    }

    private static SyncScheduler CreateScheduler(DateTimeOffset utcNow, params ICollector[] collectors) =>
        new(collectors, Options.Create(new SyncOptions { Enabled = false }), new FixedTimeProvider(utcNow), NullLogger<SyncScheduler>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeCollector(string name) : ICollector
    {
        private int _running;

        public string Name => name;
        public CollectorStatus Status { get; init; } = new(true);
        public Exception? StatusError { get; init; }
        public Exception? Error { get; init; }
        public Task? Gate { get; init; }
        public int Count { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SyncCalls;
        public int MaxConcurrency;
        public List<(DateTime, DateTime)> Ranges { get; } = [];

        public Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            StatusError is not null ? Task.FromException<CollectorStatus>(StatusError) : Task.FromResult(Status);

        public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref MaxConcurrency, running);
            Interlocked.Increment(ref SyncCalls);
            lock (Ranges)
                Ranges.Add((fromUtc, toUtc));
            try
            {
                Started.TrySetResult();
                if (Gate is not null)
                    await Gate;
                else
                    await Task.Yield();
                if (Error is not null)
                    throw Error;
                return new CollectorResult(Count, Warnings);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
