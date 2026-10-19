using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Storage;

/// <summary>The <c>DaySubmissions</c> migration is additive, back-fills submitted weeks (G11), and can be rolled back.</summary>
public sealed class DaySubmissionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261019162114_WeekTaskChoices";
    private const string ThisMigration = "20261019190453_DaySubmissions";

    private static readonly DateOnly SubmittedWeek = new(2026, 10, 5);
    private static readonly DateOnly DraftWeek = new(2026, 10, 12);
    private static readonly DateTime SubmittedUtc = new(2026, 10, 9, 21, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public async Task InitializeAsync() => await _connection.OpenAsync();

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private TimeSheetDbContext Db() => new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    /// <summary>A database at the previous migration with one submitted week (two entries on Monday, one on Tuesday) and one unsubmitted week.</summary>
    private async Task SeedPreviousSchemaAsync()
    {
        await using var old = Db();
        old.GetService<IMigrator>().Migrate(PreviousMigration);
        var task = new ForecastTask { ExternalId = "T-1", ProjectName = "Projet", TaskName = "Tâche" };
        var other = new ForecastTask { ExternalId = "T-2", ProjectName = "Projet", TaskName = "Autre" };
        old.ForecastTasks.AddRange(task, other);
        await old.SaveChangesAsync();
        old.TimesheetDrafts.AddRange(
            new TimesheetDraft
            {
                WeekStart = SubmittedWeek,
                SubmittedUtc = SubmittedUtc,
                Entries =
                [
                    new TimesheetEntry { Date = SubmittedWeek, ForecastTaskId = task.Id, Hours = 4m },
                    new TimesheetEntry { Date = SubmittedWeek, ForecastTaskId = other.Id, Hours = 3.75m },
                    new TimesheetEntry { Date = SubmittedWeek.AddDays(1), ForecastTaskId = task.Id, Hours = 8m },
                ],
            },
            new TimesheetDraft
            {
                WeekStart = DraftWeek,
                Entries = [new TimesheetEntry { Date = DraftWeek, ForecastTaskId = task.Id, Hours = 8m }],
            });
        await old.SaveChangesAsync();
    }

    [Fact]
    public async Task G11_MigratedSubmittedWeek_NeverProposedAsNew()
    {
        await SeedPreviousSchemaAsync();

        await using var db = Db();
        await db.Database.MigrateAsync();

        Assert.Contains(ThisMigration, await db.Database.GetAppliedMigrationsAsync());
        var days = await db.DaySubmissions.Include(d => d.Cells).OrderBy(d => d.Date).ToListAsync();
        Assert.Equal([SubmittedWeek, SubmittedWeek.AddDays(1)], days.Select(d => d.Date));
        Assert.All(days, d =>
        {
            Assert.Equal(SubmittedWeek, d.WeekStart);
            Assert.Equal(DayStatus.SentUnknownFingerprint, d.Status);
            Assert.Null(d.Fingerprint);
            Assert.Equal(SubmittedUtc, d.SentUtc);
            Assert.Equal(DateTimeKind.Utc, d.SentUtc!.Value.Kind);
            Assert.Null(d.InProgressSinceUtc);
            Assert.Empty(d.Cells);
        });

        var journal = await db.SubmissionJournal.OrderBy(j => j.DaySubmissionId).ToListAsync();
        Assert.Equal(days.Select(d => d.Id), journal.Select(j => j.DaySubmissionId));
        Assert.All(journal, j =>
        {
            Assert.Equal((JournalKind.Migrated, JournalOutcome.Unknown), (j.Kind, j.Outcome));
            Assert.NotEqual(default, j.TimestampUtc);
        });
    }

    [Fact]
    public async Task Migration_keeps_existing_drafts_and_rolls_back_to_the_previous_schema()
    {
        await SeedPreviousSchemaAsync();
        await using (var db = Db())
            await db.Database.MigrateAsync();

        await using (var db = Db())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration);
            Assert.DoesNotContain(ThisMigration, await db.Database.GetAppliedMigrationsAsync());
            var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();
            Assert.DoesNotContain("DaySubmissions", tables);
            Assert.DoesNotContain("DaySubmissionCells", tables);
            Assert.DoesNotContain("SubmissionJournal", tables);
        }

        await using var again = Db();
        await again.Database.MigrateAsync();
        var drafts = await again.TimesheetDrafts.Include(d => d.Entries).OrderBy(d => d.WeekStart).ToListAsync();
        Assert.Equal([(SubmittedWeek, (DateTime?)SubmittedUtc, 3), (DraftWeek, null, 1)],
            drafts.Select(d => (d.WeekStart, d.SubmittedUtc, d.Entries.Count)));
        Assert.Equal(2, await again.DaySubmissions.CountAsync());
    }

    [Fact]
    public async Task A_day_can_only_be_tracked_once_per_week()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        db.DaySubmissions.Add(new DaySubmission { WeekStart = DraftWeek, Date = DraftWeek });
        await db.SaveChangesAsync();

        await using var other = Db();
        other.DaySubmissions.Add(new DaySubmission { WeekStart = DraftWeek, Date = DraftWeek, Status = DayStatus.Sent });

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task A_task_can_only_have_one_sent_cell_per_day()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        db.DaySubmissions.Add(new DaySubmission
        {
            WeekStart = DraftWeek,
            Date = DraftWeek,
            Cells = [new DaySubmissionCell { ForecastTaskId = 7, SentHours = 2m }, new DaySubmissionCell { ForecastTaskId = 7, SentHours = 1m }],
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_day_with_its_cells_and_journal_round_trips_and_is_deleted_as_a_whole()
    {
        var sent = new DateTime(2026, 10, 13, 14, 15, 0, DateTimeKind.Utc);
        await using (var db = Db())
        {
            await db.Database.MigrateAsync();
            var day = new DaySubmission
            {
                WeekStart = DraftWeek,
                Date = DraftWeek.AddDays(1),
                Status = DayStatus.Partial,
                Fingerprint = new string('a', 64),
                SentUtc = sent,
                Cells =
                [
                    new DaySubmissionCell { ForecastTaskId = 7, SentHours = 3.25m, ForecastEntryId = "987654" },
                    new DaySubmissionCell { ForecastTaskId = 8, SentHours = 0.75m },
                ],
            };
            db.DaySubmissions.Add(day);
            await db.SaveChangesAsync();
            db.SubmissionJournal.Add(new SubmissionJournalEntry
            {
                DaySubmissionId = day.Id,
                TimestampUtc = sent,
                Kind = JournalKind.Interrupted,
                Outcome = JournalOutcome.Partial,
                ForecastTaskId = 8,
                Hours = 0.75m,
                Message = "Envoi interrompu : 1 cellule sur 2 écrite.",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var read = await db.DaySubmissions.Include(d => d.Cells).SingleAsync();
            Assert.Equal((DraftWeek, DraftWeek.AddDays(1), DayStatus.Partial, new string('a', 64), (DateTime?)sent),
                (read.WeekStart, read.Date, read.Status, read.Fingerprint, read.SentUtc));
            Assert.Equal(DateTimeKind.Utc, read.SentUtc!.Value.Kind);
            Assert.Equal([(7, 3.25m, "987654"), (8, 0.75m, null)],
                read.Cells.OrderBy(c => c.ForecastTaskId).Select(c => (c.ForecastTaskId, c.SentHours, c.ForecastEntryId)));
            var line = await db.SubmissionJournal.SingleAsync();
            Assert.Equal((read.Id, sent, JournalKind.Interrupted, JournalOutcome.Partial, (int?)8, (decimal?)0.75m, "Envoi interrompu : 1 cellule sur 2 écrite."),
                (line.DaySubmissionId, line.TimestampUtc, line.Kind, line.Outcome, line.ForecastTaskId, line.Hours, line.Message));

            db.DaySubmissions.Remove(read);
            await db.SaveChangesAsync();
        }

        await using var after = Db();
        Assert.Equal(0, await after.DaySubmissionCells.CountAsync());
        Assert.Equal(0, await after.SubmissionJournal.CountAsync());
    }

    [Fact]
    public async Task An_empty_database_migrates_and_the_snapshot_matches_the_model()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, await db.DaySubmissions.CountAsync());
        Assert.Equal(0, await db.SubmissionJournal.CountAsync());
    }
}
