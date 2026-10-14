using Microsoft.Extensions.Logging;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>
/// Calls <see cref="IForecastLookup"/> from a Blazor event handler without letting an unexpected exception reach the circuit:
/// it becomes a failed result with a readable message, so the form keeps working and the user can type by hand.
/// </summary>
public static class ForecastLookupGuard
{
    public const string UnexpectedAutoFillError = "Le remplissage automatique depuis Forecast a échoué.";

    /// <summary>
    /// Asks Forecast to fill the form. Cancellation still throws <see cref="OperationCanceledException"/> (the caller treats it as silent);
    /// any other exception is logged by type only (its message could repeat what the user typed) and returned as a failed result.
    /// </summary>
    public static async Task<ForecastAutoFillResult> AutoFillAsync(
        IForecastLookup lookup, AutoFillRequest request, ILogger logger, CancellationToken token)
    {
        try
        {
            return await lookup.AutoFillAsync(request.ProjectNumber, request.TaskNumber, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Forecast auto-fill failed unexpectedly ({ExceptionType}).", ex.GetType().Name);
            return new ForecastAutoFillResult(null, null, null, [], UnexpectedAutoFillError);
        }
    }
}
