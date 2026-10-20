using TimeSheetHelper.App.Components.Layout;
using TimeSheetHelper.App.Services;

namespace TimeSheetHelper.Tests.App;

public class ShellStateTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);

    [Theory]
    [InlineData(2026, 10, 19)]
    [InlineData(2026, 10, 21)]
    [InlineData(2026, 10, 25)]
    public void Starts_on_the_monday_of_the_given_day(int y, int m, int d)
    {
        var state = new ShellState(new DateOnly(y, m, d));

        Assert.Equal(Monday, state.WeekStart);
        Assert.Equal(DayOfWeek.Monday, state.WeekStart.DayOfWeek);
    }

    [Fact]
    public void Panel_is_open_and_theme_light_by_default()
    {
        var state = new ShellState(Monday);

        Assert.True(state.PanelOpen);
        Assert.Equal(ShellTheme.Light, state.Theme);
    }

    [Fact]
    public void SetWeek_normalizes_to_monday_and_notifies_once()
    {
        var state = new ShellState(Monday);
        var changes = 0;
        state.Changed += () => changes++;

        state.SetWeek(new DateOnly(2026, 11, 4));

        Assert.Equal(new DateOnly(2026, 11, 2), state.WeekStart);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetWeek_within_the_same_week_does_not_notify()
    {
        var state = new ShellState(Monday);
        var changes = 0;
        state.Changed += () => changes++;

        state.SetWeek(new DateOnly(2026, 10, 23));

        Assert.Equal(Monday, state.WeekStart);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(1, 2026, 10, 26)]
    [InlineData(-1, 2026, 10, 12)]
    [InlineData(5, 2026, 11, 23)]
    public void ShiftWeek_moves_by_whole_weeks(int weeks, int y, int m, int d)
    {
        var state = new ShellState(Monday);

        state.ShiftWeek(weeks);

        Assert.Equal(new DateOnly(y, m, d), state.WeekStart);
    }

    [Fact]
    public void ShiftWeek_zero_does_not_notify()
    {
        var state = new ShellState(Monday);
        var changes = 0;
        state.Changed += () => changes++;

        state.ShiftWeek(0);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Panel_and_theme_notify_only_on_a_real_change()
    {
        var state = new ShellState(Monday);
        var changes = 0;
        state.Changed += () => changes++;

        state.SetPanel(true);
        state.SetTheme(ShellTheme.Light);
        Assert.Equal(0, changes);

        state.SetPanel(false);
        state.SetTheme(ShellTheme.Dark);

        Assert.Equal(2, changes);
        Assert.False(state.PanelOpen);
        Assert.Equal(ShellTheme.Dark, state.Theme);
    }

    [Fact]
    public void SetHeader_notifies_only_when_title_or_actions_change()
    {
        var state = new ShellState(Monday);
        var changes = 0;
        state.Changed += () => changes++;

        state.SetHeader("Semaine", null);
        state.SetHeader("Semaine", null);
        state.SetHeader("Jour", null);

        Assert.Equal(2, changes);
        Assert.Equal("Jour", state.PageTitle);
    }

    [Fact]
    public void RouteFor_defaults_to_the_first_page_of_each_section()
    {
        var state = new ShellState(Monday);

        Assert.Equal("", state.RouteFor(NavSection.Time));
        Assert.Equal("soumettre", state.RouteFor(NavSection.Sending));
        Assert.Equal("regles", state.RouteFor(NavSection.Configuration));
        Assert.Equal("activite", state.RouteFor(NavSection.Advanced));
    }

    [Fact]
    public void RouteFor_returns_the_last_page_visited_in_that_section_only()
    {
        var state = new ShellState(Monday);
        var sources = ShortcutMap.Destinations.Single(d => d.Route == "sources");

        state.RememberRoute(sources, "sources");

        Assert.Equal("sources", state.RouteFor(NavSection.Configuration));
        Assert.Equal("", state.RouteFor(NavSection.Time));
        Assert.Equal("soumettre", state.RouteFor(NavSection.Sending));
    }

    [Fact]
    public void RouteFor_remembers_the_full_relative_path()
    {
        var state = new ShellState(Monday);
        var jour = ShortcutMap.Destinations.Single(d => d.Route == "jour");

        state.RememberRoute(jour, "jour/2026-10-21");

        Assert.Equal("jour/2026-10-21", state.RouteFor(NavSection.Time));
    }
}

public class ShellFormatTests
{
    [Theory]
    [InlineData(7.5, "7,5 h")]
    [InlineData(40, "40 h")]
    [InlineData(0, "0 h")]
    [InlineData(7.25, "7,25 h")]
    public void Hours_uses_the_french_decimal_comma(double hours, string expected)
    {
        Assert.Equal(expected, ShellFormat.Hours((decimal)hours));
    }

    [Fact]
    public void HoursOf_shows_progress_against_the_target()
    {
        Assert.Equal("36 h / 40 h", ShellFormat.HoursOf(36m, 40m));
        Assert.Equal("7,5 h / 8 h", ShellFormat.HoursOf(7.5m, 8m));
    }

    [Theory]
    [InlineData(2026, 7, 13, 29)]
    [InlineData(2026, 10, 19, 43)]
    [InlineData(2025, 12, 29, 1)]
    public void WeekNumber_is_the_iso_week(int y, int m, int d, int week)
    {
        Assert.Equal(week, ShellFormat.WeekNumber(new DateOnly(y, m, d)));
    }

    [Fact]
    public void WeekRange_is_monday_to_friday_within_a_month()
    {
        Assert.Equal("19–23 oct.", ShellFormat.WeekRange(new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public void WeekRange_spans_two_months_when_needed()
    {
        Assert.Equal("28 sept. – 2 oct.", ShellFormat.WeekRange(new DateOnly(2026, 9, 28)));
    }

    [Fact]
    public void Kicker_combines_week_number_and_range()
    {
        Assert.Equal("Semaine 43 · 19–23 oct.", ShellFormat.Kicker(new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public void ShortDay_is_capitalized_without_trailing_dot()
    {
        Assert.Equal("Lun 19", ShellFormat.ShortDay(new DateOnly(2026, 10, 19)));
        Assert.Equal("Ven 23", ShellFormat.ShortDay(new DateOnly(2026, 10, 23)));
    }

    [Theory]
    [InlineData(DayBadge.Empty, "Non saisi", "none", "ring")]
    [InlineData(DayBadge.Draft, "Brouillon", "warn", "warn")]
    [InlineData(DayBadge.Gap, "Écart", "warn", "warn")]
    [InlineData(DayBadge.Ready, "Prêt", "ok", "check")]
    [InlineData(DayBadge.Sent, "Envoyé", "ok", "check")]
    [InlineData(DayBadge.Modified, "Modifié depuis l'envoi", "warn", "warn")]
    public void Badge_has_a_label_and_a_shape_never_colour_alone(DayBadge badge, string label, string tone, string icon)
    {
        Assert.Equal(label, ShellFormat.BadgeLabel(badge));
        Assert.Equal(tone, ShellFormat.DotTone(badge));
        Assert.Equal(icon, ShellFormat.DotIcon(ShellFormat.DotTone(badge)));
    }

    [Fact]
    public void Every_badge_has_a_distinct_label()
    {
        var labels = Enum.GetValues<DayBadge>().Select(ShellFormat.BadgeLabel).ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }
}
