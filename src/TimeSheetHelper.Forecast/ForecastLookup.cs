using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace TimeSheetHelper.Forecast;

/// <summary>A Forecast project. <see cref="Id"/> is the company project id, the number of <see cref="Code"/> ("P1078").</summary>
public sealed record ForecastProjectMatch(int Id, string Code, string Name, string? ClientName, string? Status);

/// <summary>
/// A Forecast task with its parent project. The Forecast tools send no client for a task (only its project) and, today, no
/// description either: <see cref="Description"/> stays null unless the server starts sending one.
/// </summary>
public sealed record ForecastTaskMatch(
    string Code, string Name, string? Description, int? ProjectId, string? ProjectCode, string? ProjectName, string? ClientName)
{
    /// <summary>What describes the task: its description when there is one, else its name.</summary>
    public string Text => Description ?? Name;
}

/// <summary>A search: at most <see cref="ForecastLookup.MaxResults"/> items, best first; <see cref="Error"/> is a readable message when Forecast could not be asked.</summary>
public sealed record ForecastLookupResult<T>(IReadOnlyList<T> Items, int Total, string? Error = null)
{
    public static ForecastLookupResult<T> Empty { get; } = new([], 0);

    public static ForecastLookupResult<T> Failure(string error) => new([], 0, error);

    public bool Succeeded => Error is null;

    /// <summary>More matched than are listed: the user should type more.</summary>
    public bool HasMore => Total > Items.Count;
}

/// <summary>One lookup by number: no <see cref="Item"/> and no <see cref="Error"/> means Forecast has nothing with that number.</summary>
public sealed record ForecastGetResult<T>(T? Item, string? Error = null) where T : class
{
    public bool Found => Item is not null;
}

/// <summary>
/// What the "add a task" form can pre-fill from a project and/or task number. <see cref="Warnings"/> are things the user should
/// know (unknown number, task of another project); <see cref="Error"/> means Forecast could not be asked.
/// </summary>
public sealed record ForecastAutoFillResult(
    ForecastProjectMatch? Project, ForecastTaskMatch? Task, string? Description, IReadOnlyList<string> Warnings, string? Error = null)
{
    public static ForecastAutoFillResult Empty { get; } = new(null, null, null, []);

    public bool Succeeded => Error is null;
}

