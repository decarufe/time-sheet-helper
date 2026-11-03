using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.App;

public sealed class TaskNumberSearchTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc);
    private static readonly ForecastTaskMatch Build = new("T35647", "Build du tableau de bord", null, 1078, "P1078", "Intranet", "Acme");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TaskNumberSearchTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private ForecastTaskImporter Importer() => new(new Factory(CreateDb), new FixedClock(Now));

    [Theory]
    [InlineData("T35647", "T35647")]
    [InlineData("35647", "T35647")]
    [InlineData(" t35647 ", "T35647")]
    [InlineData("t1", "T1")]
    public void A_task_number_is_normalized(string query, string expected)
    {
        Assert.True(TaskPickerView.TryParseTaskNumber(query, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("T")]
    [InlineData("build")]
    [InlineData("T12x")]
    [InlineData("P1078")]
    [InlineData("12345678")]
    public void Other_text_is_not_a_task_number(string? query)
    {
        Assert.False(TaskPickerView.TryParseTaskNumber(query, out var code));
        Assert.Equal("", code);
    }

    [Theory]
    [InlineData("35647")]
    [InlineData("T35647")]
    [InlineData("t35647")]
    public void The_local_filter_finds_a_task_by_its_code(string query)
    {
        ForecastTask[] tasks =
        [
            new() { Id = 1, ExternalId = "T35647", ProjectName = "Intranet", TaskName = "Build" },
            new() { Id = 2, ExternalId = "T35648", ProjectName = "Intranet", TaskName = "Deploy" },
        ];

        var groups = TaskPickerView.Build(tasks, query, new TaskPickerOptions(), new Dictionary<int, TaskUsage>(), new DateOnly(2026, 10, 20));

        Assert.Equal([1], groups.SelectMany(g => g.Tasks).Select(t => t.Id));
    }

    [Fact]
    public async Task Adding_a_remote_task_creates_it_with_its_codes_and_client()
    {
        var result = await Importer().AddFromRemoteAsync(Build);

        Assert.Null(result.Error);
        await using var check = CreateDb();
        var task = await check.ForecastTasks.SingleAsync(t => t.Id == result.TaskId);
        Assert.Equal("T35647", task.ExternalId);
        Assert.Equal("Build du tableau de bord", task.TaskName);
        Assert.Equal("Intranet", task.ProjectName);
        Assert.Equal("P1078", task.ProjectCode);
        Assert.Equal("Acme", task.ClientName);
        Assert.True(task.IsActive);
    }

    [Fact]
    public async Task Adding_a_remote_task_already_known_returns_the_existing_one()
    {
        int id;
        await using (var db = CreateDb())
        {
            var existing = new ForecastTask { ExternalId = "T35647", ProjectName = "Intranet", TaskName = "Build", IsFavorite = true };
            db.ForecastTasks.Add(existing);
            await db.SaveChangesAsync();
            id = existing.Id;
        }

        var result = await Importer().AddFromRemoteAsync(Build);

        Assert.Equal(id, result.TaskId);
        await using var check = CreateDb();
        var single = await check.ForecastTasks.SingleAsync();
        Assert.True(single.IsFavorite);
    }

    [Fact]
    public async Task Adding_a_remote_task_reactivates_an_inactive_local_one()
    {
        int id;
        await using (var db = CreateDb())
        {
            var inactive = new ForecastTask { ExternalId = "T35647", ProjectName = "Intranet", TaskName = "Build", IsActive = false };
            db.ForecastTasks.Add(inactive);
            await db.SaveChangesAsync();
            id = inactive.Id;
        }

        var result = await Importer().AddFromRemoteAsync(Build);

        Assert.Equal(id, result.TaskId);
        await using var check = CreateDb();
        Assert.True((await check.ForecastTasks.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Adding_a_remote_task_without_a_project_name_fails()
    {
        var result = await Importer().AddFromRemoteAsync(new ForecastTaskMatch("T1", "Task", null, null, null, null, null));

        Assert.Null(result.TaskId);
        Assert.NotNull(result.Error);
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
