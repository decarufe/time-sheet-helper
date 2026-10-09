using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TimeSheetHelper.Core.Forecast;

namespace TimeSheetHelper.Forecast;

/// <summary>Pure helpers that interpret what the week grid shows (headers, row labels).</summary>
public static partial class ForecastWeekGrid
{
    /// <summary>
    /// Maps grid column headers to dates of the week starting at <paramref name="weekStart"/>.
    /// Each header is expected to contain the day of month ("Mon 6", "lun. 06 oct.", "10/6"); headers without a
    /// matching day (task name, total...) map to null. Matching is sequential so "10/5", "10/6"... resolve correctly.
    /// </summary>
    public static IReadOnlyList<DateOnly?> MapHeaders(IReadOnlyList<string> headers, DateOnly weekStart)
    {
        var week = Enumerable.Range(0, 7).Select(weekStart.AddDays).ToList();
        var result = new List<DateOnly?>(headers.Count);
        var next = 0;

        foreach (var header in headers)
        {
            var numbers = NumberRegex().Matches(header)
                .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                .Where(n => n is >= 1 and <= 31)
                .ToHashSet();

            DateOnly? match = null;
            for (var i = next; i < week.Count && numbers.Count > 0; i++)
            {
                if (numbers.Contains(week[i].Day))
                {
                    match = week[i];
                    next = i + 1;
                    break;
                }
            }

            result.Add(match);
        }

        return result;
    }

    /// <summary>
    /// The dates of the day columns in display order (non-day columns removed). The hour inputs of a row are
    /// expected in the same order, so the input index of a date is its index in this list.
    /// </summary>
    public static IReadOnlyList<DateOnly> DayColumns(IReadOnlyList<string> headers, DateOnly weekStart) =>
        MapHeaders(headers, weekStart).Where(d => d.HasValue).Select(d => d!.Value).ToList();

    /// <summary>True when the headers show (at least the work days of) the requested week.</summary>
    public static bool IsShowingWeek(IReadOnlyList<string> headers, DateOnly weekStart) =>
        DayColumns(headers, weekStart).Count >= 5;

    /// <summary>
    /// How well a grid row label matches a task: 3 = contains the task code (ExternalId like "T123"),
    /// 2 = task and project names, 1 = task name only, 0 = no match.
    /// </summary>
    public static int MatchScore(string rowText, ForecastTask task)
    {
        var row = Normalize(rowText);
        if (row.Length == 0)
            return 0;

        if (ForecastSelectors.TaskCode.IsMatch(task.ExternalId)
            && Regex.IsMatch(rowText, $@"\b{Regex.Escape(task.ExternalId)}\b", RegexOptions.CultureInvariant))
            return 3;

        if (!row.Contains(Normalize(task.TaskName), StringComparison.Ordinal))
            return 0;

        return row.Contains(Normalize(task.ProjectName), StringComparison.Ordinal) ? 2 : 1;
    }

    /// <summary>
    /// Index of the row that best matches the task, or null when none matches or when the best score is shared
    /// by several rows (ambiguous: e.g. two projects with a "Development" task).
    /// </summary>
    public static int? FindRow(IReadOnlyList<string> rowTexts, ForecastTask task)
    {
        var scored = rowTexts.Select((text, index) => (Index: index, Score: MatchScore(text, task)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ToList();

        if (scored.Count == 0)
            return null;
        if (scored.Count > 1 && scored[1].Score == scored[0].Score)
            return null;
        return scored[0].Index;
    }

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

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
