using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace TimeSheetHelper.Forecast;

/// <summary>A tool of the server is missing, cannot be filled from what we know, or answered with an error.</summary>
public class ForecastToolException(string message) : Exception(message);

/// <summary>The server refused the request because of its rate limit (429); it is retried before this surfaces.</summary>
public sealed class ForecastThrottledException(string message) : ForecastToolException(message);

public enum ForecastWriteOutcome
{
    /// <summary>The server declined the write: nothing was registered.</summary>
    Refused,

    /// <summary>The write failed: nothing was registered.</summary>
    Failed,

    /// <summary>The server cannot say whether the write was applied: the week must be read again before concluding.</summary>
    Unknown,
}

/// <summary>A registration was not made, or may not have been: see <see cref="Outcome"/>.</summary>
public sealed class ForecastWriteException(string message, ForecastWriteOutcome outcome, bool transportFailure = false) : ForecastToolException(message)
{
    public ForecastWriteOutcome Outcome { get; } = outcome;

    /// <summary>The outcome is unknown because the request itself failed (timeout, network): the following registrations should not be tried.</summary>
    public bool IsTransportFailure { get; } = transportFailure;
}

/// <summary>What a registration created: the warnings of the plan step (the server stages a write before applying it).</summary>
public sealed record ForecastCreateResult(IReadOnlyList<string> Warnings);

/// <summary>The registrations of a period; <paramref name="IsComplete"/> is false when the server still held more than it listed.</summary>
public sealed record ForecastEntryRead(IReadOnlyList<ForecastRemoteEntry> Entries, bool IsComplete, string? Detail);

/// <summary>What can go wrong when Forecast is asked (network, timeout, tool failure); a cancellation asked by the caller is not one.</summary>
public static class ForecastErrors
{
    public static bool IsRecoverable(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        ForecastAuthRequiredException => false,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        ForecastToolException or TimeoutException or HttpRequestException or McpException or IOException => true,
        _ => false,
    };
}

/// <summary>Opens <see cref="ForecastMcpSession"/>s on the Forecast MCP server.</summary>
public sealed class ForecastApi(IForecastConnector connector, IOptions<ForecastOptions> options, ILogger<ForecastApi> logger)
{
    public async Task<ForecastMcpSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var client = await connector.ConnectAsync(interactive: false, cancellationToken: cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken);
            logger.LogInformation("Forecast MCP : {Count} outil(s) disponible(s).", tools.Count);
            return new ForecastMcpSession(client, tools, options.Value, logger);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}

