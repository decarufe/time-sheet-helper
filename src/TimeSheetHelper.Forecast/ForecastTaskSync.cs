using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>Raw texts of one task row scraped from Forecast (cells in display order, link targets).</summary>
public sealed record RawForecastTaskRow(IReadOnlyList<string> Cells, IReadOnlyList<string> Hrefs);

public sealed record ParsedForecastTask(
    string ExternalId,
    string ProjectName,
    string? ProjectCode,
    string? ClientName,
    string TaskName);

public sealed record ForecastTaskSyncResult(int Added, int Updated, int Deactivated, int Seen, string? Warning = null)
{
    public string Summary =>
        Warning ?? $"{Seen} tâche(s) lue(s) : {Added} ajoutée(s), {Updated} mise(s) à jour, {Deactivated} désactivée(s).";
}

/// <summary>Turns scraped rows into tasks and upserts them in the local cache.</summary>
public static class ForecastTaskSync
{
    /// <summary>
    /// UNVERIFIED heuristic: the cell holding the task code ("T123") is the task name, the cell holding the
    /// project code ("P-12") is the project name; otherwise the first two non-code texts are task then project.
    /// Returns null when no task name can be found.
    /// </summary>
    public static ParsedForecastTask? Parse(RawForecastTaskRow row)
    {
        var cells = row.Cells.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        var all = string.Join(" ", cells.Concat(row.Hrefs));

        var taskCode = ForecastSelectors.TaskCode.Match(all) is { Success: true } t ? t.Value : null;
        var projectCode = ForecastSelectors.ProjectCode.Match(all) is { Success: true } p ? p.Value : null;

        string? taskName = null, projectName = null;
        if (taskCode is not null)
            taskName = cells.Where(c => c.Contains(taskCode, StringComparison.Ordinal)).Select(c => Strip(c, taskCode)).FirstOrDefault(IsLabel);
        if (projectCode is not null)
            projectName = cells.Where(c => c.Contains(projectCode, StringComparison.Ordinal)).Select(c => Strip(c, projectCode)).FirstOrDefault(IsLabel);

        var labels = cells
            .Select(c => Strip(Strip(c, taskCode), projectCode))
            .Where(IsLabel)
            .Where(c => c != taskName && c != projectName)
            .ToList();

        taskName ??= labels.FirstOrDefault();
        if (taskName is null)
            return null;
        projectName ??= labels.FirstOrDefault(l => l != taskName) ?? projectCode ?? "(sans projet)";

        var externalId = taskCode ?? $"{projectName}/{taskName}";
        return new ParsedForecastTask(externalId, projectName, projectCode, null, taskName);
    }

    private static string Strip(string text, string? code) =>
        code is null ? text : text.Replace(code, "", StringComparison.Ordinal).Trim(' ', '-', '·', '|', ':', ' ');

    /// <summary>A human label: not empty, not only digits/durations/dates.</summary>
    private static bool IsLabel(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetter) && !ForecastHours.TryParse(text, out _);

    /// <summary>
    /// Upserts the tasks by ExternalId and marks cached tasks that were not seen as inactive (never deleted: rules
    /// and drafts reference them). A cached "project/task" row is re-keyed when the same task now comes with a code.
    /// When nothing was read, nothing is deactivated (a broken selector must not wipe the cache). Scraped pages only
    /// show a weekly subset of the tasks, so callers pass <paramref name="deactivateMissing"/> = false for them.
    /// </summary>
    public static async Task<ForecastTaskSyncResult> UpsertAsync(
        TimeSheetDbContext db, IEnumerable<ParsedForecastTask> parsed, DateTime nowUtc,
        bool deactivateMissing = true, CancellationToken cancellationToken = default)
    {
        var incoming = parsed
            .GroupBy(p => p.ExternalId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (incoming.Count == 0)
            return new ForecastTaskSyncResult(0, 0, 0, 0,
                "Aucune tâche lue dans Forecast : le cache local n'a pas été modifié (sélecteurs à vérifier avec « forecast discover »).");

        var existing = await db.ForecastTasks.ToListAsync(cancellationToken);
        var byExternalId = existing.ToDictionary(t => t.ExternalId, StringComparer.Ordinal);
        var seen = new HashSet<int>();
        int added = 0, updated = 0;

        foreach (var p in incoming)
        {
            var rekeyed = false;
            if (!byExternalId.TryGetValue(p.ExternalId, out var task))
            {
                // Re-key a task cached without code ("project/task") now that Forecast exposes its id.
                var fallbackKey = $"{p.ProjectName}/{p.TaskName}";
                if (p.ExternalId != fallbackKey && byExternalId.TryGetValue(fallbackKey, out task))
                {
                    byExternalId.Remove(fallbackKey);
                    task.ExternalId = p.ExternalId;
                    byExternalId[p.ExternalId] = task;
                    rekeyed = true;
                }
                else if (p.ExternalId == fallbackKey
                    && byExternalId.TryGetValue($"{ForecastTimesheetDom.NoProject}/{p.TaskName}", out task))
                {
                    // Internal time is scraped without a project label: adopt that row instead of duplicating it.
                    byExternalId.Remove($"{ForecastTimesheetDom.NoProject}/{p.TaskName}");
                    task.ExternalId = p.ExternalId;
                    byExternalId[p.ExternalId] = task;
                    rekeyed = true;
                }
            }

            if (task is null)
            {
                task = new ForecastTask
                {
                    ExternalId = p.ExternalId,
                    ProjectName = p.ProjectName,
                    ProjectCode = p.ProjectCode,
                    ClientName = p.ClientName,
                    TaskName = p.TaskName,
                    IsActive = true,
                    LastSeenUtc = nowUtc,
                };
                db.ForecastTasks.Add(task);
                byExternalId[p.ExternalId] = task;
                added++;
                continue;
            }

            if (rekeyed || task.ProjectName != p.ProjectName || task.TaskName != p.TaskName || !task.IsActive
                || (p.ProjectCode is not null && task.ProjectCode != p.ProjectCode)
                || (p.ClientName is not null && task.ClientName != p.ClientName))
                updated++;

            task.ProjectName = p.ProjectName;
            task.TaskName = p.TaskName;
            task.ProjectCode = p.ProjectCode ?? task.ProjectCode;
            task.ClientName = p.ClientName ?? task.ClientName;
            task.IsActive = true;
            task.LastSeenUtc = nowUtc;
            seen.Add(task.Id);
        }

        var deactivated = 0;
        if (deactivateMissing)
        {
            foreach (var task in existing.Where(t => t.IsActive && !seen.Contains(t.Id)))
            {
                task.IsActive = false;
                deactivated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ForecastTaskSyncResult(added, updated, deactivated, incoming.Count);
    }
}
