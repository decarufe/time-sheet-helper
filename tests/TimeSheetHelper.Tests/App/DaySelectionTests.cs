using TimeSheetHelper.App.Components.Shared;

namespace TimeSheetHelper.Tests.App;

public sealed class DaySelectionTests
{
    [Fact]
    public void A_week_holding_today_opens_with_today_alone_checked()
    {
        var today = GapFx.Day(2);

        Assert.Equal([today], DaySelection.Default(GapFx.Monday, today));
        Assert.Equal([GapFx.Day(6)], DaySelection.Default(GapFx.Monday, GapFx.Day(6)));
    }

    [Theory]
    [InlineData(-7)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(30)]
    public void A_past_or_future_week_opens_with_nothing_checked(int todayOffset) =>
        Assert.Empty(DaySelection.Default(GapFx.Monday, GapFx.Monday.AddDays(todayOffset)));

    [Fact]
    public void Going_to_another_week_drops_the_days_that_are_no_longer_on_screen()
    {
        var today = GapFx.Day(2);
        var selected = new HashSet<DateOnly> { today, GapFx.Day(3) };

        Assert.Empty(DaySelection.Prune(selected, GapFx.Monday.AddDays(-7)));
        Assert.Empty(DaySelection.Prune(selected, GapFx.Monday.AddDays(7)));
        Assert.Equal(selected, DaySelection.Prune(selected, GapFx.Monday));
        Assert.Equal([GapFx.Day(3)], DaySelection.Prune([GapFx.Monday.AddDays(-1), GapFx.Day(3), GapFx.Monday.AddDays(7)], GapFx.Monday));
    }

    [Fact]
    public void The_whole_week_is_recognised_only_when_the_seven_days_are_checked()
    {
        var week = DaySelection.WeekOf(GapFx.Monday);

        Assert.Equal(7, week.Count);
        Assert.True(DaySelection.IsWholeWeek(week.ToHashSet(), GapFx.Monday));
        Assert.False(DaySelection.IsWholeWeek(week.Take(6).ToHashSet(), GapFx.Monday));
        Assert.False(DaySelection.IsWholeWeek(new HashSet<DateOnly>(), GapFx.Monday));
    }
}
