using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

public sealed class SubmissionTaskChoicesTests : IDisposable
{
    private static readonly DateOnly Week1 = new(2026, 10, 5);
    private static readonly DateOnly Week2 = new(2026, 10, 12);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SubmissionTaskChoicesTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private SubmissionTaskChoices Choices() => new(new Factory(CreateDb));

    private async Task<int> AddTaskAsync(string externalId)
    {
        await using var db = CreateDb();
        var task = new ForecastTask { ExternalId = externalId, ProjectName = "Acme", TaskName = externalId };
        db.ForecastTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    [Fact]
    public async Task Choices_are_kept_per_week_and_task_and_reloaded()
    {
        var a = await AddTaskAsync("T1");
        var b = await AddTaskAsync("T2");
        var choices = Choices();

        await choices.SetAsync(Week1, a, true);
        await choices.SetAsync(Week1, b, false);
        await choices.SetAsync(Week2, a, false);
        await choices.SetAsync(Week1, a, false);

        Assert.Equal(new Dictionary<int, bool> { [a] = false, [b] = false }, await Choices().LoadAsync(Week1));
        Assert.Equal(new Dictionary<int, bool> { [a] = false }, await Choices().LoadAsync(Week2));
    }

    [Fact]
    public async Task A_null_choice_forgets_it_so_the_task_follows_its_flag_again()
    {
        var a = await AddTaskAsync("T1");
        await Choices().SetAsync(Week1, a, true);

        await Choices().SetAsync(Week1, a, null);
        await Choices().SetAsync(Week1, a, null);

        Assert.Empty(await Choices().LoadAsync(Week1));
    }

    [Fact]
    public async Task Resetting_a_week_forgets_only_that_weeks_choices()
    {
        var a = await AddTaskAsync("T1");
        var b = await AddTaskAsync("T2");
        await Choices().SetAsync(Week1, a, true);
        await Choices().SetAsync(Week1, b, false);
        await Choices().SetAsync(Week2, a, true);

        await Choices().ResetWeekAsync(Week1);
        await Choices().ResetWeekAsync(Week1);

        Assert.Empty(await Choices().LoadAsync(Week1));
        Assert.Equal(new Dictionary<int, bool> { [a] = true }, await Choices().LoadAsync(Week2));
    }

    [Fact]
    public async Task A_choice_on_a_task_that_vanished_is_not_stored()
    {
        await Choices().SetAsync(Week1, 4242, true);

        Assert.Empty(await Choices().LoadAsync(Week1));
    }

    [Fact]
    public async Task Deleting_a_task_removes_its_choices_without_blocking_the_deletion()
    {
        var a = await AddTaskAsync("T1");
        var b = await AddTaskAsync("T2");
        await Choices().SetAsync(Week1, a, true);
        await Choices().SetAsync(Week1, b, true);

        var report = await new ForecastTaskCatalog(new Factory(CreateDb)).DeleteAsync([a]);

        Assert.Equal(1, report.Deleted);
        Assert.Equal([b], (await Choices().LoadAsync(Week1)).Keys);
    }

    [Fact]
    public async Task Merging_a_task_into_another_moves_its_choices_and_the_survivors_own_choice_wins()
    {
        var local = await AddTaskAsync("T-local");
        var target = await AddTaskAsync("T1");
        await Choices().SetAsync(Week1, local, true);
        await Choices().SetAsync(Week1, target, false);
        await Choices().SetAsync(Week2, local, true);
        var linker = new ForecastTaskLinker(null!, new Factory(CreateDb), TimeProvider.System, NullLogger<ForecastTaskLinker>.Instance);

        var result = await linker.RelinkAsync(local, new ForecastRemoteTask("1", "T1", "T1", "Acme", null, null));

        Assert.True(result.Merged);
        Assert.Equal(new Dictionary<int, bool> { [target] = false }, await Choices().LoadAsync(Week1));
        Assert.Equal(new Dictionary<int, bool> { [target] = true }, await Choices().LoadAsync(Week2));
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }
}
