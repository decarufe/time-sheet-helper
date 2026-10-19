namespace TimeSheetHelper.App.Components.Shared;

/// <summary>
/// The days of the displayed week that the simulation and the fill cover. Only days of the week on screen can be selected: a selection
/// never follows the user to another week. Pure.
/// </summary>
public static class DaySelection
{
    public static IReadOnlyList<DateOnly> WeekOf(DateOnly monday) => Enumerable.Range(0, 7).Select(monday.AddDays).ToList();

    public static bool IsWholeWeek(IReadOnlyCollection<DateOnly> selected, DateOnly monday) => WeekOf(monday).ToHashSet().SetEquals(selected);

    /// <summary>
    /// What is checked when a week opens: today alone when the week holds it (the day-by-day workflow needs no click), nothing
    /// otherwise — a past (or future) week is never entered by default, each day to submit is checked on purpose.
    /// </summary>
    public static HashSet<DateOnly> Default(DateOnly monday, DateOnly today) =>
        today >= monday && today < monday.AddDays(7) ? [today] : [];

    /// <summary>The selected days that belong to the week on screen: a change of week drops the others.</summary>
    public static HashSet<DateOnly> Prune(IEnumerable<DateOnly> selected, DateOnly monday) =>
        selected.Where(d => d >= monday && d < monday.AddDays(7)).ToHashSet();
}
