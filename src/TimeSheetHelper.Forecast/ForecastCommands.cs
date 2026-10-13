using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Entry points for the CLI ("forecast login|logout|tools|call|sync-tasks").
/// Each method returns a process exit code and writes French, user-facing lines to <c>output</c> (console by default).
/// </summary>
public sealed class ForecastCommands(
    IForecastConnector connector,
    ForecastApi api,
    ForecastTaskReader taskReader,
    IOptions<ForecastOptions> options,
    ILogger<ForecastCommands> logger)
{
    /// <summary>"forecast login": signs in to the Forecast MCP server with the system browser.</summary>
    public Task<int> LoginAsync(TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            await o.WriteLineAsync($"Connexion au serveur MCP de Forecast ({options.Value.ResolveEndpoint()})…");
            await using var client = await connector.ConnectAsync(interactive: true, line => o.WriteLine(line), cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken);
            await o.WriteLineAsync($"Connecté. Le serveur offre {tools.Count} outil(s) (« TimeSheetHelper forecast tools » pour les voir).");
        });

    /// <summary>"forecast logout": forgets the stored sign-in.</summary>
    public Task<int> LogoutAsync(TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            await connector.SignOutAsync(cancellationToken);
            await o.WriteLineAsync("Connexion à Forecast oubliée.");
        });

    /// <summary>"forecast tools": lists the tools of the server and the ones the application picked for each job.</summary>
    public Task<int> ToolsAsync(TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            await using var session = await api.OpenAsync(cancellationToken);
            await o.WriteLineAsync($"{session.Tools.Count} outil(s) :");
            foreach (var tool in session.Tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var parameters = string.Join(", ", tool.ParameterNames);
                await o.WriteLineAsync($"  {tool.Name}({parameters})");
                if (!string.IsNullOrWhiteSpace(tool.Description))
                    await o.WriteLineAsync($"      {tool.Description.ReplaceLineEndings(" ").Trim()}");
            }

            await o.WriteLineAsync();
            await o.WriteLineAsync("Outils retenus (à forcer avec Forecast:Tools:* s'ils sont faux) :");
            foreach (var role in Enum.GetValues<ForecastToolRole>())
            {
                var tool = ForecastToolResolver.Resolve(session.Tools, role, options.Value.Tools);
                await o.WriteLineAsync($"  {Label(role),-28} {tool?.Name ?? "(aucun)"}");
            }
        });

    /// <summary>"forecast call &lt;tool&gt; [json]": calls one tool and prints the answer (diagnostics).</summary>
    public Task<int> CallAsync(string toolName, string? jsonArguments, TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            var arguments = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(jsonArguments))
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonArguments);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ForecastCommandException("Les arguments doivent être un objet JSON, par exemple {\"from\":\"2026-10-05\"}.");
                    foreach (var property in doc.RootElement.EnumerateObject())
                        arguments[property.Name] = property.Value.Clone();
                }
                catch (JsonException ex)
                {
                    throw new ForecastCommandException($"Arguments JSON invalides : {ex.Message}");
                }
            }

            await using var session = await api.OpenAsync(cancellationToken);
            var result = await session.CallRawAsync(toolName, arguments, cancellationToken);
            await o.WriteLineAsync(result.IsError ? "Le serveur a répondu par une erreur :" : "Réponse :");
            await o.WriteLineAsync(result.Text);
            if (result.IsError)
                throw new ForecastCommandException("Appel en erreur.");
        });

    /// <summary>"forecast sync-tasks": refreshes the local list of Forecast tasks.</summary>
    public Task<int> SyncTasksAsync(TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            await o.WriteLineAsync("Lecture des tâches Forecast…");
            var result = await taskReader.SyncTasksAsync(cancellationToken);
            await o.WriteLineAsync(result.Summary);
            if (result.Warning is not null)
                throw new ForecastCommandException(result.Warning);
        });

    private static string Label(ForecastToolRole role) => role switch
    {
        ForecastToolRole.ListTasks => "Lister les tâches",
        ForecastToolRole.ListTimeEntries => "Lister les saisies de temps",
        ForecastToolRole.CreateTimeEntry => "Créer une saisie de temps",
        _ => "Utilisateur courant",
    };

    private async Task<int> RunAsync(TextWriter? output, Func<TextWriter, Task> action)
    {
        var o = output ?? Console.Out;
        try
        {
            await action(o);
            return 0;
        }
        catch (ForecastCommandException)
        {
            return 2;
        }
        catch (OperationCanceledException)
        {
            await o.WriteLineAsync("Opération annulée.");
            return 130;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Forecast command failed");
            await o.WriteLineAsync($"Erreur : {ex.Message}");
            return 1;
        }
    }

    private sealed class ForecastCommandException(string message) : Exception(message);
}
