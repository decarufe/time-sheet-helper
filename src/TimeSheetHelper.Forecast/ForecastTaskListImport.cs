using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

public sealed record ForecastTaskListParseResult(IReadOnlyList<ParsedForecastTask> Tasks, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a task list typed or pasted by the user (copied from Excel or a web table): one row per task, project then
/// task separated by a tab, each cell written "P1078-Project name" / "T35647-Task name". The "-Internal Time" form
/// (no code) is used for internal time. Header rows ("Projet / Task", "Column 1 / Column 2") are skipped.
/// </summary>
public static partial class ForecastTaskListImport
{
    private static readonly string[] ProjectHeaders = ["projet", "project", "column 1", "colonne 1"];
    private static readonly string[] TaskHeaders = ["task", "tache", "column 2", "colonne 2"];

    public static ForecastTaskListParseResult Parse(string? text)
    {
        var tasks = new List<ParsedForecastTask>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var lineNumber = 0;
        foreach (var rawLine in (text ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            var cells = rawLine.Split('\t').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (cells.Count != 2)
            {
                warnings.Add($"Ligne {lineNumber} ignorée : « {Short(rawLine)} » — attendu : projet et tâche séparés par une tabulation.");
                continue;
            }

            if (IsHeader(cells[0], ProjectHeaders) && IsHeader(cells[1], TaskHeaders))
                continue;
            if (IsHeader(cells[0], ["column 1", "colonne 1"]) && IsHeader(cells[1], ["column 2", "colonne 2"]))
                continue;

            var (projectCode, projectName) = Split(cells[0], ForecastCodes.ProjectCode);
            var (taskCode, taskName) = Split(cells[1], ForecastCodes.TaskCode);
            if (projectName.Length == 0 || taskName.Length == 0)
            {
                warnings.Add($"Ligne {lineNumber} ignorée : nom de projet ou de tâche vide.");
                continue;
            }

            var task = new ParsedForecastTask(taskCode ?? $"{projectName}/{taskName}", projectName, projectCode, null, taskName);
            if (!seen.Add(task.ExternalId))
            {
                warnings.Add($"Ligne {lineNumber} : « {task.ExternalId} » déjà présente plus haut, ignorée.");
                continue;
            }

            tasks.Add(task);
        }

        return new ForecastTaskListParseResult(tasks, warnings);
    }

    /// <summary>"P1078-M365 - Intranet" → ("P1078", "M365 - Intranet"); "-Internal Time" → (null, "Internal Time").</summary>
    internal static (string? Code, string Name) Split(string cell, Regex codePattern)
    {
        var match = CodePrefix().Match(cell);
        if (match.Success && codePattern.IsMatch(match.Groups["code"].Value.ToUpperInvariant()))
            return (match.Groups["code"].Value.ToUpperInvariant(), CollapseSpaces(match.Groups["name"].Value));
        return (null, CollapseSpaces(cell.TrimStart('-', ' ')));
    }

    /// <summary>The cache as editable text, in the format <see cref="Parse"/> reads back (active tasks only).</summary>
    public static string Format(IEnumerable<Core.Forecast.ForecastTask> tasks) =>
        string.Join(Environment.NewLine, tasks
            .Where(t => t.IsActive && !t.IsIgnored)
            .OrderBy(t => t.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.TaskName, StringComparer.CurrentCultureIgnoreCase)
            .Select(t =>
            {
                var project = $"{t.ProjectCode}-{t.ProjectName}";
                var task = ForecastCodes.TaskCode.IsMatch(t.ExternalId) ? $"{t.ExternalId}-{t.TaskName}" : $"-{t.TaskName}";
                return $"{project}\t{task}";
            }));

    internal static string CollapseSpaces(string text) => Whitespace().Replace(text, " ").Trim();

    private static bool IsHeader(string cell, string[] words) =>
        words.Contains(ForecastCodes.Normalize(cell), StringComparer.Ordinal);

    private static string Short(string text) => text.Length <= 60 ? text.Trim() : text.Trim()[..60] + "…";

    [GeneratedRegex(@"^(?<code>[A-Za-z]-?\d{1,7})\s*-\s*(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Applies a pasted task list to the local cache of Forecast tasks.</summary>
public sealed class ForecastTaskImporter(IDbContextFactory<TimeSheetDbContext> dbFactory, TimeProvider timeProvider)
{
    /// <summary>The active tasks as text, ready to be edited and applied again.</summary>
    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return ForecastTaskListImport.Format(await db.ForecastTasks.Where(t => t.IsActive).ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Adds or updates the listed tasks. With <paramref name="deactivateMissing"/>, cached tasks that are not in the
    /// list are deactivated (never deleted: rules and drafts reference them).
    /// </summary>
    public async Task<ForecastTaskImportResult> ImportAsync(
        string? text, bool deactivateMissing, CancellationToken cancellationToken = default)
    {
        var parsed = ForecastTaskListImport.Parse(text);
        if (parsed.Tasks.Count == 0)
            return new ForecastTaskImportResult(parsed.Warnings,
                new ForecastTaskSyncResult(0, 0, 0, 0, "Aucune tâche reconnue dans le texte collé : rien n'a été modifié."));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sync = await ForecastTaskSync.UpsertAsync(
            db, parsed.Tasks, timeProvider.GetUtcNow().UtcDateTime, deactivateMissing, cancellationToken);
        return new ForecastTaskImportResult(parsed.Warnings, sync);
    }

    /// <summary>
    /// Adds one task (or reactivates it when it already exists) so it can be assigned right away. A project name that
    /// is already cached reuses its code and client. Codes are optional ("P1078", "T35647").
    /// </summary>
    public async Task<ForecastTaskAddResult> AddAsync(
        string? projectName, string? taskName, string? projectCode = null, string? taskCode = null,
        CancellationToken cancellationToken = default)
    {
        projectName = ForecastTaskListImport.CollapseSpaces(projectName ?? "");
        taskName = ForecastTaskListImport.CollapseSpaces(taskName ?? "");
        if (projectName.Length == 0)
            return ForecastTaskAddResult.Fail("Indiquez le projet.");
        if (taskName.Length == 0)
            return ForecastTaskAddResult.Fail("Indiquez le nom de la tâche.");

        var normalizedProjectCode = NormalizeCode(projectCode, ForecastCodes.ProjectCode);
        var normalizedTaskCode = NormalizeCode(taskCode, ForecastCodes.TaskCode);
        if (normalizedProjectCode == InvalidCode)
            return ForecastTaskAddResult.Fail("Code de projet invalide : attendu « P1078 » (ou laissez vide).");
        if (normalizedTaskCode == InvalidCode)
            return ForecastTaskAddResult.Fail("Code de tâche invalide : attendu « T35647 » (ou laissez vide).");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sameProject = await db.ForecastTasks
            .Where(t => t.ProjectName == projectName)
            .OrderByDescending(t => t.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        var parsed = new ParsedForecastTask(
            normalizedTaskCode ?? $"{projectName}/{taskName}",
            sameProject?.ProjectName ?? projectName,
            normalizedProjectCode ?? sameProject?.ProjectCode,
            sameProject?.ClientName,
            taskName);
        await ForecastTaskSync.UpsertAsync(db, [parsed], timeProvider.GetUtcNow().UtcDateTime, deactivateMissing: false, cancellationToken);

        var id = await db.ForecastTasks.Where(t => t.ExternalId == parsed.ExternalId).Select(t => t.Id).FirstAsync(cancellationToken);
        return new ForecastTaskAddResult(id, null);
    }

    internal const string InvalidCode = "\0invalid";

    internal static string? NormalizeCode(string? code, System.Text.RegularExpressions.Regex pattern)
    {
        code = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
            return null;
        return pattern.Match(code) is { Success: true } m && m.Value == code ? code : InvalidCode;
    }
}

public sealed record ForecastTaskAddResult(int? TaskId, string? Error)
{
    public static ForecastTaskAddResult Fail(string error) => new(null, error);
}

public sealed record ForecastTaskImportResult(IReadOnlyList<string> Warnings, ForecastTaskSyncResult Sync)
{
    public string Summary => Sync.Summary;
}
