using System.Globalization;
using System.Text.RegularExpressions;

namespace TimeSheetHelper.Forecast;

public enum ForecastHourFormat
{
    /// <summary>"1.5"</summary>
    Decimal,

    /// <summary>"1,5" (French locale)</summary>
    DecimalComma,

    /// <summary>"1h 30m"</summary>
    HoursMinutes,
}

/// <summary>Rounding, formatting and parsing of hour values exchanged with the Forecast timesheet grid.</summary>
public static partial class ForecastHours
{
    public const decimal Increment = 0.25m;

    /// <summary>Rounds to the nearest quarter hour (midpoint away from zero).</summary>
    public static decimal RoundToQuarter(decimal hours) =>
        Math.Round(hours / Increment, MidpointRounding.AwayFromZero) * Increment;

    public static string Format(decimal hours, ForecastHourFormat format)
    {
        var rounded = RoundToQuarter(hours);
        return format switch
        {
            ForecastHourFormat.Decimal => rounded.ToString("0.##", CultureInfo.InvariantCulture),
            ForecastHourFormat.DecimalComma => rounded.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ','),
            ForecastHourFormat.HoursMinutes => FormatHoursMinutes(rounded),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    private static string FormatHoursMinutes(decimal hours)
    {
        var totalMinutes = (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero);
        var (h, m) = (totalMinutes / 60, totalMinutes % 60);
        return (h, m) switch
        {
            (0, 0) => "0h",
            (_, 0) => $"{h}h",
            (0, _) => $"{m}m",
            _ => $"{h}h {m}m",
        };
    }

    /// <summary>
    /// Parses what the grid displays back into hours. Accepts "1.5", "1,5", "1h 30m", "1h30", "90m", "1:30",
    /// with optional surrounding text such as "h". Empty or "-" means 0.
    /// </summary>
    public static bool TryParse(string? text, out decimal hours)
    {
        hours = 0;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var s = text.Trim().ToLowerInvariant().Replace(' ', ' ');
        if (s is "-" or "—" or "–")
            return true;

        var clock = ClockRegex().Match(s);
        if (clock.Success)
        {
            hours = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture)
                    + int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) / 60m;
            return true;
        }

        // A bare number is hours ("1.5", "1,5"), never minutes.
        if (TryParseNumber(s, out var plain))
        {
            hours = plain;
            return true;
        }

        var hm = HoursMinutesRegex().Match(s);
        if (hm.Success && (hm.Groups["h"].Success || hm.Groups["m"].Success))
        {
            decimal h = 0, m = 0;
            if (hm.Groups["h"].Success && !TryParseNumber(hm.Groups["h"].Value, out h))
                return false;
            if (hm.Groups["m"].Success && !TryParseNumber(hm.Groups["m"].Value, out m))
                return false;
            hours = h + m / 60m;
            return true;
        }

        return false;
    }

    private static bool TryParseNumber(string s, out decimal value) =>
        decimal.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"^(?<h>\d+):(?<m>[0-5]\d)$")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"^(?:(?<h>\d+(?:[.,]\d+)?)\s*h(?:rs?|ours?|eures?)?)?\s*(?:(?<m>\d+(?:[.,]\d+)?)\s*(?:m(?:in(?:utes?)?)?)?)?$")]
    private static partial Regex HoursMinutesRegex();
}
