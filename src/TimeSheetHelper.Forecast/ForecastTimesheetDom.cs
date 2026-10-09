using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TimeSheetHelper.Forecast;

/// <summary>One task row of the "My Timesheets" grid (VERIFIED structure, discovery 2026-10-09).</summary>
/// <param name="Index">The "i" of the ids "task-name-time-reg-{i}", "hour-inputs-time-reg-{i}"...</param>
/// <param name="Hours">Text of the 7 day cells, in header order.</param>
public sealed record ForecastGridRow(
    int Index, string? TaskCode, string TaskName, string? ProjectCode, string ProjectName, string? Phase, IReadOnlyList<string> Hours);

public sealed record ForecastGridSnapshot(IReadOnlyList<string> DayHeaders, IReadOnlyList<ForecastGridRow> Rows)
{
    public static readonly ForecastGridSnapshot Empty = new([], []);
}

/// <summary>
/// Reads the Forecast timesheet grid. The grid is not an ARIA table: it is a flat CSS grid where every row is a run
/// of siblings identified by index ("project-indicator-time-reg-0", "task-name-time-reg-0", "hour-inputs-time-reg-0"
/// holding 7 buttons data-cy="hours-input-popup-toggle", Sunday to Saturday). Task and project codes are in the
/// title attributes: "T3382 - Rencontre Bimensuelle Nexus", "P225 - Nexus - Interne".
/// </summary>
public static partial class ForecastTimesheetDom
{
    private const string SnapshotScript = """
        () => {
          const text = e => (e?.innerText ?? '').replace(/\s+/g, ' ').trim();
          const dayRx = /^(sunday|monday|tuesday|wednesday|thursday|friday|saturday|dimanche|lundi|mardi|mercredi|jeudi|vendredi|samedi)\s+\d{1,2}\b/i;
          const page = document.querySelector('[data-cy=timesheets-page]') ?? document.body;
          const headers = [...page.querySelectorAll('div')]
            .filter(d => d.children.length === 2 && dayRx.test(text(d)))
            .map(text);
          const rows = [...page.querySelectorAll('[id^=hour-inputs-time-reg-]')].map(cells => {
            const i = cells.id.substring('hour-inputs-time-reg-'.length);
            const task = document.getElementById('task-name-time-reg-' + i);
            const project = document.querySelector('#project-indicator-time-reg-' + i + ' a');
            const projectName = document.getElementById('project-name-time-reg-' + i);
            const phase = document.getElementById('phase-time-reg-' + i);
            return {
              index: Number(i),
              taskTitle: task?.getAttribute('title') ?? '',
              taskName: text(task),
              projectTitle: project?.getAttribute('title') ?? '',
              projectName: text(projectName),
              phase: text(phase),
              hours: [...cells.querySelectorAll('[data-cy=hours-input-popup-toggle]')].map(text),
            };
          });
          return { headers: [...new Set(headers)].slice(0, 7), rows };
        }
        """;

    public static async Task<ForecastGridSnapshot> ReadAsync(IPage page)
    {
        var json = await page.EvaluateAsync<JsonElement>(SnapshotScript);
        var headers = json.GetProperty("headers").EnumerateArray().Select(h => h.GetString() ?? "").ToList();
        var rows = json.GetProperty("rows").EnumerateArray().Select(r => ParseRow(
            r.GetProperty("index").GetInt32(),
            r.GetProperty("taskTitle").GetString(),
            r.GetProperty("taskName").GetString(),
            r.GetProperty("projectTitle").GetString(),
            r.GetProperty("projectName").GetString(),
            r.GetProperty("phase").GetString(),
            r.GetProperty("hours").EnumerateArray().Select(h => h.GetString() ?? "").ToList())).ToList();
        return new ForecastGridSnapshot(headers, rows);
    }

    /// <summary>Builds a row from the raw texts; codes come from the "T123 - name" / "P12 - name" title attributes.</summary>
    public static ForecastGridRow ParseRow(
        int index, string? taskTitle, string? taskName, string? projectTitle, string? projectName, string? phase, IReadOnlyList<string> hours)
    {
        var (taskCode, taskLabel) = SplitCode(taskTitle, ForecastSelectors.TaskCode);
        var (projectCode, projectLabel) = SplitCode(projectTitle, ForecastSelectors.ProjectCode);
        return new ForecastGridRow(
            index,
            taskCode,
            NonEmpty(taskName) ?? taskLabel ?? "(sans nom)",
            projectCode,
            NonEmpty(projectName) ?? projectLabel ?? projectCode ?? "(sans projet)",
            NonEmpty(phase),
            hours);
    }

