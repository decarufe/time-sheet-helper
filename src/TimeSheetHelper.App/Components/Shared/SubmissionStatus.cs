using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

public enum StatusLevel { Idle, Ok, Warn, Danger }

/// <summary>One of the three steps of the page (draft, simulation, fill) with what to say about it.</summary>
public sealed record StatusStep(string Name, string Text, StatusLevel Level);

/// <param name="Analysis">The latest analysis (null before the first one).</param>
/// <param name="OpenBlocking">Blocking gaps still open (the failure gap of an operation included).</param>
/// <param name="OpenToVerify">Gaps to verify still open (those ignored for the session are not).</param>
/// <param name="Ignored">Gaps ignored for the session.</param>
/// <param name="FirstBlockingId">The first open blocking gap, the one the fill reason links to.</param>
/// <param name="HasRows">The draft holds hours to type.</param>
/// <param name="HasSimulation">A simulation was run on this week.</param>
/// <param name="SimulationFresh">That simulation was made on exactly the current draft.</param>
/// <param name="SimulationSucceeded">Every cell of that simulation is fine.</param>
/// <param name="SimulatedUtc">When the simulation ended.</param>
/// <param name="Refreshing">The draft or the analysis is being updated (the screen is kept, greyed).</param>
public sealed record SubmissionStatusInput(
    SubmissionAnalysis? Analysis,
    int OpenBlocking,
    int OpenToVerify,
    int Ignored,
    string? FirstBlockingId,
    bool DraftSubmitted,
    bool HasRows,
    bool HasSimulation,
    bool SimulationFresh,
    bool SimulationSucceeded,
    DateTime? SimulatedUtc,
    bool SimulationRunning,
    bool FillRunning,
    bool Refreshing,
    DateTime NowUtc);

/// <summary>What the status bar says: counters, steps, week against the objective, verdict, and whether the fill may start.</summary>
public sealed record SubmissionStatus(
    int Blocking,
    int ToVerify,
    int Ignored,
    int WithoutGap,
    IReadOnlyList<StatusStep> Steps,
    string WeekText,
    StatusLevel WeekLevel,
    string Verdict,
    StatusLevel VerdictLevel,
    bool CanFill,
    string? FillDisabledReason,
    string? FirstBlockingId)
{
    /// <summary>The summary read out when it changes (aria-live).</summary>
    public string Announcement =>
        $"{Verdict}. {CountText(Blocking, "bloquant", "bloquants")}, {ToVerify} à vérifier, {WithoutGap} sans écart.";

    private static string CountText(int count, string singular, string plural) => $"{count} {(count > 1 ? plural : singular)}";
}

public static class SubmissionStatusBuilder
{
    public static SubmissionStatus Build(SubmissionStatusInput input)
    {
        var summary = input.Analysis?.Summary;
        var reason = FillReason(input);
        return new SubmissionStatus(
            input.OpenBlocking,
            input.OpenToVerify,
            input.Ignored,
            summary?.CellsWithoutGapCount ?? 0,
            Steps(input, reason),
            WeekText(summary, out var weekLevel),
            weekLevel,
            Verdict(input, out var verdictLevel),
            verdictLevel,
            reason is null,
            reason,
            input.FirstBlockingId);
    }

    /// <summary>Why the fill cannot start (null when it can). The contract is unchanged: a simulation of this very draft with nothing blocking.</summary>
    public static string? FillReason(SubmissionStatusInput input)
    {
        if (input.DraftSubmitted)
            return "La semaine est déjà saisie dans Forecast : rouvrez le brouillon pour la modifier.";
        if (!input.HasRows)
            return "Aucune heure classée à saisir : attribuez des tâches dans la vue Semaine.";
        if (input.SimulationRunning || input.FillRunning || input.Refreshing)
            return "Une opération est en cours : attendez la fin de la mise à jour.";
        if (!input.HasSimulation)
            return "Lancez d'abord la simulation : la saisie n'est possible qu'après une simulation réussie de ce brouillon.";
        if (!input.SimulationFresh)
            return "Le brouillon a changé depuis la simulation : elle doit être relancée (elle se relance seule après une correction).";
        if (input.OpenBlocking > 0)
            return input.OpenBlocking == 1
                ? "1 écart bloquant reste à régler."
                : $"{input.OpenBlocking} écarts bloquants restent à régler.";
        if (!input.SimulationSucceeded)
            return "La simulation a signalé des problèmes : relancez-la après correction.";
        if (input.Analysis?.Summary is { CanFill: false })
            return "La lecture de Forecast est incomplète : relisez la semaine.";
        return null;
    }

