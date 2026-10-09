using TimeSheetHelper.App;
using TimeSheetHelper.App.Services;

namespace TimeSheetHelper.Tests.App;

public sealed class WeeklyReminderTests
{
    private static readonly ReminderOptions Friday3pm = new() { DayOfWeek = DayOfWeek.Friday, Time = new TimeSpan(15, 0, 0) };

    [Fact]
    public void Due_once_on_the_configured_day_after_the_time()
    {
        var reminder = new WeeklyReminder(Friday3pm);

        Assert.False(reminder.IsDue(new DateTime(2026, 10, 9, 14, 59, 0)));
        Assert.True(reminder.IsDue(new DateTime(2026, 10, 9, 15, 0, 0)));

        reminder.MarkShown(new DateTime(2026, 10, 9, 15, 0, 0));
        Assert.False(reminder.IsDue(new DateTime(2026, 10, 9, 16, 30, 0)));

        // Next Friday it is due again.
        Assert.True(reminder.IsDue(new DateTime(2026, 10, 16, 15, 1, 0)));
    }

    [Fact]
    public void Not_due_on_other_days_or_when_disabled()
    {
        Assert.False(new WeeklyReminder(Friday3pm).IsDue(new DateTime(2026, 10, 8, 16, 0, 0)));
        Assert.False(new WeeklyReminder(new ReminderOptions { Enabled = false }).IsDue(new DateTime(2026, 10, 9, 16, 0, 0)));
    }
}

public sealed class LocalFoldersTests
{
    [Theory]
    [InlineData(@"C:\data\forecast-fill\a\b.png", true)]
    [InlineData(@"C:\data\forecast-fill", true)]
    [InlineData(@"C:\data\forecast-fill\..\data.db", false)]
    [InlineData(@"C:\data\forecast-fill-other\b.png", false)]
    [InlineData(@"C:\other\b.png", false)]
    public void Is_under_rejects_paths_outside_the_root(string path, bool expected) =>
        Assert.Equal(expected, LocalFolders.IsUnder(path, @"C:\data\forecast-fill"));
}