    /// <summary>"T3382 - Rencontre Bimensuelle Nexus" → ("T3382", "Rencontre Bimensuelle Nexus").</summary>
    internal static (string? Code, string? Label) SplitCode(string? title, Regex code)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (null, null);
        var match = CodePrefix().Match(title.Trim());
        if (match.Success && code.IsMatch(match.Groups["code"].Value))
            return (match.Groups["code"].Value, NonEmpty(match.Groups["label"].Value));
        return (null, title.Trim());
    }

    /// <summary>
    /// Dates of the day headers ("Sunday 04 Oct", "Monday 05 Oct"...). The week shown can start on Sunday or Monday
    /// (Forecast profile setting), so the dates are resolved around <paramref name="near"/> rather than assumed.
    /// Returns null for a header that cannot be read.
    /// </summary>
    public static IReadOnlyList<DateOnly?> HeaderDates(IReadOnlyList<string> headers, DateOnly near)
    {
        var result = new List<DateOnly?>(headers.Count);
        foreach (var header in headers)
        {
            var match = HeaderPattern().Match(header);
            if (!match.Success || !TryMonth(match.Groups["month"].Value, out var month))
            {
                result.Add(null);
                continue;
            }

            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            // The year is not displayed: pick the one that puts the date closest to the requested week.
            DateOnly? best = null;
            foreach (var year in new[] { near.Year - 1, near.Year, near.Year + 1 })
            {
                if (day > DateTime.DaysInMonth(year, month))
                    continue;
                var candidate = new DateOnly(year, month, day);
                if (best is null || Math.Abs(candidate.DayNumber - near.DayNumber) < Math.Abs(best.Value.DayNumber - near.DayNumber))
                    best = candidate;
            }
            result.Add(best);
        }
        return result;
    }

    /// <summary>True when the 7 headers cover the Monday-to-Friday of the week starting at <paramref name="monday"/>.</summary>
    public static bool IsShowingWeek(IReadOnlyList<string> headers, DateOnly monday)
    {
        var dates = HeaderDates(headers, monday).OfType<DateOnly>().ToHashSet();
        return Enumerable.Range(0, 5).All(i => dates.Contains(monday.AddDays(i)));
    }

    /// <summary>The row for a task: by code ("T123") first, then by project + task name; null when absent or ambiguous.</summary>
    public static ForecastGridRow? FindRow(IReadOnlyList<ForecastGridRow> rows, Core.Forecast.ForecastTask task)
    {
        if (ForecastSelectors.TaskCode.IsMatch(task.ExternalId))
        {
            var byCode = rows.Where(r => string.Equals(r.TaskCode, task.ExternalId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCode.Count > 0)
                return byCode[0];
        }

        var byName = rows
            .Where(r => ForecastWeekGrid.Normalize(r.TaskName) == ForecastWeekGrid.Normalize(task.TaskName))
            .Where(r => ForecastWeekGrid.Normalize(r.ProjectName) == ForecastWeekGrid.Normalize(task.ProjectName))
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static bool TryMonth(string text, out int month)
    {
        // English and French abbreviations; "juin"/"juil" need 4 letters to be told apart.
        var name = ForecastWeekGrid.Normalize(text);
        month = name switch
        {
            _ when name.StartsWith("juin", StringComparison.Ordinal) => 6,
            _ when name.StartsWith("juil", StringComparison.Ordinal) => 7,
            _ => (name.Length >= 3 ? name[..3] : name) switch
            {
                "jan" => 1,
                "feb" or "fev" => 2,
                "mar" => 3,
                "apr" or "avr" => 4,
                "may" or "mai" => 5,
                "jun" => 6,
                "jul" => 7,
                "aug" or "aou" => 8,
                "sep" => 9,
                "oct" => 10,
                "nov" => 11,
                "dec" => 12,
                _ => 0,
            },
        };
        return month != 0;
    }

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex(@"^(?<code>[A-Z]-?\d{1,7})\s*-\s*(?<label>.*)$")]
    private static partial Regex CodePrefix();

    [GeneratedRegex(@"(?<day>\d{1,2})\s+(?<month>\p{L}+)\.?\s*$")]
    private static partial Regex HeaderPattern();
}
