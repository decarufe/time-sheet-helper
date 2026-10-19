using System.Globalization;
using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.App.Services;

namespace TimeSheetHelper.App.Components.Layout;

/// <summary>State of the window tracking plugin ("focus-tracker") as shown by the shell.</summary>
public enum TrackingState { Disabled, Live, Paused }

/// <summary>French labels shared by the shell components (header, panel, stepper).</summary>
internal static class ShellFormat
{
    private static CultureInfo French => TimesheetUi.French;

    /// <summary>"7,5 h", "40 h".</summary>
    public static string Hours(decimal hours) => hours.ToString("0.##", French) + " h";

    /// <summary>"36 h / 40 h".</summary>
    public static string HoursOf(decimal hours, decimal target) => $"{hours.ToString("0.##", French)} h / {Hours(target)}";

    public static int WeekNumber(DateOnly weekStart) => ISOWeek.GetWeekOfYear(weekStart.ToDateTime(TimeOnly.MinValue));

    /// <summary>"13–17 juil." or "29 sept. – 3 oct." (Monday to Friday).</summary>
    public static string WeekRange(DateOnly weekStart)
    {
        var friday = weekStart.AddDays(4);
        return weekStart.Month == friday.Month
            ? $"{weekStart.Day}–{friday.ToString("d MMM", French)}"
            : $"{weekStart.ToString("d MMM", French)} – {friday.ToString("d MMM", French)}";
    }

    /// <summary>"Semaine 29 · 13–17 juil."</summary>
    public static string Kicker(DateOnly weekStart) => $"Semaine {WeekNumber(weekStart)} · {WeekRange(weekStart)}";

    /// <summary>Spoken label of a week, announced after a week change.</summary>
    public static string WeekAnnouncement(DateOnly weekStart) =>
        $"Semaine {WeekNumber(weekStart)}, du {weekStart.ToString("d MMMM", French)} au {weekStart.AddDays(6).ToString("d MMMM yyyy", French)}";

    /// <summary>"Lun 19".</summary>
    public static string ShortDay(DateOnly day) =>
        TimesheetUi.Capitalize(day.ToString("ddd", French).TrimEnd('.')) + " " + day.Day;

    public static string BadgeLabel(DayBadge badge) => badge switch
    {
        DayBadge.Draft => "Brouillon",
        DayBadge.Gap => "Écart",
        DayBadge.Ready => "Prêt",
        DayBadge.Sent => "Envoyé",
        DayBadge.Modified => "Modifié depuis l'envoi",
        _ => "Non saisi",
    };

    /// <summary>Shape of the day dot: ok = check, warn = triangle, none = empty ring (never colour alone).</summary>
    public static string DotTone(DayBadge badge) => badge switch
    {
        DayBadge.Ready or DayBadge.Sent => "ok",
        DayBadge.Draft or DayBadge.Gap or DayBadge.Modified => "warn",
        _ => "none",
    };

    public static string DotIcon(string tone) => tone switch
    {
        "ok" => "check",
        "warn" => "warn",
        _ => "ring",
    };
}
