using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TimeSheetHelper.Forecast;

/// <summary>Forecast naming conventions: tasks are "T1234", projects "P225" or "P-225".</summary>
public static partial class ForecastCodes
{
    public static readonly Regex TaskCode = new(@"\bT\d{1,7}\b");

    public static readonly Regex ProjectCode = new(@"\bP-?\d{1,6}\b");

    /// <summary>Project label of tasks that belong to no project (internal time).</summary>
    public const string NoProject = "(sans projet)";

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
