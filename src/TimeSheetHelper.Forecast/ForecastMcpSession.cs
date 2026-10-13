using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TimeSheetHelper.Forecast;

/// <summary>A tool of the server is missing, cannot be filled from what we know, or answered with an error.</summary>
public sealed class ForecastToolException(string message) : Exception(message);

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
public sealed class ForecastMcpSession(
    IForecastToolClient client, IReadOnlyList<ForecastToolInfo> tools, ForecastOptions options, ILogger logger) : IAsyncDisposable
{
    private string? _personId;
    private bool _personResolved;

    public IReadOnlyList<ForecastToolInfo> Tools => tools;

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
    public async Task<IReadOnlyList<ForecastRemoteTask>> FindTasksAsync(string query, CancellationToken cancellationToken = default)
    {
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, "find_task", StringComparison.OrdinalIgnoreCase))
            ?? throw new ForecastToolException("Le serveur Forecast n'offre pas l'outil « find_task » : la recherche de tâches est impossible.");
        var result = await CallAsync(tool, new Dictionary<string, object?> { ["name"] = query }, cancellationToken);
        return result.Json is { } json ? ForecastJson.ParseTasks(json) : [];
    }

    /// <summary>Time registrations of the signed-in person between the two dates (inclusive).</summary>
    public async Task<IReadOnlyList<ForecastRemoteEntry>> ListTimeEntriesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var tool = Require(ForecastToolRole.ListTimeEntries);
        var entries = new List<ForecastRemoteEntry>();

        if (ForecastArgumentBuilder.AcceptsDateRange(tool))
        {
            var args = await BuildAsync(tool, new ForecastArgumentValues { From = from, To = to }, cancellationToken);
            entries.AddRange(Parse(await CallAsync(tool, args, cancellationToken)));
        }
        else
        {
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                var args = await BuildAsync(tool, new ForecastArgumentValues { Date = day }, cancellationToken);
                entries.AddRange(Parse(await CallAsync(tool, args, cancellationToken)));
            }
        }

        var personId = await GetPersonIdAsync(cancellationToken);
        return entries
            .Where(e => e.Date >= from && e.Date <= to)
            .Where(e => personId is null || e.PersonId is null || e.PersonId == personId)
            .ToList();

        IEnumerable<ForecastRemoteEntry> Parse(ForecastToolResult result) =>
            result.Json is { } json ? ForecastJson.ParseTimeEntries(json) : [];
    }

    public async Task CreateTimeEntryAsync(string remoteTaskId, DateOnly date, decimal hours, string? notes, CancellationToken cancellationToken = default)
    {
        var tool = Require(ForecastToolRole.CreateTimeEntry);
        if (tool.Name.StartsWith("plan_", StringComparison.OrdinalIgnoreCase))
        {
            await PlanAndConfirmAsync(tool, remoteTaskId, date, hours, notes, cancellationToken);
            return;
        }

        var values = new ForecastArgumentValues { TaskId = remoteTaskId, Date = date, Hours = hours, Notes = notes };
        var args = await BuildAsync(tool, values, cancellationToken);
        await CallAsync(tool, args, cancellationToken);
    }

    /// <summary>Forecast stages a write ("plan_…") and applies it only when the matching "confirm_…" is called with its action id.</summary>
    private async Task PlanAndConfirmAsync(
        ForecastToolInfo plan, string remoteTaskId, DateOnly date, decimal hours, string? notes, CancellationToken cancellationToken)
    {
        var confirmName = "confirm_" + plan.Name["plan_".Length..];
        if (tools.FirstOrDefault(t => string.Equals(t.Name, confirmName, StringComparison.OrdinalIgnoreCase)) is not { } confirm)
            throw new ForecastToolException($"Le serveur Forecast offre « {plan.Name} » mais pas « {confirmName} » : rien n'a été enregistré.");

        // No person: the registration is made for the signed-in one.
        var entry = new Dictionary<string, object?>
        {
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["minutes"] = (int)Math.Round(hours * 60m),
        };
        entry["task_id"] = long.TryParse(remoteTaskId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var taskId) ? taskId : remoteTaskId;
        if (!string.IsNullOrWhiteSpace(notes))
            entry["notes"] = notes;

        var staged = await CallAsync(
            plan, new Dictionary<string, object?> { ["entries"] = JsonSerializer.SerializeToElement(new[] { entry }) }, cancellationToken);
        var actionId = staged.Json is { } json ? ForecastJson.Text(ForecastJson.Get(json, "actionid")) : null;
        if (actionId is null)
            throw new ForecastToolException($"« {plan.Name} » n'a pas renvoyé d'action à confirmer : {Truncate(staged.Text)}");
        if (staged.Json is { } planned && ForecastJson.Get(planned, "warnings") is { ValueKind: JsonValueKind.Array } warnings && warnings.GetArrayLength() > 0)
            logger.LogWarning("« {Tool} » signale : {Warnings}", plan.Name, Truncate(warnings.GetRawText()));

        await CallAsync(confirm, new Dictionary<string, object?> { ["action_id"] = actionId }, cancellationToken);
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

    private async Task<ForecastToolResult> CallAsync(ForecastToolInfo tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(tool.Name, arguments, cancellationToken);
        if (result.IsError)
            throw new ForecastToolException($"« {tool.Name} » a répondu par une erreur : {Truncate(result.Text)}");
        return result;
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
