using TimeSheetHelper.App.Components.Layout;

namespace TimeSheetHelper.Tests.App;

public class ShortcutMapTests
{
    private static KeyChord Chord(string key, bool alt = false, bool ctrl = false, bool meta = false, bool shift = false, bool inField = false)
        => new(key, alt, ctrl, meta, shift, inField);

    [Theory]
    [InlineData("1", "")]
    [InlineData("2", "jour")]
    [InlineData("3", "soumettre")]
    [InlineData("4", "regles")]
    [InlineData("5", "taches")]
    [InlineData("6", "sources")]
    [InlineData("7", "activite")]
    public void Alt_digit_navigates_to_the_matching_route(string key, string route)
    {
        var action = ShortcutMap.Resolve(Chord(key, alt: true));

        Assert.Equal(ShortcutKind.Navigate, action.Kind);
        Assert.Equal(route, action.Route);
    }

    [Fact]
    public void Alt_digit_stays_active_in_an_editable_field()
    {
        var action = ShortcutMap.Resolve(Chord("3", alt: true, inField: true));

        Assert.Equal(ShortcutKind.Navigate, action.Kind);
        Assert.Equal("soumettre", action.Route);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("8")]
    [InlineData("9")]
    [InlineData("a")]
    public void Alt_key_outside_the_seven_destinations_does_nothing(string key)
    {
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord(key, alt: true)).Kind);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    public void Digit_without_alt_does_nothing(string key)
    {
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord(key)).Kind);
    }

    [Theory]
    [InlineData("t", false)]
    [InlineData("T", false)]
    [InlineData("t", true)]
    public void Alt_T_toggles_the_theme_in_any_case_and_even_in_a_field(string key, bool inField)
    {
        Assert.Equal(ShortcutKind.ToggleTheme, ShortcutMap.Resolve(Chord(key, alt: true, inField: inField)).Kind);
    }

    [Fact]
    public void T_without_alt_does_nothing()
    {
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord("t")).Kind);
    }

    [Theory]
    [InlineData("[", ShortcutKind.PreviousWeek)]
    [InlineData("]", ShortcutKind.NextWeek)]
    public void Brackets_change_week(string key, ShortcutKind expected)
    {
        Assert.Equal(expected, ShortcutMap.Resolve(Chord(key)).Kind);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("]")]
    public void Brackets_in_a_field_are_left_to_the_field(string key)
    {
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord(key, inField: true)).Kind);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Ctrl_or_Meta_B_toggles_the_panel(bool ctrl, bool meta)
    {
        Assert.Equal(ShortcutKind.TogglePanel, ShortcutMap.Resolve(Chord("b", ctrl: ctrl, meta: meta)).Kind);
        Assert.Equal(ShortcutKind.TogglePanel, ShortcutMap.Resolve(Chord("B", ctrl: ctrl, meta: meta, shift: true)).Kind);
    }

    [Fact]
    public void Ctrl_B_in_a_field_is_left_to_the_editor()
    {
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord("b", ctrl: true, inField: true)).Kind);
    }

    [Fact]
    public void Plain_B_and_other_ctrl_combinations_do_nothing()
    {
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord("b")).Kind);
        Assert.Equal(ShortcutKind.None, ShortcutMap.Resolve(Chord("k", ctrl: true)).Kind);
    }

    [Fact]
    public void Destinations_are_numbered_one_to_seven_in_order_with_unique_routes()
    {
        var destinations = ShortcutMap.Destinations;

        Assert.Equal(Enumerable.Range(1, 7), destinations.Select(d => d.Shortcut));
        Assert.Equal(destinations.Count, destinations.Select(d => d.Route).Distinct().Count());
        Assert.Equal(destinations.Count, destinations.Select(d => d.Label).Distinct().Count());
    }

    [Fact]
    public void Destinations_belong_to_the_four_rail_sections_in_order()
    {
        var sections = ShortcutMap.Destinations.Select(d => d.Section).ToList();

        Assert.Equal(sections.OrderBy(s => s), sections);
        Assert.Equal(Enum.GetValues<NavSection>(), sections.Distinct());
    }

    [Theory]
    [InlineData("", "Semaine")]
    [InlineData("/", "Semaine")]
    [InlineData("?week=2026-10-19", "Semaine")]
    [InlineData("jour", "Jour")]
    [InlineData("jour/2026-10-19", "Jour")]
    [InlineData("jour/2026-10-19?x=1#top", "Jour")]
    [InlineData("Soumettre", "Soumettre")]
    [InlineData("regles", "Règles")]
    [InlineData("taches/12", "Tâches")]
    [InlineData("sources", "Sources")]
    [InlineData("activite", "Activité brute")]
    public void DestinationOf_resolves_the_owning_destination(string path, string label)
    {
        Assert.Equal(label, ShortcutMap.DestinationOf(path)?.Label);
    }

    [Theory]
    [InlineData("inconnu")]
    [InlineData("journal")]
    public void DestinationOf_returns_null_outside_the_rail(string path)
    {
        Assert.Null(ShortcutMap.DestinationOf(path));
    }
}
