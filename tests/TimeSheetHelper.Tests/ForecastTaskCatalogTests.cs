using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

public sealed class ForecastTaskCatalogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ForecastTaskCatalogTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private ForecastTaskCatalog Catalog() => new(new Factory(CreateDb));

    private async Task<int> AddTaskAsync(string externalId, string project, string task, bool active = true, bool favorite = false, string? client = null)
    {
        await using var db = CreateDb();
        var entity = new ForecastTask
        {
            ExternalId = externalId,
            ProjectName = project,
            TaskName = task,
            ClientName = client,
            IsActive = active,
            IsFavorite = favorite,
            LastSeenUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.ForecastTasks.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    [Fact]
    public async Task Query_hides_inactive_and_the_ignored_task_by_default_and_sorts_by_project_then_task()
    {
        await AddTaskAsync("T2", "Beta", "Zeta");
        await AddTaskAsync("T1", "alpha", "Gamma");
        await AddTaskAsync("T3", "Beta", "Alpha");
        await AddTaskAsync("T4", "Alpha", "Off", active: false);
        await AddTaskAsync(ForecastTask.IgnoredExternalId, "Ignoré", "Ignoré");

        var page = await Catalog().QueryAsync(new ForecastTaskQuery(), 0, 50);

        Assert.Equal(3, page.Total);
        Assert.Equal(["T1", "T3", "T2"], page.Items.Select(r => r.Task.ExternalId));
    }

    [Fact]
    public async Task Query_pages_without_overlap_and_reports_the_total()
    {
        for (var i = 0; i < 25; i++)
            await AddTaskAsync($"T{i}", "Acme", $"Task {i:00}");

        var catalog = Catalog();
        var first = await catalog.QueryAsync(new ForecastTaskQuery(), 0, 10);
        var second = await catalog.QueryAsync(new ForecastTaskQuery(), 10, 10);
        var last = await catalog.QueryAsync(new ForecastTaskQuery(), 20, 10);

        Assert.Equal(25, first.Total);
        Assert.Equal([10, 10, 5], new[] { first.Items.Count, second.Items.Count, last.Items.Count });
        Assert.Equal(25, first.Items.Concat(second.Items).Concat(last.Items).Select(r => r.Task.Id).Distinct().Count());
    }

    [Fact]
    public async Task Query_filters_by_text_status_and_favorites()
    {
        await AddTaskAsync("T1", "Acme", "Intranet", favorite: true);
        await AddTaskAsync("T2", "Acme", "Support");
        await AddTaskAsync("T3", "Globex", "Intranet", active: false);
        var catalog = Catalog();

        var text = await catalog.QueryAsync(new ForecastTaskQuery(Search: "intra", Status: ForecastTaskStatusFilter.All), 0, 50);
        var inactive = await catalog.QueryAsync(new ForecastTaskQuery(Status: ForecastTaskStatusFilter.Inactive), 0, 50);
        var favorites = await catalog.QueryAsync(new ForecastTaskQuery(FavoritesOnly: true), 0, 50);

        Assert.Equal(["T1", "T3"], text.Items.Select(r => r.Task.ExternalId));
        Assert.Equal(["T3"], inactive.Items.Select(r => r.Task.ExternalId));
        Assert.Equal(["T1"], favorites.Items.Select(r => r.Task.ExternalId));
    }

    [Fact]
    public async Task Query_sorts_descending_by_task_name()
    {
        await AddTaskAsync("T1", "Acme", "A");
        await AddTaskAsync("T2", "Acme", "B");

        var page = await Catalog().QueryAsync(new ForecastTaskQuery(Sort: ForecastTaskSort.Task, Descending: true), 0, 50);

        Assert.Equal(["T2", "T1"], page.Items.Select(r => r.Task.ExternalId));
    }

    [Fact]
    public async Task Query_groups_by_project_and_marks_the_first_row_of_each_group_even_across_pages()
    {
        await AddTaskAsync("T1", "Acme", "A");
        await AddTaskAsync("T2", "Acme", "B");
        await AddTaskAsync("T3", "Globex", "C");
        await AddTaskAsync("T4", "Globex", "D");
        var query = new ForecastTaskQuery(Grouping: ForecastTaskGrouping.Project);
        var catalog = Catalog();

        var all = await catalog.QueryAsync(query, 0, 50);
        var secondPage = await catalog.QueryAsync(query, 3, 50);

        Assert.Equal(["Acme", null, "Globex", null], all.Items.Select(r => r.GroupHeader));
        Assert.Equal([null], secondPage.Items.Select(r => r.GroupHeader));
    }

    [Fact]
    public async Task Query_groups_by_status_with_active_tasks_first()
    {
        await AddTaskAsync("T1", "Acme", "Off", active: false);
        await AddTaskAsync("T2", "Acme", "On");

        var page = await Catalog().QueryAsync(
            new ForecastTaskQuery(Status: ForecastTaskStatusFilter.All, Grouping: ForecastTaskGrouping.Status), 0, 50);

        Assert.Equal(["T2", "T1"], page.Items.Select(r => r.Task.ExternalId));
        Assert.Equal(["Actives", "Désactivées"], page.Items.Select(r => r.GroupHeader));
    }

    [Fact]
    public async Task Update_renames_the_task_and_moves_its_identity_with_the_task_code()
    {
        var id = await AddTaskAsync("Acme/Dev", "Acme", "Dev");

        var result = await Catalog().UpdateAsync(id, "Acme Corp", "Development", "p1078", "t35647");

        Assert.True(result.Ok, result.Error);
        await using var check = CreateDb();
        var task = await check.ForecastTasks.SingleAsync();
        Assert.Equal(("T35647", "Acme Corp", "Development", "P1078"), (task.ExternalId, task.ProjectName, task.TaskName, task.ProjectCode));
    }

    [Fact]
    public async Task Update_rejects_a_duplicate_identity_and_invalid_input()
    {
        await AddTaskAsync("T1", "Acme", "Dev");
        var id = await AddTaskAsync("T2", "Acme", "Support");
        var catalog = Catalog();

        Assert.False((await catalog.UpdateAsync(id, "Acme", "Support", null, "T1")).Ok);
        Assert.False((await catalog.UpdateAsync(id, "", "Support", null, null)).Ok);
        Assert.False((await catalog.UpdateAsync(id, "Acme", "Support", "nope", null)).Ok);
        Assert.False((await catalog.UpdateAsync(9999, "Acme", "Support", null, null)).Ok);
    }

    [Fact]
    public async Task Bulk_activation_and_favorites_apply_to_the_selection_only()
    {
        var a = await AddTaskAsync("T1", "Acme", "A");
        var b = await AddTaskAsync("T2", "Acme", "B");
        var c = await AddTaskAsync("T3", "Acme", "C");
        var catalog = Catalog();

        Assert.Equal(2, await catalog.SetActiveAsync([a, b], false));
        Assert.Equal(1, await catalog.SetFavoriteAsync([c], true));

        await using var check = CreateDb();
        var tasks = await check.ForecastTasks.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal([false, false, true], tasks.Select(t => t.IsActive));
        Assert.Equal([false, false, true], tasks.Select(t => t.IsFavorite));
    }

    [Fact]
    public async Task Delete_removes_unused_tasks_and_keeps_the_referenced_ones()
    {
        var free = await AddTaskAsync("T1", "Acme", "Free");
        var ruled = await AddTaskAsync("T2", "Acme", "Ruled");
        var billed = await AddTaskAsync("T3", "Acme", "Billed");
        var ignored = await AddTaskAsync(ForecastTask.IgnoredExternalId, "Ignoré", "Ignoré");
        await using (var db = CreateDb())
        {
            db.Rules.Add(new Rule { Name = "r", Field = RuleField.Project, Operator = RuleOperator.Equals, Pattern = "x", ForecastTaskId = ruled });
            db.TimesheetDrafts.Add(new TimesheetDraft
            {
                WeekStart = new DateOnly(2026, 10, 5),
                Entries = [new TimesheetEntry { Date = new DateOnly(2026, 10, 5), ForecastTaskId = billed, Hours = 1m }],
            });
            await db.SaveChangesAsync();
        }

        var report = await Catalog().DeleteAsync([free, ruled, billed, ignored]);

        Assert.Equal(1, report.Deleted);
        Assert.Equal([ruled, billed], report.Blocked.Select(b => b.TaskId).Order());
        await using var check = CreateDb();
        Assert.Equal([ForecastTask.IgnoredExternalId, "T2", "T3"], check.ForecastTasks.Select(t => t.ExternalId).ToList().Order());
    }

    [Fact]
    public async Task Delete_keeps_tasks_sent_to_forecast_or_named_in_the_journal()
    {
        var sent = await AddTaskAsync("T1", "Acme", "Sent");
        var journaled = await AddTaskAsync("T2", "Acme", "Journaled");
        await using (var db = CreateDb())
        {
            var day = new DaySubmission
            {
                WeekStart = new DateOnly(2026, 10, 5),
                Date = new DateOnly(2026, 10, 5),
                Status = DayStatus.Sent,
                Cells = [new DaySubmissionCell { ForecastTaskId = sent, SentHours = 1m }],
            };
            db.DaySubmissions.Add(day);
            await db.SaveChangesAsync();
            db.SubmissionJournal.Add(new SubmissionJournalEntry
            {
                DaySubmissionId = day.Id, TimestampUtc = DateTime.UtcNow, Kind = JournalKind.Failed, Outcome = JournalOutcome.Failed,
                ForecastTaskId = journaled, Message = "x",
            });
            await db.SaveChangesAsync();
        }

        var report = await Catalog().DeleteAsync([sent, journaled]);

        Assert.Equal(0, report.Deleted);
        Assert.Equal([sent, journaled], report.Blocked.Select(b => b.TaskId).Order());
    }

    [Fact]
    public async Task A_forecast_sync_keeps_the_favorite_flag()
    {
        var id = await AddTaskAsync("T1", "Acme", "Dev", favorite: true);

        await using (var db = CreateDb())
            await ForecastTaskSync.UpsertAsync(db, [new ParsedForecastTask("T1", "Acme", null, null, "Dev renamed")], DateTime.UtcNow);

        await using var check = CreateDb();
        var task = await check.ForecastTasks.SingleAsync(t => t.Id == id);
        Assert.True(task.IsFavorite);
        Assert.Equal("Dev renamed", task.TaskName);
    }

    [Fact]
    public async Task Excluding_from_submission_applies_to_the_selection_never_to_the_ignored_task_and_is_filterable_and_counted()
    {
        var a = await AddTaskAsync("T1", "Acme", "A");
        var b = await AddTaskAsync("T2", "Acme", "B");
        var ignored = await AddTaskAsync(ForecastTask.IgnoredExternalId, "Ignoré", "Ignoré");
        var catalog = Catalog();

        Assert.Equal(1, await catalog.SetExcludeFromSubmissionAsync([a, ignored], true));

        var excluded = await catalog.QueryAsync(new ForecastTaskQuery(ExcludedOnly: true), 0, 50);
        Assert.Equal(["T1"], excluded.Items.Select(r => r.Task.ExternalId));
        Assert.Equal(1, (await catalog.CountsAsync()).Excluded);

        Assert.Equal(1, await catalog.SetExcludeFromSubmissionAsync([a, b], false));
        Assert.Equal(0, (await catalog.CountsAsync()).Excluded);
    }

    [Fact]
    public async Task A_forecast_sync_keeps_the_exclusion_flag()
    {
        var id = await AddTaskAsync("T1", "Acme", "Dev");
        await Catalog().SetExcludeFromSubmissionAsync([id], true);

        await using (var db = CreateDb())
            await ForecastTaskSync.UpsertAsync(db, [new ParsedForecastTask("T1", "Acme", null, null, "Dev renamed")], DateTime.UtcNow);

        await using var check = CreateDb();
        var task = await check.ForecastTasks.SingleAsync(t => t.Id == id);
        Assert.True(task.ExcludeFromSubmission);
        Assert.Equal("Dev renamed", task.TaskName);
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }
}
