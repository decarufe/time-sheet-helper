namespace TimeSheetHelper.App;

public sealed class ReminderOptions
{
    public const string SectionName = "Reminder";

    public bool Enabled { get; set; } = true;
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Friday;

    /// <summary>Local time of day, e.g. "15:00".</summary>
    public TimeSpan Time { get; set; } = new(15, 0, 0);
}

/// <summary>Decides when the weekly "review your timesheet" notification is due (at most once per day).</summary>
public sealed class WeeklyReminder(ReminderOptions options)
{
    public const string Message = "Il est temps de réviser votre feuille de temps";

    private DateOnly? _lastShown;

    /// <summary>True once on the configured day, from the configured time until midnight.</summary>
    public bool IsDue(DateTime localNow)
    {
        if (!options.Enabled || localNow.DayOfWeek != options.DayOfWeek || localNow.TimeOfDay < options.Time)
            return false;
        return _lastShown != DateOnly.FromDateTime(localNow);
    }

    public void MarkShown(DateTime localNow) => _lastShown = DateOnly.FromDateTime(localNow);
}
