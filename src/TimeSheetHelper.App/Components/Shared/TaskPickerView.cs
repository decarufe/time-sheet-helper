using System.Globalization;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Engine;

namespace TimeSheetHelper.App.Components.Shared;

public enum TaskGrouping
{
    ClientProject,
    Project,
    None,
}

public enum TaskSort
{
    Name,
    Project,
    Recent,
    Frequency,
}

public enum TaskScope
{
    All,
    Favorites,
    Recent,
}

/// <summary>How the task picker lists the tasks; <paramref name="Project"/> keeps only that project when set.</summary>
public sealed record TaskPickerOptions(
    TaskGrouping Grouping = TaskGrouping.ClientProject,
    TaskSort Sort = TaskSort.Name,
    TaskScope Scope = TaskScope.All,
    string? Project = null);

/// <summary>One section of the picker: a title (none when ungrouped), an optional detail and its tasks in display order.</summary>
public sealed record TaskPickerGroup(string? Title, string? Detail, IReadOnlyList<ForecastTask> Tasks, bool IsFavorites = false);

/// <summary>Filtering, sorting and grouping of the task picker list (pure, so it can be tested without rendering).</summary>
public static class TaskPickerView
{
    /// <summary>A task used within this many days counts as recent.</summary>
    public const int RecentDays = 30;

    public const string FavoritesTitle = "★ Favoris";

    public static string GroupingLabel(TaskGrouping grouping) => grouping switch
    {
        TaskGrouping.Project => "Projet",
        TaskGrouping.None => "Aucun",
        _ => "Client et projet",
    };

    public static string SortLabel(TaskSort sort) => sort switch
    {
        TaskSort.Project => "Projet",
        TaskSort.Recent => "Utilisées récemment",
        TaskSort.Frequency => "Plus utilisées",
        _ => "Nom de tâche",
    };

    public static string ScopeLabel(TaskScope scope) => scope switch
    {
        TaskScope.Favorites => "Favoris",
        TaskScope.Recent => "Récentes",
        _ => "Toutes",
    };

    /// <summary>Distinct project names of the selectable tasks, alphabetically.</summary>
    public static IReadOnlyList<string> Projects(IEnumerable<ForecastTask> tasks) =>
        tasks.Where(t => !t.IsIgnored)
            .Select(t => t.ProjectName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static IReadOnlyList<TaskPickerGroup> Build(
        IEnumerable<ForecastTask> tasks,
        string query,
        TaskPickerOptions options,
        IReadOnlyDictionary<int, TaskUsage> usage,
        DateOnly today)
    {
        var recentSince = today.AddDays(-RecentDays);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching = tasks
            .Where(t => !t.IsIgnored)
            .Where(t => terms.All(term => Matches(t, term)))
            .Where(t => options.Project is null || string.Equals(t.ProjectName, options.Project, StringComparison.CurrentCultureIgnoreCase))
            .Where(t => options.Scope switch
            {
                TaskScope.Favorites => t.IsFavorite,
                TaskScope.Recent => usage.TryGetValue(t.Id, out var u) && u.LastUsed >= recentSince,
                _ => true,
            });
        var sorted = Sort(matching, options.Sort, usage).ToList();

        var groups = new List<TaskPickerGroup>();
        if (terms.Length == 0 && options.Scope == TaskScope.All)
        {
            var favorites = sorted.Where(t => t.IsFavorite).ToList();
            if (favorites.Count > 0)
            {
                groups.Add(new TaskPickerGroup(FavoritesTitle, null, favorites, IsFavorites: true));
                sorted = sorted.Where(t => !t.IsFavorite).ToList();
            }
        }

        if (sorted.Count == 0)
            return groups;

        if (options.Grouping == TaskGrouping.None)
        {
            groups.Add(new TaskPickerGroup(null, null, sorted));
            return groups;
        }

        // GroupBy keeps the order in which groups first appear: by usage, the group of the best task comes first.
        var grouped = sorted
            .GroupBy(t => options.Grouping == TaskGrouping.Project
                ? (Title: t.ProjectName, Detail: t.ClientName)
                : (Title: t.ClientName ?? t.ProjectName, Detail: t.ProjectName))
            .Select(g => new TaskPickerGroup(
                g.Key.Title,
                string.Equals(g.Key.Title, g.Key.Detail, StringComparison.Ordinal) ? null : g.Key.Detail,
                g.ToList()));
        if (options.Sort is TaskSort.Name or TaskSort.Project)
        {
            grouped = grouped
                .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(g => g.Detail, StringComparer.CurrentCultureIgnoreCase);
        }
        groups.AddRange(grouped);
        return groups;
    }

    /// <summary>The task Enter picks in the search box: the first one listed.</summary>
    public static ForecastTask? First(IReadOnlyList<TaskPickerGroup> groups) =>
        groups.SelectMany(g => g.Tasks).FirstOrDefault();

    public static bool Matches(ForecastTask task, string term) =>
        Contains(task.TaskName, term) || Contains(task.ProjectName, term) || Contains(task.ClientName, term) || Contains(task.ProjectCode, term);

    private static IEnumerable<ForecastTask> Sort(IEnumerable<ForecastTask> tasks, TaskSort sort, IReadOnlyDictionary<int, TaskUsage> usage)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        DateOnly LastUsed(ForecastTask t) => usage.TryGetValue(t.Id, out var u) ? u.LastUsed : DateOnly.MinValue;
        int Days(ForecastTask t) => usage.TryGetValue(t.Id, out var u) ? u.Days : 0;

        return sort switch
        {
            TaskSort.Project => tasks.OrderBy(t => t.ProjectName, byName).ThenBy(t => t.TaskName, byName),
            TaskSort.Recent => tasks.OrderByDescending(LastUsed).ThenByDescending(Days).ThenBy(t => t.TaskName, byName),
            TaskSort.Frequency => tasks.OrderByDescending(Days).ThenByDescending(LastUsed).ThenBy(t => t.TaskName, byName),
            _ => tasks.OrderBy(t => t.TaskName, byName).ThenBy(t => t.ProjectName, byName),
        };
    }

    public static bool Contains(string? value, string term) =>
        value is not null && TimesheetUi.French.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
