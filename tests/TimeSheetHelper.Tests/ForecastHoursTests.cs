using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastHoursTests
{
    [Theory]
    [InlineData("1.1", "1")]
    [InlineData("1.125", "1.25")]
    [InlineData("1.37", "1.25")]
    [InlineData("1.38", "1.5")]
    [InlineData("7.875", "8")]
    [InlineData("0.1", "0")]
    public void RoundToQuarter_rounds_to_nearest_quarter(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            ForecastHours.RoundToQuarter(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(1.5, ForecastHourFormat.Decimal, "1.5")]
    [InlineData(1.5, ForecastHourFormat.DecimalComma, "1,5")]
    [InlineData(1.5, ForecastHourFormat.HoursMinutes, "1h 30m")]
    [InlineData(8, ForecastHourFormat.Decimal, "8")]
    [InlineData(8, ForecastHourFormat.HoursMinutes, "8h")]
    [InlineData(0.25, ForecastHourFormat.HoursMinutes, "15m")]
    [InlineData(0.25, ForecastHourFormat.DecimalComma, "0,25")]
    [InlineData(2.75, ForecastHourFormat.HoursMinutes, "2h 45m")]
    [InlineData(1.1, ForecastHourFormat.Decimal, "1")]
    public void Format_uses_requested_format_after_rounding(double hours, ForecastHourFormat format, string expected) =>
        Assert.Equal(expected, ForecastHours.Format((decimal)hours, format));

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1,5", 1.5)]
    [InlineData("1h 30m", 1.5)]
    [InlineData("1h30", 1.5)]
    [InlineData("1 h 30 min", 1.5)]
    [InlineData("90m", 1.5)]
    [InlineData("1:30", 1.5)]
    [InlineData("8h", 8)]
    [InlineData("7.5h", 7.5)]
    [InlineData("15m", 0.25)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("-", 0)]
    public void TryParse_reads_back_grid_values(string text, double expected)
    {
        Assert.True(ForecastHours.TryParse(text, out var hours));
        Assert.Equal((decimal)expected, hours);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1h x")]
    public void TryParse_rejects_garbage(string text) =>
        Assert.False(ForecastHours.TryParse(text, out _));

    [Theory]
    [InlineData(ForecastHourFormat.Decimal)]
    [InlineData(ForecastHourFormat.DecimalComma)]
    [InlineData(ForecastHourFormat.HoursMinutes)]
    public void Format_then_parse_round_trips(ForecastHourFormat format)
    {
        for (var h = 0.25m; h <= 12m; h += 0.25m)
        {
            Assert.True(ForecastHours.TryParse(ForecastHours.Format(h, format), out var parsed));
            Assert.Equal(h, parsed);
        }
    }
}
