namespace TimeSheetHelper.App.Components.Layout;

public enum ShortcutKind { None, Navigate, PreviousWeek, NextWeek, TogglePanel, ToggleTheme }

/// <param name="Route">Relative path without a leading "/" ("" = Semaine), usable as is by NavigationManager.NavigateTo.</param>
public readonly record struct ShortcutAction(ShortcutKind Kind, string? Route = null);

/// <summary>A key press as reported by shell.js (KeyboardEvent).</summary>
/// <param name="InEditableField">The target is an INPUT, TEXTAREA, SELECT or contentEditable element.</param>
public readonly record struct KeyChord(
    string Key,
    bool Alt,
    bool Ctrl,
    bool Meta,
    bool Shift,
    bool InEditableField);

/// <summary>Rail grouping of the destinations (four sections).</summary>
public enum NavSection { Time, Sending, Configuration, Advanced }

public sealed record NavDestination(
    int Shortcut,
    string Route,
    string Label,
    string Icon,
    NavSection Section);

/// <summary>Pure mapping from a key press to a shell action (no dependency, see nav-c-contracts §2).</summary>
public static class ShortcutMap
{
    private static readonly ShortcutAction None = new(ShortcutKind.None);

    /// <summary>The seven destinations, in Alt+1..7 order.</summary>
    public static IReadOnlyList<NavDestination> Destinations { get; } =
    [
        new(1, "", "Semaine", "semaine", NavSection.Time),
        new(2, "jour", "Jour", "jour", NavSection.Time),
        new(3, "soumettre", "Soumettre", "soumettre", NavSection.Sending),
        new(4, "regles", "Règles", "regles", NavSection.Configuration),
        new(5, "taches", "Tâches", "taches", NavSection.Configuration),
        new(6, "sources", "Sources", "sources", NavSection.Configuration),
        new(7, "activite", "Activité brute", "activite", NavSection.Advanced),
    ];

    public static ShortcutAction Resolve(KeyChord chord)
    {
        var key = chord.Key ?? "";
        var ctrlOrMeta = chord.Ctrl || chord.Meta;

        // "[" / "]" typed through AltGr (reported as Ctrl+Alt, AZERTY/CSA layouts): the typed character counts, not the modifiers.
        if (key is "[" or "]" && !chord.Meta && (chord.Alt || !chord.Ctrl))
        {
            if (chord.InEditableField)
                return None;
            return new ShortcutAction(key == "[" ? ShortcutKind.PreviousWeek : ShortcutKind.NextWeek);
        }

        if (chord.Alt)
        {
            // Alt shortcuts stay active in a field (like the prototype) but never steal Ctrl/Meta combinations.
            if (ctrlOrMeta)
                return None;
            if (key.Length == 1 && key[0] is >= '1' and <= '7')
                return new ShortcutAction(ShortcutKind.Navigate, Destinations[key[0] - '1'].Route);
            if (key.Equals("t", StringComparison.OrdinalIgnoreCase))
                return new ShortcutAction(ShortcutKind.ToggleTheme);
            return None;
        }

        if (ctrlOrMeta)
        {
            // Ctrl+B in an editor means "bold": leave it alone there.
            if (!chord.InEditableField && key.Equals("b", StringComparison.OrdinalIgnoreCase))
                return new ShortcutAction(ShortcutKind.TogglePanel);
            return None;
        }

        if (chord.InEditableField)
            return None;

        return key switch
        {
            "[" => new ShortcutAction(ShortcutKind.PreviousWeek),
            "]" => new ShortcutAction(ShortcutKind.NextWeek),
            _ => None,
        };
    }

    /// <summary>The destination owning a relative path ("" = Semaine, "jour/2026-10-19" = Jour); null outside the rail.</summary>
    public static NavDestination? DestinationOf(string relativePath)
    {
        var path = relativePath.Split('?', '#')[0].Trim('/');
        if (path.Length == 0)
            return Destinations[0];
        var first = path.Split('/')[0];
        return Destinations.FirstOrDefault(d => d.Route.Length > 0 && d.Route.Equals(first, StringComparison.OrdinalIgnoreCase));
    }
}