/// <summary>The few things the application does with Forecast, expressed on top of the tools of the MCP server.</summary>
public sealed partial class ForecastMcpSession(
    IForecastToolClient client, IReadOnlyList<ForecastToolInfo> tools, ForecastOptions options, ILogger logger) : IAsyncDisposable
{
    public const string ListProjectTasksTool = "list_project_tasks";
    public const string SearchProjectsTool = "search_projects";

    private string? _personId;
    private bool _personResolved;

    public IReadOnlyList<ForecastToolInfo> Tools => tools;

    /// <summary>The read-only checks before a fill are on (<see cref="ForecastOptions.PreflightChecks"/>).</summary>
    public bool PreflightEnabled => options.PreflightChecks;

    public bool HasTool(string name) => tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public ForecastToolInfo Require(ForecastToolRole role)
    {
        if (ForecastToolResolver.Resolve(tools, role, options.Tools) is { } tool)
            return tool;

        var configKey = role switch
        {
            ForecastToolRole.ListTasks => "ListTasks",
            ForecastToolRole.ListTimeEntries => "ListTimeEntries",
            ForecastToolRole.CreateTimeEntry => "CreateTimeEntry",
            _ => "CurrentUser",
        };
        throw new ForecastToolException(
            $"Aucun outil du serveur Forecast MCP ne correspond à « {role} ». Lancez « TimeSheetHelper forecast tools » pour voir ceux qu'il offre, "
            + $"puis indiquez le bon dans la configuration (Forecast:Tools:{configKey}).");
    }

    public async Task<IReadOnlyList<ForecastRemoteTask>> ListTasksAsync(CancellationToken cancellationToken = default)
    {
        var tool = Require(ForecastToolRole.ListTasks);
        var args = await BuildAsync(tool, new ForecastArgumentValues(), cancellationToken);
        var result = await CallAsync(tool, args, cancellationToken);
        var tasks = result.Json is { } json ? ForecastJson.ParseTasks(json) : [];
        if (tasks.Count == 0)
            logger.LogWarning("« {Tool} » n'a renvoyé aucune tâche lisible : {Text}", tool.Name, Truncate(result.Text));
        if (result.Json is { ValueKind: JsonValueKind.Object } root && ForecastJson.Get(root, "truncated") is { ValueKind: JsonValueKind.True })
            logger.LogWarning("« {Tool} » a plafonné sa liste à {Count} tâche(s) : les autres sont à ajouter à la main (page Tâches).", tool.Name, tasks.Count);
        return tasks;
    }

    /// <summary>Searches every task of the company (any assignee) by name, or by its T-number, with "find_task".</summary>
    public Task<IReadOnlyList<ForecastRemoteTask>> FindTasksAsync(string query, CancellationToken cancellationToken = default) =>
        FindTasksAsync(query, null, cancellationToken);

    /// <summary>Same, restricted to the project with this company id (the number of "P1078") when the tool accepts it.</summary>
    public async Task<IReadOnlyList<ForecastRemoteTask>> FindTasksAsync(string query, int? projectId, CancellationToken cancellationToken)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, "find_task", StringComparison.OrdinalIgnoreCase))
            ?? throw new ForecastToolException("Le serveur Forecast n'offre pas l'outil « find_task » : la recherche de tâches est impossible.");
        var arguments = new Dictionary<string, object?> { ["name"] = query };
        if (projectId is { } project && tool.ParameterNames.Contains("project_id", StringComparer.OrdinalIgnoreCase))
            arguments["project_id"] = project;
        var result = await CallAsync(tool, arguments, cancellationToken);
        return result.Json is { } json ? ForecastJson.ParseTasks(json) : [];
    }

    /// <summary>Searches the projects by P-number or by name with "search_projects" (an exact hit is one project, otherwise the matches).</summary>
    public async Task<IReadOnlyList<ForecastRemoteProject>> SearchProjectsAsync(string query, CancellationToken cancellationToken = default)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, SearchProjectsTool, StringComparison.OrdinalIgnoreCase))
            ?? throw new ForecastToolException("Le serveur Forecast n'offre pas l'outil « search_projects » : la recherche de projets est impossible.");
        var result = await CallAsync(tool, new Dictionary<string, object?> { ["name"] = query }, cancellationToken);
        return result.Json is { } json ? ForecastJson.ParseProjects(json) : [];
    }

    /// <summary>
    /// State of every task of a project ("list_project_tasks": completed, blocked, estimate and hours registered), read without
    /// changing anything. <paramref name="projectId"/> is the company project id (the number of "P1078").
    /// </summary>
    public async Task<IReadOnlyList<ForecastRemoteTaskState>> ListProjectTaskStatesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, ListProjectTasksTool, StringComparison.OrdinalIgnoreCase))
            ?? throw new ForecastToolException("Le serveur Forecast n'offre pas l'outil « list_project_tasks » : l'état des tâches du projet est non vérifiable.");
        object id = long.TryParse(projectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : projectId;
        var result = await CallAsync(tool, new Dictionary<string, object?> { ["project_id"] = id }, cancellationToken);
        return result.Json is { } json ? ForecastJson.ParseTaskStates(json) : [];
    }

    /// <summary>Time registrations of the signed-in person between the two dates (inclusive).</summary>
    public async Task<IReadOnlyList<ForecastRemoteEntry>> ListTimeEntriesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        (await ReadTimeEntriesAsync(from, to, cancellationToken)).Entries;

    /// <summary>
    /// Same as <see cref="ListTimeEntriesAsync"/>, and says whether the list is whole. The server caps its answer and flags it with
    /// "has_more": a capped period is read again day by day, and a day that is still capped is reported as incomplete.
    /// </summary>
    public async Task<ForecastEntryRead> ReadTimeEntriesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var tool = Require(ForecastToolRole.ListTimeEntries);
        var entries = new List<ForecastRemoteEntry>();
        string? detail = null;
        var complete = true;

        if (ForecastArgumentBuilder.AcceptsDateRange(tool))
        {
            await ReadRangeAsync(from, to);
        }
        else
        {
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                var args = await BuildAsync(tool, new ForecastArgumentValues { Date = day, MaxResults = options.TimeEntriesMaxResults }, cancellationToken);
                entries.AddRange(Parse(await CallAsync(tool, args, cancellationToken)));
            }
        }

        var personId = await GetPersonIdAsync(cancellationToken);
        var kept = entries
            .Where(e => e.Date >= from && e.Date <= to)
            .Where(e => personId is null || e.PersonId is null || e.PersonId == personId)
            .ToList();
        return new ForecastEntryRead(kept, complete, detail);

        async Task ReadRangeAsync(DateOnly start, DateOnly end)
        {
            var args = await BuildAsync(tool, new ForecastArgumentValues { From = start, To = end, MaxResults = options.TimeEntriesMaxResults }, cancellationToken);
            var result = await CallAsync(tool, args, cancellationToken);
            var parsed = Parse(result).ToList();
            var (hasMore, count, note) = result.Json is { } json ? ForecastJson.ParseCompleteness(json) : (false, null, null);

            if (!hasMore && (count is not { } total || total <= parsed.Count))
            {
                entries.AddRange(parsed);
                return;
            }

            if (start < end)
            {
                for (var day = start; day <= end; day = day.AddDays(1))
                    await ReadRangeAsync(day, day);
                return;
            }

            entries.AddRange(parsed);
            complete = false;
            detail = note ?? $"{parsed.Count} saisie(s) lue(s) pour le {start:yyyy-MM-dd}, mais Forecast en contient d'autres.";
        }

        static IEnumerable<ForecastRemoteEntry> Parse(ForecastToolResult result) =>
            result.Json is { } json ? ForecastJson.ParseTimeEntries(json) : [];
    }

    /// <exception cref="ForecastWriteException">The registration was refused, failed, or its outcome is unknown (see <see cref="ForecastWriteOutcome"/>).</exception>
    public async Task<ForecastCreateResult> CreateTimeEntryAsync(string remoteTaskId, DateOnly date, decimal hours, string? notes, CancellationToken cancellationToken = default)
    {
        var tool = Require(ForecastToolRole.CreateTimeEntry);
        if (tool.Name.StartsWith("plan_", StringComparison.OrdinalIgnoreCase))
            return await PlanAndConfirmAsync(tool, remoteTaskId, date, hours, notes, cancellationToken);

        var values = new ForecastArgumentValues { TaskId = remoteTaskId, Date = date, Hours = hours, Notes = notes };
        var args = await BuildAsync(tool, values, cancellationToken);
        await CallAsync(tool, args.Values, cancellationToken, retryTransient: false);
        return new ForecastCreateResult([]);
    }

    /// <summary>Forecast stages a write ("plan_…") and applies it only when the matching "confirm_…" is called with its action id.</summary>
    private async Task<ForecastCreateResult> PlanAndConfirmAsync(
        ForecastToolInfo plan, string remoteTaskId, DateOnly date, decimal hours, string? notes, CancellationToken cancellationToken)
    {
        var confirmName = "confirm_" + plan.Name["plan_".Length..];
        if (tools.FirstOrDefault(t => string.Equals(t.Name, confirmName, StringComparison.OrdinalIgnoreCase)) is not { } confirm)
            throw new ForecastWriteException($"Le serveur Forecast offre « {plan.Name} » mais pas « {confirmName} » : rien n'a été enregistré.", ForecastWriteOutcome.Failed);

        // No person: the registration is made for the signed-in one.
        var entry = new Dictionary<string, object?>
        {
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["minutes"] = (int)Math.Round(hours * 60m),
        };
        entry["task_id"] = long.TryParse(remoteTaskId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskId) ? taskId : remoteTaskId;
        if (!string.IsNullOrWhiteSpace(notes))
            entry["notes"] = notes;

        ForecastToolResult staged;
        try
        {
            staged = await CallAsync(
                plan, new Dictionary<string, object?> { ["entries"] = JsonSerializer.SerializeToElement(new[] { entry }) }, cancellationToken);
        }
        catch (ForecastToolException ex)
        {
            throw new ForecastWriteException(ex.Message, ForecastWriteOutcome.Failed);
        }

        // Staging changes nothing: whatever the server says, nothing is registered yet.
        var planOutcome = ForecastJson.ParseOutcome(staged.Json);
        if (!planOutcome.Ok)
        {
            var refused = planOutcome.Outcome == "refused";
            throw new ForecastWriteException(
                $"« {plan.Name} » {(refused ? "a refusé l'enregistrement" : "a échoué")}{(planOutcome.Message is null ? "." : " : " + planOutcome.Message)}",
                refused ? ForecastWriteOutcome.Refused : ForecastWriteOutcome.Failed);
        }

        var actionId = staged.Json is { } json ? ForecastJson.Text(ForecastJson.Get(json, "actionid")) : null;
        if (actionId is null)
            throw new ForecastWriteException($"« {plan.Name} » n'a pas renvoyé d'action à confirmer : {Truncate(staged.Text)}", ForecastWriteOutcome.Failed);

        var warnings = ForecastJson.ParseWarnings(staged.Json);
        if (warnings.Count > 0)
            logger.LogWarning("« {Tool} » signale : {Warnings}", plan.Name, string.Join(" | ", warnings.Select(Truncate)));

        ForecastToolResult confirmed;
        try
        {
            // A confirmation is never retried after a timeout or a server error: it may have been applied.
            confirmed = await CallAsync(confirm, new Dictionary<string, object?> { ["action_id"] = actionId }, cancellationToken, retryTransient: false);
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or McpException or IOException)
        {
            throw new ForecastWriteException(
                $"« {confirm.Name} » n'a pas répondu ({ex.Message}) : l'enregistrement a peut-être été appliqué.", ForecastWriteOutcome.Unknown, transportFailure: true);
        }
        catch (ForecastToolException ex)
        {
            throw new ForecastWriteException(ex.Message, ForecastWriteOutcome.Failed);
        }

        var outcome = ForecastJson.ParseOutcome(confirmed.Json);
        if (!outcome.Ok)
        {
            var kind = outcome.Outcome switch
            {
                "refused" => ForecastWriteOutcome.Refused,
                "unknown" => ForecastWriteOutcome.Unknown,
                _ => ForecastWriteOutcome.Failed,
            };
            var verb = kind switch
            {
                ForecastWriteOutcome.Refused => "a refusé l'enregistrement",
                ForecastWriteOutcome.Unknown => "ne sait pas si l'enregistrement a été appliqué",
                _ => "a échoué",
            };
            throw new ForecastWriteException($"« {confirm.Name} » {verb}{(outcome.Message is null ? "." : " : " + outcome.Message)}", kind);
        }

        return new ForecastCreateResult(warnings);
    }

    /// <summary>Calls any tool by name (diagnostics: "forecast call").</summary>
    public Task<ForecastToolResult> CallRawAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        client.CallToolAsync(toolName, arguments, cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();

    private Task<ForecastArguments> BuildAsync(ForecastToolInfo tool, ForecastArgumentValues values, CancellationToken cancellationToken)
    {
        var withoutPerson = ForecastArgumentBuilder.Build(tool, values);
        var withPerson = ForecastArgumentBuilder.Build(tool, values with { PersonId = "0" });
        if (withPerson.Values.Count == withoutPerson.Values.Count)
            return Task.FromResult(Checked(tool, withoutPerson));

        // The tool takes a person: the configured one, else the signed-in one ("me" is understood by the Forecast tools).
        return Task.FromResult(Checked(tool, ForecastArgumentBuilder.Build(tool, values with { PersonId = options.PersonId ?? "me" })));
    }

    private static ForecastArguments Checked(ForecastToolInfo tool, ForecastArguments arguments) =>
        arguments.IsComplete
            ? arguments
            : throw new ForecastToolException(
                $"L'outil « {tool.Name} » exige le(s) paramètre(s) {string.Join(", ", arguments.MissingRequired.Select(m => $"« {m} »"))} "
                + "que je ne sais pas renseigner (Forecast:PersonId peut aider pour une personne).");

    private async Task<string?> GetPersonIdAsync(CancellationToken cancellationToken)
    {
        if (_personResolved)
            return _personId;

        _personId = options.PersonId;
        if (_personId is null && ForecastToolResolver.Resolve(tools, ForecastToolRole.CurrentUser, options.Tools) is { } tool)
        {
            try
            {
                var result = await client.CallToolAsync(tool.Name, new Dictionary<string, object?>(), cancellationToken);
                if (!result.IsError && result.Json is { } json)
                    _personId = ForecastJson.ParsePersonId(json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Impossible de lire l'utilisateur courant avec « {Tool} ».", tool.Name);
            }
        }

        _personResolved = true;
        return _personId;
    }

    private Task<ForecastToolResult> CallAsync(ForecastToolInfo tool, ForecastArguments arguments, CancellationToken cancellationToken) =>
        CallAsync(tool, arguments.Values, cancellationToken);

    /// <summary>
    /// Calls a tool and turns an error answer into <see cref="ForecastToolException"/> and a timeout into <see cref="TimeoutException"/>
    /// (the client cancels a slow request itself: that is not a cancellation by the caller). A request refused with 429 is retried
    /// with a growing delay; with <paramref name="retryTransient"/> a 502/503/504 is too (not for a confirmation, which may have been applied).
    /// </summary>
    private async Task<ForecastToolResult> CallAsync(
        ForecastToolInfo tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken, bool retryTransient = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                ForecastToolResult result;
                try
                {
                    result = await client.CallToolAsync(tool.Name, arguments, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"« {tool.Name} » n'a pas répondu dans le délai de {options.RequestTimeout.TotalSeconds:0} s.");
                }

                if (result.IsError)
                {
                    var message = $"« {tool.Name} » a répondu par une erreur : {Truncate(result.Text)}";
                    throw ThrottleText().IsMatch(result.Text) ? new ForecastThrottledException(message) : new ForecastToolException(message);
                }
                return result;
            }
            catch (Exception ex) when (attempt < options.RetryCount && IsRetryable(ex, retryTransient))
            {
                var delay = TimeSpan.FromTicks(Math.Min(options.RetryBaseDelay.Ticks * (1L << attempt), TimeSpan.FromSeconds(30).Ticks));
                logger.LogWarning("« {Tool} » est limité par Forecast ({Reason}) : nouvel essai {Attempt}/{Max} dans {Delay:0.#} s.",
                    tool.Name, ex.Message, attempt + 1, options.RetryCount, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception ex, bool retryTransient) => ex switch
    {
        ForecastThrottledException => true,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => true,
        HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => retryTransient,
        _ => false,
    };

    [GeneratedRegex(@"\b429\b|too many requests|rate[ -]?limit|trop de requêtes", RegexOptions.IgnoreCase)]
    private static partial Regex ThrottleText();

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}

