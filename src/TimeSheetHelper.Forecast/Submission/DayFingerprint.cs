using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TimeSheetHelper.Forecast.Submission;

/// <summary>
/// Fingerprint of the content of one day: SHA-256, lowercase hex, over the lines "yyyy-MM-dd|&lt;taskId&gt;|&lt;hours F2&gt;\n" sorted by task,
/// zero hours left out, UTF-8 without BOM. Independent of culture, time zone and order (G10). Hours of one task given twice add up.
/// </summary>
public static class DayFingerprint
{
    public static string Compute(DateOnly date, IEnumerable<(int ForecastTaskId, decimal Hours)> cells)
    {
        var builder = new StringBuilder();
        foreach (var cell in cells
                     .GroupBy(c => c.ForecastTaskId)
                     .Select(g => (TaskId: g.Key, Hours: g.Sum(c => c.Hours)))
                     .Where(c => c.Hours != 0)
                     .OrderBy(c => c.TaskId))
        {
            builder.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append('|').Append(cell.TaskId.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(cell.Hours.ToString("F2", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        var hash = SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
