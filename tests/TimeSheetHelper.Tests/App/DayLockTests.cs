using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.App;

/// <summary>One send at a time per day, in this process and across processes sharing the database; an interrupted send leaves a resumable day.</summary>
public sealed class DayLockTests : IAsyncLifetime
{
    private static readonly DateOnly Week = new(2026, 10, 12);
    private static readonly DateOnly Day = Week.AddDays(2);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = Db();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private TimeSheetDbContext Db() => new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private DayLock NewLock() => new(new Factory(Db), _time);

    private async Task<DaySubmission> DayAsync()
    {
        await using var db = Db();
        return await db.DaySubmissions.AsNoTracking().SingleAsync(d => d.WeekStart == Week && d.Date == Day);
    }

    private async Task SeedAsync(DayStatus status, DateTime? since)
    {
        await using var db = Db();
        db.DaySubmissions.Add(new DaySubmission { WeekStart = Week, Date = Day, Status = status, InProgressSinceUtc = since });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_day_being_sent_cannot_be_taken_twice()
    {
        var dayLock = NewLock();

        var lease = Assert.IsType<DayLock.DayLease>(await dayLock.TryAcquireAsync(Week, Day));

        Assert.Equal(DayStatus.NotAttempted, lease.PreviousStatus);
        var day = await DayAsync();
        Assert.Equal((DayStatus.InProgress, (DateTime?)_time.Now.UtcDateTime), (day.Status, day.InProgressSinceUtc));
        Assert.Null(await dayLock.TryAcquireAsync(Week, Day));
        Assert.Null(await NewLock().TryAcquireAsync(Week, Day));
        Assert.NotNull(await dayLock.TryAcquireAsync(Week, Day.AddDays(1)));
    }

    [Fact]
    public async Task Releasing_a_day_still_in_progress_leaves_it_partial_and_free()
    {
        var dayLock = NewLock();
        var lease = await dayLock.TryAcquireAsync(Week, Day);

        await lease!.DisposeAsync();

        var day = await DayAsync();
        Assert.Equal((DayStatus.Partial, (DateTime?)null), (day.Status, day.InProgressSinceUtc));
        var again = Assert.IsType<DayLock.DayLease>(await dayLock.TryAcquireAsync(Week, Day));
        Assert.Equal(DayStatus.Partial, again.PreviousStatus);
    }

    [Fact]
    public async Task Releasing_keeps_the_status_the_send_settled()
    {
        var lease = Assert.IsType<DayLock.DayLease>(await NewLock().TryAcquireAsync(Week, Day));
        await using (var db = Db())
        {
            await db.DaySubmissions.Where(d => d.Id == lease.DaySubmissionId).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DayStatus.Sent)
                .SetProperty(d => d.InProgressSinceUtc, (DateTime?)null));
        }

        await lease.DisposeAsync();

        Assert.Equal(DayStatus.Sent, (await DayAsync()).Status);
    }

    [Fact]
    public async Task A_recent_send_of_another_process_is_respected()
    {
        await SeedAsync(DayStatus.InProgress, _time.Now.UtcDateTime.AddMinutes(-2));

        Assert.Null(await NewLock().TryAcquireAsync(Week, Day));
        Assert.Equal(DayStatus.InProgress, (await DayAsync()).Status);
    }

    [Fact]
    public async Task An_abandoned_send_can_be_resumed_as_partial()
    {
        await SeedAsync(DayStatus.InProgress, _time.Now.UtcDateTime - DayLock.StaleAfter - TimeSpan.FromMinutes(1));

        var lease = Assert.IsType<DayLock.DayLease>(await NewLock().TryAcquireAsync(Week, Day));

        Assert.Equal(DayStatus.Partial, lease.PreviousStatus);
        Assert.Equal(_time.Now.UtcDateTime, (await DayAsync()).InProgressSinceUtc);
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
