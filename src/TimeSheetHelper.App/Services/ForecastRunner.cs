using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Services;

/// <summary>
/// Serializes the UI's Forecast operations (login, task sync, discovery, simulation, fill): they all drive the same
/// Edge profile through the singleton <see cref="ForecastSession"/>. The browser is closed after each operation,
/// like the command line does, so the profile lock is released and no window is left behind.
/// </summary>
public sealed class ForecastRunner(ForecastSession session, ILogger<ForecastRunner> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Description of the running operation, or null.</summary>
    public string? CurrentOperation { get; private set; }

    public bool IsBusy => CurrentOperation is not null;

    /// <exception cref="ForecastBusyException">Another Forecast operation is running.</exception>
    public async Task<T> RunAsync<T>(string description, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new ForecastBusyException(CurrentOperation);

        CurrentOperation = description;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            try
            {
                await session.CloseAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Closing the Forecast session failed");
            }

            CurrentOperation = null;
            _gate.Release();
        }
    }
}

public sealed class ForecastBusyException(string? operation)
    : Exception($"Une opération Forecast est déjà en cours{(operation is null ? "" : $" ({operation})")}. Réessayez quand elle sera terminée.");
