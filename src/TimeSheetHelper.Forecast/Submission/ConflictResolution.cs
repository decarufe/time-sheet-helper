namespace TimeSheetHelper.Forecast.Submission;

/// <summary>
/// The user's explicit choice for a <see cref="CellState.Conflict"/> cell (Forecast holds hours the application did not write).
/// Without one, a conflicting cell is never written (G6).
/// </summary>
public enum ConflictResolution
{
    /// <summary>« Garder Forecast tel quel » — nothing is written, the cell stays reported.</summary>
    Keep,

    /// <summary>« Accepter Forecast » — the draft takes the hours of Forecast; nothing is written to Forecast.</summary>
    AcceptForecast,

    /// <summary>« Compléter » — only the missing delta D − F (&gt; 0) is added. Risky: needs a confirmation.</summary>
    Complement,

    /// <summary>« Retirer » — the registration is removed from Forecast (needs the delete tool). Risky: needs a confirmation.</summary>
    Remove,
}