    private static string Verdict(SubmissionStatusInput input, out StatusLevel level)
    {
        if (input.DraftSubmitted)
        {
            level = StatusLevel.Ok;
            return "Semaine saisie dans Forecast";
        }

        if (input.OpenBlocking > 0)
        {
            level = StatusLevel.Danger;
            return input.OpenBlocking == 1 ? "1 écart bloque la saisie" : $"{input.OpenBlocking} écarts bloquent la saisie";
        }

        if (!input.HasRows)
        {
            level = StatusLevel.Idle;
            return "Rien à saisir pour cette semaine";
        }

        if (!input.HasSimulation)
        {
            level = StatusLevel.Idle;
            return "Lancez la simulation pour savoir si la semaine est prête";
        }

        if (!input.SimulationFresh)
        {
            level = StatusLevel.Warn;
            return "Simulation périmée : le brouillon a changé";
        }

        if (!input.SimulationSucceeded)
        {
            level = StatusLevel.Warn;
            return "La simulation a signalé des problèmes";
        }

        if (input.OpenToVerify > 0)
        {
            level = StatusLevel.Warn;
            return input.OpenToVerify == 1
                ? "Prêt à saisir — 1 point à vérifier reste ouvert"
                : $"Prêt à saisir — {input.OpenToVerify} points à vérifier restent ouverts";
        }

        level = StatusLevel.Ok;
        return "Prêt à saisir";
    }

    private static string WeekText(SubmissionSummary? summary, out StatusLevel level)
    {
        if (summary is null)
        {
            level = StatusLevel.Idle;
            return "";
        }

        if (summary.WeekTargetHours is not { } target)
        {
            level = StatusLevel.Idle;
            return $"Semaine {TimesheetUi.HoursLabel(summary.WeekHours)}";
        }

        var text = $"Semaine {TimesheetUi.HoursLabel(summary.WeekHours)} / {TimesheetUi.HoursLabel(target)}";
        if (summary.WeekWithinTolerance == true)
        {
            level = StatusLevel.Ok;
            return text + $" (objectif atteint, tolérance ±{TimesheetUi.HoursLabel(summary.ToleranceHours)})";
        }

        level = StatusLevel.Warn;
        var delta = summary.WeekDeltaHours ?? summary.WeekHours - target;
        return text + $" ({TimesheetUi.SignedHours(delta)} par rapport à l'objectif)";
    }

    private static List<StatusStep> Steps(SubmissionStatusInput input, string? fillReason)
    {
        var draft = input.DraftSubmitted
            ? new StatusStep("Brouillon", "saisi dans Forecast", StatusLevel.Ok)
            : input.Refreshing
                ? new StatusStep("Brouillon", "mise à jour…", StatusLevel.Idle)
                : input.HasRows
                    ? new StatusStep("Brouillon", "à jour", StatusLevel.Ok)
                    : new StatusStep("Brouillon", "vide", StatusLevel.Warn);

        StatusStep simulation;
        if (input.SimulationRunning)
            simulation = new StatusStep("Simulation", "en cours…", StatusLevel.Idle);
        else if (!input.HasSimulation)
            simulation = new StatusStep("Simulation", "à lancer", StatusLevel.Idle);
        else
        {
            var when = input.SimulatedUtc is { } at ? $" ({UiLabels.Ago(at, input.NowUtc)})" : "";
            simulation = !input.SimulationFresh
                ? new StatusStep("Simulation", "périmée" + when, StatusLevel.Warn)
                : input.SimulationSucceeded
                    ? new StatusStep("Simulation", "réussie" + when, StatusLevel.Ok)
                    : new StatusStep("Simulation", "écarts à régler" + when, StatusLevel.Danger);
        }

        StatusStep fill;
        if (input.FillRunning)
            fill = new StatusStep("Saisie", "en cours…", StatusLevel.Idle);
        else if (input.DraftSubmitted)
            fill = new StatusStep("Saisie", "faite", StatusLevel.Ok);
        else if (fillReason is null)
            fill = new StatusStep("Saisie", "prête", StatusLevel.Ok);
        else
            fill = new StatusStep("Saisie", "bloquée", StatusLevel.Idle);

        return [draft, simulation, fill];
    }
}
