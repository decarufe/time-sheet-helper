using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

public sealed class ForecastTaskSyncTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ForecastTaskSyncTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private static ParsedForecastTask Parsed(string id, string project, string task, string? code = null) =>
        new(id, project, code, null, task);

    [Fact]
    public async Task Upsert_adds_updates_and_deactivates()
    {
        await using (var db = CreateDb())
        {
            db.ForecastTasks.AddRange(
                new ForecastTask { ExternalId = "T1", ProjectName = "Acme", TaskName = "Old name", LastSeenUtc = Now.AddDays(-1) },
                new ForecastTask { ExternalId = "T2", ProjectName = "Acme", TaskName = "Gone", LastSeenUtc = Now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        ForecastTaskSyncResult result;
        await using (var db = CreateDb())
            result = await ForecastTaskSync.UpsertAsync(db, [Parsed("T1", "Acme", "New name"), Parsed("T3", "Globex", "Dev"), Parsed("T3", "Globex", "Dev")], Now);

        Assert.Equal((1, 1, 1, 2), (result.Added, result.Updated, result.Deactivated, result.Seen));

        await using var check = CreateDb();
        var tasks = await check.ForecastTasks.OrderBy(t => t.ExternalId).ToListAsync();
        Assert.Equal(["T1", "T2", "T3"], tasks.Select(t => t.ExternalId));
        Assert.Equal("New name", tasks[0].TaskName);
        Assert.True(tasks[0].IsActive);
        Assert.Equal(Now, tasks[0].LastSeenUtc);
        Assert.False(tasks[1].IsActive);
        Assert.True(tasks[2].IsActive);
    }

    [Fact]
    public async Task Upsert_reactivates_and_rekeys_fallback_ids()
    {
        int id;
        await using (var db = CreateDb())
        {
            var task = new ForecastTask { ExternalId = "Acme/Dev", ProjectName = "Acme", TaskName = "Dev", IsActive = false };
            db.ForecastTasks.Add(task);
            await db.SaveChangesAsync();
            id = task.Id;
        }

        await using (var db = CreateDb())
        {
            var result = await ForecastTaskSync.UpsertAsync(db, [Parsed("T42", "Acme", "Dev", "P-1")], Now);
            Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Deactivated));
        }

        await using var check = CreateDb();
        var single = await check.ForecastTasks.SingleAsync();
        Assert.Equal(id, single.Id);
        Assert.Equal("T42", single.ExternalId);
        Assert.Equal("P-1", single.ProjectCode);
        Assert.True(single.IsActive);
    }

    [Fact]
    public async Task Upsert_with_nothing_read_keeps_cache()
    {
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(new ForecastTask { ExternalId = "T1", ProjectName = "Acme", TaskName = "Dev" });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDb())
        {
            var result = await ForecastTaskSync.UpsertAsync(db, [], Now);
            Assert.NotNull(result.Warning);
        }

        await using var check = CreateDb();
        Assert.True((await check.ForecastTasks.SingleAsync()).IsActive);
    }

    [Fact]
    public void Parse_uses_task_and_project_codes()
    {
        var row = new RawForecastTaskRow(["T123 Intégration API", "P-7 Acme Portail", "2h 30m", "En cours"], ["/project/P-7/task/T123"]);

        var parsed = ForecastTaskSync.Parse(row);

        Assert.NotNull(parsed);
        Assert.Equal("T123", parsed.ExternalId);
        Assert.Equal("Intégration API", parsed.TaskName);
        Assert.Equal("Acme Portail", parsed.ProjectName);
        Assert.Equal("P-7", parsed.ProjectCode);
    }

    [Fact]
    public void Parse_without_codes_falls_back_to_project_slash_task()
    {
        var parsed = ForecastTaskSync.Parse(new RawForecastTaskRow(["Réunions", "Interne", "1h", "0h"], []));

        Assert.NotNull(parsed);
        Assert.Equal("Interne/Réunions", parsed.ExternalId);
        Assert.Null(ForecastTaskSync.Parse(new RawForecastTaskRow(["1h", "2,5"], [])));
    }
}
