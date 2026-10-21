using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;
using TimeSheetHelper.Tests.Engine;

namespace TimeSheetHelper.Tests.Storage;

public sealed class ActivityStoreMaintenanceTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);

    private static ActivityEvent Old(int daysAgo, string title = "x") =>
        Fx.Act(ActivitySource.Focus, Now.AddDays(-daysAgo), Now.AddDays(-daysAgo).AddMinutes(10), a => a.Title = title);

    [Fact]
    public async Task An_empty_store_has_a_zero_footprint()
    {
        await using var host = new EngineTestHost();

        Assert.Equal(new ActivityFootprint(0, 0), await host.Get<ActivityStore>().GetFootprintAsync());
    }

    [Fact]
    public async Task The_footprint_counts_rows_and_adds_text_length_and_a_fixed_row_overhead()
    {
        await using var host = new EngineTestHost();
        await host.AddActivitiesAsync(Old(1, "abcd"), Old(2, "é"));

        var footprint = await host.Get<ActivityStore>().GetFootprintAsync();

        // "Focus" = 5 bytes per row; "abcd" = 4 bytes; "é" = 2 bytes in UTF-8.
        Assert.Equal(2, footprint.Count);
        Assert.Equal(5 + 4 + 5 + 2 + 2L * ActivityFootprint.RowOverheadBytes, footprint.ApproxBytes);
    }

    [Fact]
    public async Task Deleting_before_a_cutoff_removes_only_activities_that_started_earlier()
    {
        await using var host = new EngineTestHost();
        await host.AddActivitiesAsync(Old(100, "old"), Old(91, "older-than-cutoff"), Old(10, "recent"));
        var store = host.Get<ActivityStore>();

        var removed = await store.DeleteOlderThanAsync(Now.AddDays(-90));

        Assert.Equal(2, removed);
        await using var db = host.Db();
        Assert.Equal(["recent"], await db.Activities.Select(a => a.Title!).ToListAsync());
    }

    [Fact]
    public async Task Deleting_without_a_cutoff_erases_everything_and_is_safe_on_an_empty_store()
    {
        await using var host = new EngineTestHost();
        await host.AddActivitiesAsync(Old(1), Old(200));
        var store = host.Get<ActivityStore>();

        Assert.Equal(2, await store.DeleteOlderThanAsync(null));
        Assert.Equal(0, await store.DeleteOlderThanAsync(null));
        Assert.Equal(0, (await store.GetFootprintAsync()).Count);
    }

    [Fact]
    public async Task Erasing_activity_keeps_manual_assignments_and_rules()
    {
        await using var host = new EngineTestHost();
        var task = await host.AddTaskAsync("P", "T");
        await host.AddRuleAsync(RuleField.ProcessName, RuleOperator.Contains, "code", task.Id);
        await host.AddActivitiesAsync(Old(1));
        await using (var db = host.Db())
        {
            db.SlotOverrides.Add(new SlotOverride { SlotStartUtc = Now, ForecastTaskId = task.Id });
            await db.SaveChangesAsync();
        }

        await host.Get<ActivityStore>().DeleteOlderThanAsync(null);

        await using var check = host.Db();
        Assert.Equal(1, await check.SlotOverrides.CountAsync());
        Assert.Equal(1, await check.Rules.CountAsync());
        Assert.Equal(1, await check.ForecastTasks.CountAsync(t => t.Id == task.Id));
    }
}
