using Microsoft.AspNetCore.Components;
using TimeSheetHelper.App.Components.Shared;

namespace TimeSheetHelper.App.Components.Layout;

public enum ShellTheme { Light, Dark }

/// <summary>
/// State of the navigation shell, one per Blazor circuit (Scoped). The panel and theme preferences are persisted
/// in localStorage by shell.js ("tsh.panel" = "1"/"0", "tsh.theme" = "light"/"dark"); MainLayout writes them back.
/// </summary>
public sealed class ShellState
{
    private readonly Dictionary<NavSection, string> lastRoutes = [];

    public ShellState() : this(DateOnly.FromDateTime(DateTime.Today))
    {
    }

    public ShellState(DateOnly today) => WeekStart = TimesheetUi.WeekStartOf(today);

    /// <summary>Monday of the displayed week (header + panel).</summary>
    public DateOnly WeekStart { get; private set; }

    public bool PanelOpen { get; private set; } = true;

    public ShellTheme Theme { get; private set; } = ShellTheme.Light;

    /// <summary>Page actions rendered by WeekHeader on the right of the title.</summary>
    public RenderFragment? HeaderActions { get; private set; }

    /// <summary>Page title shown by WeekHeader as the single &lt;h1&gt; of the page.</summary>
    public string? PageTitle { get; private set; }

    public event Action? Changed;

    public void SetWeek(DateOnly anyDayOfWeek)
    {
        var monday = TimesheetUi.WeekStartOf(anyDayOfWeek);
        if (monday == WeekStart)
            return;
        WeekStart = monday;
        Changed?.Invoke();
    }

    public void ShiftWeek(int weeks)
    {
        if (weeks != 0)
            SetWeek(WeekStart.AddDays(7 * weeks));
    }

    public void SetPanel(bool open)
    {
        if (open == PanelOpen)
            return;
        PanelOpen = open;
        Changed?.Invoke();
    }

    public void SetTheme(ShellTheme theme)
    {
        if (theme == Theme)
            return;
        Theme = theme;
        Changed?.Invoke();
    }

    public void SetHeader(string? title, RenderFragment? actions)
    {
        if (title == PageTitle && ReferenceEquals(actions, HeaderActions))
            return;
        PageTitle = title;
        HeaderActions = actions;
        Changed?.Invoke();
    }

    /// <summary>Remembers the last page visited in a section so the rail link returns to it.</summary>
    internal void RememberRoute(NavDestination destination, string relativePath) =>
        lastRoutes[destination.Section] = relativePath;

    /// <summary>The last page visited in the section, or its home page.</summary>
    internal string RouteFor(NavSection section) =>
        lastRoutes.TryGetValue(section, out var route) ? route : ShortcutMap.Destinations.First(d => d.Section == section).Route;
}
