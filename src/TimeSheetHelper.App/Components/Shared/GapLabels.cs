using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>French labels of the gaps of the Submit page: severity, type, resolution, cell.</summary>
public static class GapLabels
{
    public static string Severity(SubmissionGapSeverity severity) => severity switch
    {
        SubmissionGapSeverity.Blocking => "Bloquant",
        SubmissionGapSeverity.ToVerify => "À vérifier",
        _ => "Info",
    };

    /// <summary>A text mark that goes with the colour: the severity is never carried by the colour alone.</summary>
    public static string SeveritySymbol(SubmissionGapSeverity severity) => severity switch
    {
        SubmissionGapSeverity.Blocking => "✕",
        SubmissionGapSeverity.ToVerify => "!",
        _ => "i",
    };

    public static string SeverityClass(SubmissionGapSeverity severity) => severity switch
    {
        SubmissionGapSeverity.Blocking => "sev-blocking",
        SubmissionGapSeverity.ToVerify => "sev-verify",
        _ => "sev-info",
    };

    /// <summary>Heading of a group of gaps of one type.</summary>
    public static string TypeHeading(SubmissionGapType type) => type switch
    {
        SubmissionGapType.UnclassifiedRemaining => "Heures non classées",
        SubmissionGapType.DayBelowTarget => "Jours sous l'objectif",
        SubmissionGapType.DayAboveTarget => "Jours au-dessus de l'objectif",
        SubmissionGapType.CellLocked => "Jours verrouillés dans Forecast",
        SubmissionGapType.CellPendingApproval => "Jours soumis pour approbation",
        SubmissionGapType.TaskNotFound => "Tâches introuvables dans Forecast",
        SubmissionGapType.TaskAmbiguous => "Tâches ambiguës dans Forecast",
        SubmissionGapType.TaskUnknownLocally => "Tâches inconnues localement",
        SubmissionGapType.TaskInactive => "Tâches désactivées",
        SubmissionGapType.TaskClosed => "Tâches terminées dans Forecast",
        SubmissionGapType.TaskBlocked => "Tâches bloquées dans Forecast",
        SubmissionGapType.ProjectClosed => "Projets clos ou en pause",
        SubmissionGapType.EstimateExceeded => "Estimations dépassées",
        SubmissionGapType.ForecastHigher => "Forecast contient plus d'heures que prévu",
        SubmissionGapType.ForecastLower => "Forecast contient moins d'heures que prévu",
        SubmissionGapType.ReadTruncated => "Lecture de Forecast incomplète",
        SubmissionGapType.ForecastError => "Erreurs de Forecast",
        SubmissionGapType.AuthRequired => "Connexion à Forecast requise",
        SubmissionGapType.CellEntryUnknown => "Saisies au résultat inconnu",
        SubmissionGapType.FillInterrupted => "Saisie interrompue",
        SubmissionGapType.ForecastWarning => "Avertissements de Forecast",
        SubmissionGapType.PreflightUnavailable => "Vérifications préalables incomplètes",
        SubmissionGapType.ForecastTimeWithoutProject => "Temps sans projet (exclu de la saisie)",
        _ => type.ToString(),
    };

    public static string Resolution(SubmissionGapResolution resolution) => resolution switch
    {
        SubmissionGapResolution.LinkTask => "Relier à une tâche Forecast…",
        SubmissionGapResolution.CreateTask => "Créer / corriger la tâche…",
        SubmissionGapResolution.RefreshTasks => "Rafraîchir les tâches depuis Forecast",
        SubmissionGapResolution.ReactivateTask => "Réactiver la tâche",
        SubmissionGapResolution.ReclassifySlots => "Classer dans la vue Semaine",
        SubmissionGapResolution.MarkNotWork => "Pas du travail (dans la vue Jour)",
        SubmissionGapResolution.Remove => "Supprimer des créneaux (dans la vue Jour)",
        SubmissionGapResolution.ViewDaySlots => "Voir les créneaux du jour",
        SubmissionGapResolution.RecalculateDraft => "Recalculer le brouillon",
        SubmissionGapResolution.OpenInForecast => "Ouvrir dans Forecast",
        SubmissionGapResolution.Retry => "Réessayer",
        SubmissionGapResolution.ReadAgain => "Relire depuis Forecast",
        SubmissionGapResolution.ResumeFill => "Relire, puis reprendre la saisie",
        SubmissionGapResolution.SignIn => "Se connecter à Forecast",
        SubmissionGapResolution.ReplaceTask => "Remplacer par une autre tâche",
        SubmissionGapResolution.IgnoreKnowingly => "Ignorer en connaissance de cause",
        SubmissionGapResolution.AlignDraftOnForecast => "Aligner le brouillon sur Forecast",
        SubmissionGapResolution.AddComplement => "Ajouter le complément",
        SubmissionGapResolution.AcceptForecastHours => "Accepter les heures de Forecast",
        _ => resolution.ToString(),
    };

    /// <summary>"Dév API · lun. 12 oct." — the cell of a gap, for headings and aria labels.</summary>
    public static string Cell(SubmissionGapCell cell) => $"{cell.TaskName} · {UiLabels.Day(cell.Date)}";

    /// <summary>What the gap is about, in a few words: its cell(s), its day, or nothing.</summary>
    public static string Context(SubmissionGap gap)
    {
        if (gap.Cell is { } cell)
        {
            var more = gap.RelatedCells.Count;
            return more == 0 ? Cell(cell) : $"{Cell(cell)} (+ {more} {(more > 1 ? "autres tâches" : "autre tâche")})";
        }

        return gap.Date is { } date ? UiLabels.Day(date) : "";
    }

    /// <summary>Approval state of a registration already in Forecast.</summary>
    public static string Approval(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "APPROVED" => "Approuvée",
        "SUBMITTED" => "Soumise pour approbation",
        "NOT_SUBMITTED" => "Non soumise",
        null or "" => "",
        _ => status.Trim(),
    };

    /// <summary>A DOM-safe id for a gap ("TaskNotFound|12|20261012" gives "gap-TaskNotFound-12-20261012").</summary>
    public static string DomId(string gapId) =>
        "gap-" + new string(gapId.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>"Cellule 6,00 → 4,25 h", "Jour …", "Semaine …": the before → after of a preview, the parts that are known.</summary>
    public static IReadOnlyList<string> PreviewParts(SubmissionGapPreview preview)
    {
        var parts = new List<string>();
        if (preview.CellBefore is { } cellBefore && preview.CellAfter is { } cellAfter)
            parts.Add($"Cellule {FromTo(cellBefore, cellAfter)}");
        if (preview.DayBefore is { } dayBefore && preview.DayAfter is { } dayAfter)
            parts.Add($"Jour {FromTo(dayBefore, dayAfter)}");
        parts.Add($"Semaine {FromTo(preview.WeekBefore, preview.WeekAfter)}");
        return parts;
    }

    /// <summary>The three numbers of a gap with Forecast's hours: Forecast, expected, difference; null when there are none.</summary>
    public static (string Forecast, string Expected, string Delta)? Figures(SubmissionGap gap) =>
        gap.ForecastHours is { } forecast && gap.ExpectedHours is { } expected
            ? (TimesheetUi.HoursLabel(forecast), TimesheetUi.HoursLabel(expected), TimesheetUi.SignedHours(gap.DeltaHours ?? forecast - expected))
            : null;

    private static string FromTo(decimal before, decimal after) => $"{TimesheetUi.Hours(before)} → {TimesheetUi.HoursLabel(after)}";
}
