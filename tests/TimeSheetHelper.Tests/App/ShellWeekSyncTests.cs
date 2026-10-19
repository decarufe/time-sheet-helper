using Microsoft.AspNetCore.Components;
using TimeSheetHelper.App.Components.Layout;
using TimeSheetHelper.App.Components.Pages;
using TimeSheetHelper.App.Components.Shared;

namespace TimeSheetHelper.Tests.App;

public class ShellWeekSyncTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);

    [Fact]
    public void Showing_a_day_moves_the_shell_to_its_week_without_asking_the_page_to_navigate()
    {
        var shell = new ShellState(Monday);
        var requested = new List<DateOnly>();
        using var sync = new ShellWeekSync(shell, requested.Add);

        sync.Show(new DateOnly(2026, 10, 29));

        Assert.Equal(new DateOnly(2026, 10, 26), shell.WeekStart);
        Assert.Equal(new DateOnly(2026, 10, 26), sync.Shown);
        Assert.False(sync.IsPending);
        Assert.Empty(requested);
    }

    [Fact]
    public void A_week_chosen_in_the_header_is_requested_from_the_page_until_it_shows_it()
    {
        var shell = new ShellState(Monday);
        var requested = new List<DateOnly>();
        using var sync = new ShellWeekSync(shell, requested.Add);
        sync.Show(new DateOnly(2026, 10, 21));

        shell.ShiftWeek(-1);

        Assert.Equal([new DateOnly(2026, 10, 12)], requested);
        Assert.True(sync.IsPending);

        sync.Show(new DateOnly(2026, 10, 14));
        shell.SetPanel(false);

        Assert.False(sync.IsPending);
        Assert.Single(requested);
    }

    [Fact]
    public void Nothing_is_requested_before_the_first_show_nor_after_dispose()
    {
        var shell = new ShellState(Monday);
        var requested = new List<DateOnly>();
        var sync = new ShellWeekSync(shell, requested.Add);

        shell.ShiftWeek(1);
        Assert.Empty(requested);

        sync.Show(Monday);
        sync.Dispose();
        shell.ShiftWeek(1);

        Assert.Empty(requested);
    }
}

public class WeekPageNavigationTests
{
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation(string uri) => Initialize("http://localhost/", uri);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/?semaine=2026-10-12")]
    public void The_week_is_always_named_so_that_going_to_the_current_week_reloads_the_page(string current)
    {
        // Without ?semaine= the page may show another week (the one of the shell): "/" must not be reused.
        var nav = new TestNavigation(current);

        Assert.Equal("http://localhost/?semaine=2026-10-19", Week.WeekUri(nav, new DateOnly(2026, 10, 22)));
    }
}
