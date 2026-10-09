using Microsoft.Extensions.Logging;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Entry points for the CLI ("forecast login", "forecast discover", "forecast sync-tasks").
/// Each method returns a process exit code and writes French, user-facing lines to <c>output</c> (console by default).
/// </summary>
public sealed class ForecastCommands(
    ForecastSession session,
    ForecastDiscovery discovery,
    ForecastTaskReader taskReader,
    ILogger<ForecastCommands> logger)
{
    /// <summary>"forecast login": opens Edge on Forecast and waits until the user is logged in.</summary>
    public Task<int> LoginAsync(TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            await o.WriteLineAsync("Ouverture de Forecast dans Edge… connectez-vous si la page de connexion s'affiche.");
            await session.EnsureLoggedInAsync(cancellationToken);
            await o.WriteLineAsync("Connecté à Forecast. La session est conservée pour les prochaines exécutions.");
        });

    /// <summary>"forecast discover": captures the Forecast UI to fix the selectors.</summary>
    public Task<int> DiscoverAsync(TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(output, async o =>
        {
            await o.WriteLineAsync("Découverte de l'interface Forecast (rien ne sera enregistré dans Forecast)…");
            var dir = await discovery.RunAsync(cancellationToken);
            await o.WriteLineAsync($"Captures enregistrées dans : {dir}");
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
        finally
        {
            await session.CloseAsync();
        }
    }

    private sealed class ForecastCommandException(string message) : Exception(message);
}
