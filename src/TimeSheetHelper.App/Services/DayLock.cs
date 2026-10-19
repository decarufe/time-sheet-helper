using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.App.Services;

/// <summary>
/// One send at a time per day: a <see cref="SemaphoreSlim"/> per (week, day) in this process, and <see cref="DaySubmission.InProgressSinceUtc"/>
/// in the database for the other tabs and processes. Never waits: a day already being sent gives no lease.
/// </summary>
public sealed class DayLock(IDbContextFactory<TimeSheetDbContext> dbFactory, TimeProvider timeProvider)
{
    /// <summary>A day in progress for longer than this was interrupted (crash): it may be taken again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(DateOnly WeekStart, DateOnly Date), SemaphoreSlim> gates = new();

    /// <summary>
    /// A <see cref="DayLease"/> once the day is marked <see cref="DayStatus.InProgress"/>; null when this process already holds it or another
    /// one marked it less than <see cref="StaleAfter"/> ago. Disposing the lease puts a day still in progress back to <see cref="DayStatus.Partial"/>.
    /// </summary>
    public Task<IAsyncDisposable?> TryAcquireAsync(DateOnly weekStart, DateOnly date, CancellationToken cancellationToken = default) =>
        AcquireAsync(weekStart, date, restorePrevious: false, cancellationToken);

    /// <summary>
    /// Holds every given day for the weekly fill, so that no send per day (this tab, another one or another process) runs at the same time;
    /// null when one of them is already held (nothing stays held). Releasing puts each day back to the status it had: the weekly fill does not settle days.
    /// </summary>
    public async Task<IAsyncDisposable?> TryHoldDaysAsync(DateOnly weekStart, IEnumerable<DateOnly> dates, CancellationToken cancellationToken = default)
    {
        var leases = new List<IAsyncDisposable>();
        try
        {
            foreach (var date in dates.Distinct().Order())
            {
                var lease = await AcquireAsync(weekStart, date, restorePrevious: true, cancellationToken);
                if (lease is null)
                {
                    await ReleaseAllAsync(leases);
                    return null;
                }

                leases.Add(lease);
            }
        }
        catch
        {
            await ReleaseAllAsync(leases);
            throw;
        }

        return new DaysHold(leases);
    }

    private static async Task ReleaseAllAsync(IEnumerable<IAsyncDisposable> leases)
    {
        foreach (var lease in leases)
            await lease.DisposeAsync();
    }

    private async Task<IAsyncDisposable?> AcquireAsync(DateOnly weekStart, DateOnly date, bool restorePrevious, CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd((weekStart, date), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
            return null;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var day = await LoadOrCreateAsync(db, weekStart, date, cancellationToken);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var stale = day.InProgressSinceUtc is { } since && since <= now - StaleAfter;
            if (day.Status == DayStatus.InProgress && !stale)
            {
                gate.Release();
                return null;
            }

            // Conditional update: of two processes reading the same row, only one turns it to InProgress.
            var observedStatus = day.Status;
            var observedSince = day.InProgressSinceUtc;
            var taken = await db.DaySubmissions
                .Where(d => d.Id == day.Id && d.Status == observedStatus && d.InProgressSinceUtc == observedSince)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DayStatus.InProgress)
                    .SetProperty(d => d.InProgressSinceUtc, (DateTime?)now), cancellationToken);
            if (taken == 0)
            {
                gate.Release();
                return null;
            }

            var previous = observedStatus == DayStatus.InProgress ? DayStatus.Partial : observedStatus;
            return new DayLease(this, gate, day.Id, previous, now, restorePrevious);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private static async Task<DaySubmission> LoadOrCreateAsync(TimeSheetDbContext db, DateOnly weekStart, DateOnly date, CancellationToken cancellationToken)
    {
        var day = await db.DaySubmissions.AsNoTracking().FirstOrDefaultAsync(d => d.WeekStart == weekStart && d.Date == date, cancellationToken);
        if (day is not null)
            return day;

        db.DaySubmissions.Add(new DaySubmission { WeekStart = weekStart, Date = date, Status = DayStatus.NotAttempted });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another process created the row first (unique on week and day).
            db.ChangeTracker.Clear();
        }

        return await db.DaySubmissions.AsNoTracking().FirstAsync(d => d.WeekStart == weekStart && d.Date == date, cancellationToken);
    }

    private async Task ReleaseAsync(SemaphoreSlim gate, int daySubmissionId, DateTime since, DayStatus releasedStatus)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.DaySubmissions
                .Where(d => d.Id == daySubmissionId && d.Status == DayStatus.InProgress && d.InProgressSinceUtc == since)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, releasedStatus)
                    .SetProperty(d => d.InProgressSinceUtc, (DateTime?)null));
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class DaysHold(IReadOnlyList<IAsyncDisposable> leases) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                await ReleaseAllAsync(leases);
        }
    }

    /// <summary>The right to send one day; <see cref="PreviousStatus"/> is the status the day had before (an expired InProgress counts as Partial).</summary>
    public sealed class DayLease : IAsyncDisposable
    {
        private readonly DayLock owner;
        private readonly SemaphoreSlim gate;
        private readonly bool restorePrevious;
        private int disposed;

        internal DayLease(DayLock owner, SemaphoreSlim gate, int daySubmissionId, DayStatus previousStatus, DateTime sinceUtc, bool restorePrevious)
        {
            this.owner = owner;
            this.gate = gate;
            this.restorePrevious = restorePrevious;
            DaySubmissionId = daySubmissionId;
            PreviousStatus = previousStatus;
            SinceUtc = sinceUtc;
        }

        public int DaySubmissionId { get; }

        public DayStatus PreviousStatus { get; }

        public DateTime SinceUtc { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                await owner.ReleaseAsync(gate, DaySubmissionId, SinceUtc, restorePrevious ? PreviousStatus : DayStatus.Partial);
        }
    }
}