/// <summary>Looks up Forecast projects and tasks by number or by part of their name, for the "add a task" dialog.</summary>
public interface IForecastLookup
{
    /// <summary>Projects matching a P-number ("P1078", "1078") or a part of the name/client, best first. Under 2 characters: nothing.</summary>
    Task<ForecastLookupResult<ForecastProjectMatch>> SearchProjectsAsync(string? query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tasks matching a T-number ("T35647", "35647") or a part of the name, best first, optionally only those of the project
    /// with this number (the number of "P1078" is 1078). Under 2 characters: nothing.
    /// </summary>
    Task<ForecastLookupResult<ForecastTaskMatch>> SearchTasksAsync(string? query, int? projectId = null, CancellationToken cancellationToken = default);

    /// <summary>The project with exactly this number ("P1078" or "1078").</summary>
    Task<ForecastGetResult<ForecastProjectMatch>> GetProjectAsync(string? projectNumber, CancellationToken cancellationToken = default);

    /// <summary>The task with exactly this number ("T35647" or "35647"), whatever its project.</summary>
    Task<ForecastGetResult<ForecastTaskMatch>> GetTaskAsync(string? taskNumber, CancellationToken cancellationToken = default);

    /// <summary>What to pre-fill from the project and/or task number typed (see <see cref="ForecastLookup.AutoFillAsync"/> for the rules).</summary>
    Task<ForecastAutoFillResult> AutoFillAsync(string? projectNumber, string? taskNumber, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IForecastLookup"/>
/// <remarks>
/// Forecast's MCP tools used: "search_projects" (exact P-number or id → one project; text → the matches; no hit → "found": false) and
/// "find_task" (T-number or text, company-wide, optionally scoped by "project_id"). Neither does a ranked or limited search, so the
/// answers are ranked and cut here, and kept for <see cref="ForecastOptions.LookupCacheDuration"/> so typing does not call the
/// server again for what it just answered. Cancellation is not swallowed (the UI cancels the searches it no longer wants).
/// </remarks>
public sealed partial class ForecastLookup(
    ForecastApi api, IOptions<ForecastOptions> options, TimeProvider timeProvider, ILogger<ForecastLookup> logger) : IForecastLookup
{
    public const int MinQueryLength = 2;
    public const int MaxResults = 10;

    private const int MaxDescriptionLength = 200;

    private readonly ShortCache<IReadOnlyList<ForecastRemoteProject>> _projects = new(timeProvider, options.Value.LookupCacheDuration);
    private readonly ShortCache<IReadOnlyList<ForecastRemoteTask>> _tasks = new(timeProvider, options.Value.LookupCacheDuration);

    public async Task<ForecastLookupResult<ForecastProjectMatch>> SearchProjectsAsync(string? query, CancellationToken cancellationToken = default)
    {
        var text = Clean(query);
        if (text.Length < MinQueryLength)
            return ForecastLookupResult<ForecastProjectMatch>.Empty;

        try
        {
            await using var session = new LazySession(api);
            var remote = await RemoteProjectsAsync(session, text, cancellationToken);
            return RankProjects(remote.Select(ToProject).OfType<ForecastProjectMatch>(), text);
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return ForecastLookupResult<ForecastProjectMatch>.Failure(Describe(ex, "la recherche de projets"));
        }
    }

    public async Task<ForecastLookupResult<ForecastTaskMatch>> SearchTasksAsync(
        string? query, int? projectId = null, CancellationToken cancellationToken = default)
    {
        var text = Clean(query);
        if (text.Length < MinQueryLength)
            return ForecastLookupResult<ForecastTaskMatch>.Empty;

        try
        {
            await using var session = new LazySession(api);
            var remote = await RemoteTasksAsync(session, text, projectId, cancellationToken);
            var tasks = remote.Select(ToTask).Where(t => projectId is null || t.ProjectId == projectId);
            return RankTasks(tasks, text);
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return ForecastLookupResult<ForecastTaskMatch>.Failure(Describe(ex, "la recherche de tâches"));
        }
    }

    public async Task<ForecastGetResult<ForecastProjectMatch>> GetProjectAsync(string? projectNumber, CancellationToken cancellationToken = default)
    {
        if (!TryParseProjectNumber(Clean(projectNumber), out var code, out _))
            return new ForecastGetResult<ForecastProjectMatch>(null);

        try
        {
            await using var session = new LazySession(api);
            return new ForecastGetResult<ForecastProjectMatch>(await GetProjectCoreAsync(session, code, cancellationToken));
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return new ForecastGetResult<ForecastProjectMatch>(null, Describe(ex, "la lecture du projet"));
        }
    }

    public async Task<ForecastGetResult<ForecastTaskMatch>> GetTaskAsync(string? taskNumber, CancellationToken cancellationToken = default)
    {
        if (!TryParseTaskNumber(Clean(taskNumber), out var code, out _))
            return new ForecastGetResult<ForecastTaskMatch>(null);

        try
        {
            await using var session = new LazySession(api);
            return new ForecastGetResult<ForecastTaskMatch>(await GetTaskCoreAsync(session, code, cancellationToken));
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return new ForecastGetResult<ForecastTaskMatch>(null, Describe(ex, "la lecture de la tâche"));
        }
    }

    /// <summary>
    /// Rules:
    /// <list type="bullet">
    /// <item>Neither number typed: nothing to do (<see cref="ForecastAutoFillResult.Empty"/>).</item>
    /// <item>Project only: the project, and its name as description.</item>
    /// <item>Task only: the task and its parent project (the client is read from the project when Forecast gives it).</item>
    /// <item>Both: both, when the task belongs to the project. When it belongs to another one, only the project typed is kept, the task is
    /// dropped and a warning names the real project: nothing typed is silently overridden.</item>
    /// <item>A number Forecast does not know is a warning, not an error; the other one is still used.</item>
    /// </list>
    /// The description is the task's description when Forecast gives one, else its name, prefixed with the project name:
    /// "Project / Task" (the same convention as the identity of a task without code). Project only: the project name.
    /// </summary>
    public async Task<ForecastAutoFillResult> AutoFillAsync(string? projectNumber, string? taskNumber, CancellationToken cancellationToken = default)
    {
        var projectText = Clean(projectNumber);
        var taskText = Clean(taskNumber);
        if (projectText.Length == 0 && taskText.Length == 0)
            return ForecastAutoFillResult.Empty;

        var warnings = new List<string>();
        ForecastProjectMatch? project = null;
        ForecastTaskMatch? task = null;
        try
        {
            await using var session = new LazySession(api);
            if (projectText.Length > 0)
            {
                if (!TryParseProjectNumber(projectText, out var projectCode, out _))
                    warnings.Add($"« {projectText} » n'est pas un numéro de projet valide (attendu : P1078 ou 1078).");
                else if ((project = await GetProjectCoreAsync(session, projectCode, cancellationToken)) is null)
                    warnings.Add($"Le projet {projectCode} est introuvable dans Forecast.");
            }

            if (taskText.Length > 0)
            {
                if (!TryParseTaskNumber(taskText, out var taskCode, out _))
                    warnings.Add($"« {taskText} » n'est pas un numéro de tâche valide (attendu : T35647 ou 35647).");
                else if ((task = await GetTaskCoreAsync(session, taskCode, cancellationToken)) is null)
                    warnings.Add($"La tâche {taskCode} est introuvable dans Forecast.");
            }

            if (task is not null && project is not null && task.ProjectId != project.Id)
            {
                var actual = task.ProjectCode is null ? "aucun projet" : $"le projet {task.ProjectCode}{(task.ProjectName is null ? "" : $" ({task.ProjectName})")}";
                warnings.Add($"La tâche {task.Code} appartient à {actual}, pas au projet {project.Code} : elle n'a pas été reprise.");
                task = null;
            }
            else if (task is { ProjectId: { } parentId } && project is null)
            {
                project = await ParentProjectAsync(session, task, parentId, cancellationToken);
            }
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            return new ForecastAutoFillResult(null, null, null, warnings, Describe(ex, "le remplissage automatique"));
        }

        return new ForecastAutoFillResult(project, task, Compose(project, task), warnings);
    }

    internal static string? Compose(ForecastProjectMatch? project, ForecastTaskMatch? task)
    {
        if (task is null)
            return project?.Name;
        return project is null ? task.Text : $"{project.Name} / {task.Text}";
    }

    private async Task<ForecastProjectMatch?> ParentProjectAsync(
        LazySession session, ForecastTaskMatch task, int projectId, CancellationToken cancellationToken)
    {
        var code = $"P{projectId}";
        try
        {
            if (await GetProjectCoreAsync(session, code, cancellationToken) is { } found)
                return found;
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            logger.LogInformation("Projet {Code} non relu pour compléter la tâche : {Reason}", code, ex.Message);
        }

        // The task already names its project: enough to pre-fill, only the client is missing.
        return string.IsNullOrWhiteSpace(task.ProjectName) ? null : new ForecastProjectMatch(projectId, code, task.ProjectName, task.ClientName, null);
    }

    private async Task<ForecastProjectMatch?> GetProjectCoreAsync(LazySession session, string code, CancellationToken cancellationToken)
    {
        var remote = await RemoteProjectsAsync(session, code, cancellationToken);
        return remote.Select(ToProject).OfType<ForecastProjectMatch>().FirstOrDefault(p => p.Code == code);
    }

    private async Task<ForecastTaskMatch?> GetTaskCoreAsync(LazySession session, string code, CancellationToken cancellationToken)
    {
        var remote = await RemoteTasksAsync(session, code, null, cancellationToken);
        return remote.Select(ToTask).FirstOrDefault(t => t.Code == code);
    }

    private async Task<IReadOnlyList<ForecastRemoteProject>> RemoteProjectsAsync(LazySession session, string query, CancellationToken cancellationToken)
    {
        var key = ForecastCodes.Normalize(query);
        if (_projects.TryGet(key, out var cached))
            return cached;

        var found = await (await session.GetAsync(cancellationToken)).SearchProjectsAsync(query, cancellationToken);
        _projects.Set(key, found);
        return found;
    }

    private async Task<IReadOnlyList<ForecastRemoteTask>> RemoteTasksAsync(
        LazySession session, string query, int? projectId, CancellationToken cancellationToken)
    {
        var key = $"{projectId}|{ForecastCodes.Normalize(query)}";
        if (_tasks.TryGet(key, out var cached))
            return cached;

        var found = await (await session.GetAsync(cancellationToken)).FindTasksAsync(query, projectId, cancellationToken);
        _tasks.Set(key, found);
        return found;
    }

    internal static ForecastLookupResult<ForecastProjectMatch> RankProjects(IEnumerable<ForecastProjectMatch> projects, string query)
    {
        var isCode = TryParseProjectNumber(query, out var code, out var explicitCode);
        var text = ForecastCodes.Normalize(query);

        var ranked = projects
            .DistinctBy(p => p.Id)
            .Select(p => (Item: p, Tier: isCode && p.Code == code ? 0 : TextTier(text, p.Name, p.ClientName)))
            .OrderBy(x => x.Tier)
            .ThenBy(x => ForecastCodes.Normalize(x.Item.Name), StringComparer.Ordinal)
            .ToList();

        // A prefixed code names one project: what else the server suggested is noise.
        if (explicitCode && ranked.Any(x => x.Tier == 0))
            ranked = ranked.Where(x => x.Tier == 0).ToList();
        return Cut(ranked.Select(x => x.Item), ranked.Count);
    }

    private static ForecastLookupResult<ForecastTaskMatch> RankTasks(IEnumerable<ForecastTaskMatch> tasks, string query)
    {
        var isCode = TryParseTaskNumber(query, out var code, out var explicitCode);
        var text = ForecastCodes.Normalize(query);

        var ranked = tasks
            .DistinctBy(t => t.Code)
            .Select(t => (Item: t, Tier: isCode && t.Code == code ? 0 : TextTier(text, t.Name, t.ProjectName)))
            .OrderBy(x => x.Tier)
            .ThenBy(x => ForecastCodes.Normalize(x.Item.Name), StringComparer.Ordinal)
            .ToList();

        if (explicitCode && ranked.Any(x => x.Tier == 0))
            ranked = ranked.Where(x => x.Tier == 0).ToList();
        return Cut(ranked.Select(x => x.Item), ranked.Count);
    }

    private static ForecastLookupResult<T> Cut<T>(IEnumerable<T> ranked, int total) =>
        new(ranked.Take(MaxResults).ToList(), total);

    /// <summary>1 = same name, 2 = name starts with the text, 3 = name contains it, 4 = the other field (client/project) does, 5 = none.</summary>
    private static int TextTier(string text, string name, string? other)
    {
        var normalizedName = ForecastCodes.Normalize(name);
        if (normalizedName == text)
            return 1;
        if (normalizedName.StartsWith(text, StringComparison.Ordinal))
            return 2;
        if (normalizedName.Contains(text, StringComparison.Ordinal))
            return 3;
        return other is not null && ForecastCodes.Normalize(other).Contains(text, StringComparison.Ordinal) ? 4 : 5;
    }

    private static ForecastProjectMatch? ToProject(ForecastRemoteProject remote)
    {
        if (!int.TryParse(remote.Id, out var id))
            return null;
        return new ForecastProjectMatch(id, ProjectCodeOf(remote.Code) ?? $"P{id}", remote.Name, Blank(remote.ClientName), Blank(remote.Status));
    }

    private static ForecastTaskMatch ToTask(ForecastRemoteTask remote)
    {
        var projectId = int.TryParse(remote.ProjectId, out var id) ? id : (int?)null;
        var description = OneLine(remote.Description);
        return new ForecastTaskMatch(
            remote.Code ?? $"T{remote.Id}",
            remote.Title,
            description is not null && ForecastCodes.Normalize(description) != ForecastCodes.Normalize(remote.Title) ? description : null,
            projectId,
            projectId is { } p ? $"P{p}" : ProjectCodeOf(remote.ProjectCode),
            Blank(remote.ProjectName),
            Blank(remote.ClientName));
    }

    private static string? ProjectCodeOf(string? code) =>
        code is not null && ForecastCodes.ProjectCode.Match(code) is { Success: true } m ? m.Value.Replace("-", "").ToUpperInvariant() : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? OneLine(string? text)
    {
        var line = Blank(text is null ? null : Whitespace().Replace(text, " "));
        return line is { Length: > MaxDescriptionLength } ? line[..MaxDescriptionLength] + "…" : line;
    }

    private static string Clean(string? text) => Whitespace().Replace(text ?? "", " ").Trim();

    /// <summary>"P1078", "p-1078" or "1078" → "P1078".</summary>
    internal static bool TryParseProjectNumber(string text, out string code, out bool explicitCode)
    {
        code = "";
        explicitCode = false;
        if (ProjectNumber().Match(text) is not { Success: true } m)
            return false;
        code = "P" + m.Groups["n"].Value;
        explicitCode = m.Groups["p"].Success;
        return true;
    }

    /// <summary>"T35647", "t35647" or "35647" → "T35647".</summary>
    internal static bool TryParseTaskNumber(string text, out string code, out bool explicitCode)
    {
        code = "";
        explicitCode = false;
        if (TaskNumber().Match(text) is not { Success: true } m)
            return false;
        code = "T" + m.Groups["n"].Value;
        explicitCode = m.Groups["t"].Success;
        return true;
    }

    /// <summary>What can go wrong when Forecast is asked: not signed in, tool missing or failing, network, timeout. A cancelled search is not one.</summary>
    private static bool IsFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        ForecastAuthRequiredException or ForecastToolException or TimeoutException or HttpRequestException or McpException or IOException => true,
        _ => false,
    };

    private string Describe(Exception ex, string action)
    {
        logger.LogWarning("Forecast : {Action} impossible ({Type} : {Reason}).", action, ex.GetType().Name, ex.Message);
        return ex switch
        {
            ForecastAuthRequiredException or ForecastToolException => ex.Message,
            OperationCanceledException or TimeoutException => "Forecast n'a pas répondu à temps. Réessayez dans un instant.",
            _ => "Forecast est injoignable (réseau ou service indisponible). Réessayez dans un instant.",
        };
    }

    [GeneratedRegex(@"^(?<p>[Pp]-?)?(?<n>\d{1,6})$")]
    private static partial Regex ProjectNumber();

    [GeneratedRegex(@"^(?<t>[Tt])?(?<n>\d{1,7})$")]
    private static partial Regex TaskNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The MCP session, opened only when a search is not answered by the cache.</summary>
    private sealed class LazySession(ForecastApi api) : IAsyncDisposable
    {
        private ForecastMcpSession? _session;

        public async Task<ForecastMcpSession> GetAsync(CancellationToken cancellationToken) =>
            _session ??= await api.OpenAsync(cancellationToken);

        public ValueTask DisposeAsync() => _session?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ShortCache<T>(TimeProvider timeProvider, TimeSpan lifetime)
    {
        private const int Capacity = 200;

        private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, T Value)> _items = new();

        public bool TryGet(string key, out T value)
        {
            if (lifetime > TimeSpan.Zero && _items.TryGetValue(key, out var entry) && entry.Expires > timeProvider.GetUtcNow())
            {
                value = entry.Value;
                return true;
            }

            value = default!;
            return false;
        }

        public void Set(string key, T value)
        {
            if (lifetime <= TimeSpan.Zero)
                return;

            var now = timeProvider.GetUtcNow();
            if (_items.Count >= Capacity)
            {
                foreach (var stale in _items.Where(i => i.Value.Expires <= now).Select(i => i.Key).ToList())
                    _items.TryRemove(stale, out _);
                if (_items.Count >= Capacity)
                    _items.Clear();
            }

            _items[key] = (now + lifetime, value);
        }
    }
}
