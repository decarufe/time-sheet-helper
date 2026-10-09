using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TimeSheetHelper.Forecast;

/// <summary>Text helpers shared by the grid readers.</summary>
public static partial class ForecastWeekGrid
{
    /// <summary>Lower case, accents removed, whitespace collapsed.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        }

        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
