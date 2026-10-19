using TimeSheetHelper.App.Components.Layout;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>
/// Keeps a week-centric page (Semaine, Jour, Soumettre) and the week of the shell header on the same week.
/// The page reports the week it shows with <see cref="Show"/>; when the header (or the <c>[</c> <c>]</c> shortcuts)
/// selects another week, <paramref name="onWeekRequested"/> receives its Monday so that the page navigates with its
/// usual handler. The callback is raised from the shell event: the page dispatches it with <c>InvokeAsync</c>.
/// </summary>
public sealed class ShellWeekSync : IDisposable
{
    private readonly ShellState shell;
    private readonly Action<DateOnly> onWeekRequested;
    private DateOnly? shown;

    public ShellWeekSync(ShellState shell, Action<DateOnly> onWeekRequested)
    {
        this.shell = shell;
        this.onWeekRequested = onWeekRequested;
        shell.Changed += OnShellChanged;
    }

    /// <summary>Monday of the week the page shows, null before its first <see cref="Show"/>.</summary>
    public DateOnly? Shown => shown;

    /// <summary>The page shows the week of <paramref name="anyDayOfWeek"/>: the header follows.</summary>
    public void Show(DateOnly anyDayOfWeek)
    {
        shown = TimesheetUi.WeekStartOf(anyDayOfWeek);
        shell.SetWeek(shown.Value);
    }

    /// <summary>True while the shell asks for a week the page does not show yet.</summary>
    public bool IsPending => shown is { } week && week != shell.WeekStart;

    private void OnShellChanged()
    {
        if (IsPending)
            onWeekRequested(shell.WeekStart);
    }

    public void Dispose() => shell.Changed -= OnShellChanged;
}
