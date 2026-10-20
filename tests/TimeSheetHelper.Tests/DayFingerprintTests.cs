using System.Globalization;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests;

/// <summary>The fingerprint of a day is stored: it must not move with order, culture or empty cells (G10).</summary>
public sealed class DayFingerprintTests
{
    private static readonly DateOnly Day = new(2026, 10, 13);

    [Fact]
    public void Matches_the_documented_format()
    {
        // SHA-256 of "2026-10-13|1|4.00\n2026-10-13|2|1.50\n".
        Assert.Equal("610c3bb689c3ad85d639373293afed0a62b19178a1e679946b6f63ac80503abb",
            DayFingerprint.Compute(Day, [(2, 1.5m), (1, 4m)]));
    }

    [Fact]
    public void Ignores_order_zero_hours_and_split_lines()
    {
        var reference = DayFingerprint.Compute(Day, [(1, 4m), (2, 1.5m)]);

        Assert.Equal(reference, DayFingerprint.Compute(Day, [(3, 0m), (2, 1.5m), (1, 2.5m), (1, 1.5m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day, [(1, 4m), (2, 1.75m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day.AddDays(1), [(1, 4m), (2, 1.5m)]));
    }

    [Fact]
    public void Does_not_depend_on_the_current_culture()
    {
        var invariant = DayFingerprint.Compute(Day, [(1, 4m), (2, 1.5m)]);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-CA");
            Assert.Equal(invariant, DayFingerprint.Compute(Day, [(1, 4m), (2, 1.5m)]));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("fr-CA")]
    [InlineData("")]
    public void G10_Fingerprint_StableAcrossOrderAndCulture(string cultureName)
    {
        // Decimal comma, Arabic digits or another calendar must not reach the stored fingerprint.
        const string expected = "610c3bb689c3ad85d639373293afed0a62b19178a1e679946b6f63ac80503abb";
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(cultureName);
            Assert.Equal(expected, DayFingerprint.Compute(Day, [(1, 4m), (2, 1.5m)]));
            Assert.Equal(expected, DayFingerprint.Compute(Day, [(2, 1.50m), (1, 4.000m)]));
            Assert.Equal(expected, DayFingerprint.Compute(Day, [(2, 0.75m), (1, 4m), (2, 0.75m)]));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }

    [Fact]
    public void G10_Fingerprint_ChangesWithHoursTaskOrDay()
    {
        var reference = DayFingerprint.Compute(Day, [(1, 4m), (2, 1.5m)]);

        Assert.NotEqual(reference, DayFingerprint.Compute(Day, [(1, 4.25m), (2, 1.5m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day, [(1, 4m), (3, 1.5m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day, [(1, 1.5m), (2, 4m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day, [(1, 4m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day.AddDays(-1), [(1, 4m), (2, 1.5m)]));
        Assert.NotEqual(reference, DayFingerprint.Compute(Day.AddYears(1), [(1, 4m), (2, 1.5m)]));
    }
}
