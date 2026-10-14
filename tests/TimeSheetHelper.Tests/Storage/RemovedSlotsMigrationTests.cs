using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Storage;

/// <summary>The <c>RemovedSlots</c> migration must apply on a database that already holds data, and match the model.</summary>
public sealed class RemovedSlotsMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261013233626_InstalledPlugins";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public async Task InitializeAsync() => await _connection.OpenAsync();

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private TimeSheetDbContext Db() => new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task Migration_applies_on_an_existing_database_and_keeps_its_data()
    {
        await using (var old = Db())
        {
            old.GetService<IMigrator>().Migrate(PreviousMigration);
            Assert.DoesNotContain("20261014131813_RemovedSlots", await old.Database.GetAppliedMigrationsAsync());
            await old.Database.ExecuteSqlRawAsync("INSERT INTO SlotOverrides (SlotStartUtc, ForecastTaskId, Notes) VALUES ('2026-10-05 13:00:00', NULL, 'avant')");
        }

        await using var db = Db();
        await db.Database.MigrateAsync();

        Assert.Contains("20261014131813_RemovedSlots", await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var kept = await db.SlotOverrides.SingleAsync();
        Assert.Equal((new DateTime(2026, 10, 5, 13, 0, 0), null, "avant"), (kept.SlotStartUtc, kept.ForecastTaskId, kept.Notes));
        Assert.Equal(0, await db.RemovedSlots.CountAsync());
    }

    [Fact]
    public async Task The_removed_slots_index_rejects_a_second_tombstone_for_the_same_slot()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var slot = new DateTime(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);
        db.RemovedSlots.Add(new RemovedSlot { SlotStartUtc = slot });
        await db.SaveChangesAsync();

        await using var other = Db();
        other.RemovedSlots.Add(new RemovedSlot { SlotStartUtc = slot });

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task The_model_snapshot_matches_the_current_model()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
