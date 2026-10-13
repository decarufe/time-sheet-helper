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
    public async Task Upsert_without_deactivation_keeps_tasks_missing_from_the_input()
    {
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(new ForecastTask { ExternalId = "T2", ProjectName = "Acme", TaskName = "Other week" });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDb())
        {
            var result = await ForecastTaskSync.UpsertAsync(db, [Parsed("T1", "Acme", "Dev")], Now, deactivateMissing: false);
            Assert.Equal((1, 0, 0), (result.Added, result.Updated, result.Deactivated));
        }

        await using var check = CreateDb();
        Assert.All(await check.ForecastTasks.ToListAsync(), t => Assert.True(t.IsActive));
    }

    [Fact]
    public async Task Upsert_adopts_a_project_less_internal_row_instead_of_duplicating_it()
    {
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(new ForecastTask { ExternalId = "(sans projet)/Sport", ProjectName = "(sans projet)", TaskName = "Sport" });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDb())
            await ForecastTaskSync.UpsertAsync(db, [Parsed("Internal Time/Sport", "Internal Time", "Sport")], Now, deactivateMissing: false);

        await using var check = CreateDb();
        var single = await check.ForecastTasks.SingleAsync();
        Assert.Equal("Internal Time/Sport", single.ExternalId);
        Assert.Equal("Internal Time", single.ProjectName);
    }

    [Fact]
    public async Task Importer_applies_a_pasted_table_and_can_deactivate_the_rest()
    {
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(new ForecastTask { ExternalId = "T1", ProjectName = "Old", TaskName = "Old task" });
            await db.SaveChangesAsync();
        }

        var importer = new ForecastTaskImporter(new SharedConnectionFactory(CreateDb), new FakeTimeProvider(Now));
        var result = await importer.ImportAsync("Projet\tTask\nP1-Acme\tT2-Dev\n-Internal Time\t-Sport\n", deactivateMissing: true);

        Assert.Equal((2, 0, 1), (result.Sync.Added, result.Sync.Updated, result.Sync.Deactivated));
        Assert.Contains("T2-Dev", await importer.ExportAsync());
        Assert.DoesNotContain("Old task", await importer.ExportAsync());
    }

    [Fact]
    public async Task Add_creates_a_task_reusing_the_code_of_a_known_project()
    {
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(new ForecastTask { ExternalId = "T1", ProjectName = "Acme", ProjectCode = "P7", ClientName = "Acme Inc", TaskName = "Dev" });
            await db.SaveChangesAsync();
        }

        var importer = new ForecastTaskImporter(new SharedConnectionFactory(CreateDb), new FakeTimeProvider(Now));
        var result = await importer.AddAsync("  Acme ", "Revue  de code", taskCode: "t99");

        Assert.Null(result.Error);
        await using var check = CreateDb();
        var created = await check.ForecastTasks.SingleAsync(t => t.Id == result.TaskId);
        Assert.Equal("T99", created.ExternalId);
        Assert.Equal("Revue de code", created.TaskName);
        Assert.Equal("P7", created.ProjectCode);
        Assert.Equal("Acme Inc", created.ClientName);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task Add_reactivates_an_existing_task_instead_of_duplicating_it()
    {
        int id;
        await using (var db = CreateDb())
        {
            var task = new ForecastTask { ExternalId = "Internal Time/Sport", ProjectName = "Internal Time", TaskName = "Sport", IsActive = false };
            db.ForecastTasks.Add(task);
            await db.SaveChangesAsync();
            id = task.Id;
        }

        var importer = new ForecastTaskImporter(new SharedConnectionFactory(CreateDb), new FakeTimeProvider(Now));
        var result = await importer.AddAsync("Internal Time", "Sport");

        Assert.Equal(id, result.TaskId);
        await using var check = CreateDb();
        Assert.True((await check.ForecastTasks.SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData("", "Dev", null, null)]
    [InlineData("Acme", "  ", null, null)]
    [InlineData("Acme", "Dev", "X12", null)]
    [InlineData("Acme", "Dev", null, "T12x")]
    public async Task Add_rejects_missing_names_and_malformed_codes(string project, string task, string? projectCode, string? taskCode)
    {
        var importer = new ForecastTaskImporter(new SharedConnectionFactory(CreateDb), new FakeTimeProvider(Now));

        var result = await importer.AddAsync(project, task, projectCode, taskCode);

        Assert.Null(result.TaskId);
        Assert.NotNull(result.Error);
        await using var check = CreateDb();
        Assert.Empty(check.ForecastTasks);
    }

    private sealed class SharedConnectionFactory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
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
