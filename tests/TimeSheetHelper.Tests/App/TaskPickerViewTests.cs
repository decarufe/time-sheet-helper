using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Engine;

namespace TimeSheetHelper.Tests.App;

public sealed class TaskPickerViewTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static ForecastTask Task(int id, string project, string task, string? client = null, bool favorite = false) =>
        new() { Id = id, ExternalId = $"x{id}", ProjectName = project, TaskName = task, ClientName = client, IsFavorite = favorite };

    private static readonly ForecastTask[] Tasks =
    [
        Task(1, "Alpha", "Dev", "Contoso"),
        Task(2, "Alpha", "Tests", "Contoso", favorite: true),
        Task(3, "Beta", "Analyse", "Fabrikam"),
        Task(4, "Beta", "Support", "Fabrikam"),
    ];

    private static readonly Dictionary<int, TaskUsage> Usage = new()
    {
        [1] = new TaskUsage(1, Today.AddDays(-40), 20),
        [3] = new TaskUsage(3, Today.AddDays(-1), 2),
        [4] = new TaskUsage(4, Today.AddDays(-5), 8),
    };

    private static IReadOnlyList<TaskPickerGroup> Build(TaskPickerOptions options, string query = "") =>
        TaskPickerView.Build(Tasks, query, options, Usage, Today);

    private static int[] Ids(IEnumerable<TaskPickerGroup> groups) => groups.SelectMany(g => g.Tasks).Select(t => t.Id).ToArray();

    [Fact]
    public void Favorites_come_first_and_are_not_repeated_in_their_group()
    {
        var groups = Build(new TaskPickerOptions());

        Assert.True(groups[0].IsFavorites);
        Assert.Equal([2, 1, 3, 4], Ids(groups));
        Assert.Equal(["★ Favoris", "Contoso", "Fabrikam"], groups.Select(g => g.Title));
    }

    [Fact]
    public void Searching_drops_the_favorites_section_and_keeps_matches_only()
    {
        var groups = Build(new TaskPickerOptions(), "contoso");

        Assert.DoesNotContain(groups, g => g.IsFavorites);
        Assert.Equal([1, 2], Ids(groups));
    }

    [Fact]
    public void Recent_scope_keeps_tasks_used_in_the_last_30_days()
    {
        Assert.Equal([3, 4], Ids(Build(new TaskPickerOptions(Scope: TaskScope.Recent))));
        Assert.Equal([2], Ids(Build(new TaskPickerOptions(Scope: TaskScope.Favorites))));
    }

    [Fact]
    public void Usage_sorts_order_tasks_and_their_groups_by_the_best_task()
    {
        var byFrequency = Build(new TaskPickerOptions(TaskGrouping.None, TaskSort.Frequency, TaskScope.All));
        Assert.Equal([2, 1, 4, 3], Ids(byFrequency));

        var byRecent = Build(new TaskPickerOptions(TaskGrouping.ClientProject, TaskSort.Recent));
        Assert.Equal(["★ Favoris", "Fabrikam", "Contoso"], byRecent.Select(g => g.Title));
        Assert.Equal([2, 3, 4, 1], Ids(byRecent));
    }

    [Fact]
    public void Project_filter_and_project_grouping()
    {
        var groups = Build(new TaskPickerOptions(TaskGrouping.Project, Project: "beta"));

        var group = Assert.Single(groups);
        Assert.Equal("Beta", group.Title);
        Assert.Equal("Fabrikam", group.Detail);
        Assert.Equal([3, 4], Ids(groups));
    }
}
