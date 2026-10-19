using System.Globalization;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>An état as shown: a French label, an icon of <c>Layout\Icon.razor</c> and a tone (<c>.badge.{Tone}</c>, <c>--{Tone}</c>).</summary>
public sealed record StateLabel(string Label, string Icon, string Tone);

/// <summary>What the send button of a day shows. <see cref="Reason"/> explains a disabled button (always visible, never only a tooltip).</summary>
public sealed record DayAction(string Label, bool Enabled, bool NeedsConfirmation, bool ReadsForecast, string? Reason);

/// <summary>One day of the strip: its state and its hours (<see cref="Hours"/> null when unknown).</summary>
public sealed record DayCard(DateOnly Date, StateLabel State, decimal? Hours, decimal? TargetHours, bool IsToday);

/// <summary>One option of the conflict chooser; <see cref="UnavailableReason"/> is set when the option is shown disabled.</summary>
public sealed record ResolutionOption(ConflictResolution Value, string Label, string Description, bool Destructive, string? UnavailableReason)
{
    public bool Available => UnavailableReason is null;
}

/// <summary>
/// States of the day by day submission → label, icon, tone (contracts §3, design spec §7: labels of the contracts, icons and tones of the
/// spec, CR-6). Pure: never reads anything, never writes anything. A state is never carried by the colour alone.
/// </summary>
public static class DayStateLabels
{
    private static CultureInfo French => TimesheetUi.French;

    /// <summary>A cell written during the send and read again in Forecast.</summary>
    public static readonly StateLabel WrittenAndVerified = new("Écrit et vérifié", "check", "ok");

    /// <summary>A day whose plan is being read (Forecast is read, nothing is written).</summary>
    public static readonly StateLabel Reading = new("Lecture de Forecast…", "refresh", "info");

    /// <summary>A day whose plan could not be read.</summary>
    public static readonly StateLabel ReadFailed = new("Lecture impossible", "warn", "danger");

    /// <summary>A day whose last reading of Forecast was incomplete: nothing is known (nor written) before Forecast is read again in full.</summary>
    public static readonly StateLabel ToReread = new("À relire", "warn", "warn");

    public static StateLabel Cell(CellState state) => state switch
    {
        CellState.New => new("Nouveau — sera écrit", "plus", "accent"),
        CellState.Unchanged => new("Déjà dans Forecast — ignoré (pas de doublon)", "check", "info"),
        CellState.Modified => new("Modifié depuis l'envoi", "refresh", "warn"),
        CellState.Deleted => new("Supprimé localement", "x", "danger"),
        CellState.Conflict => new("Conflit — Forecast modifié ailleurs", "warn", "danger"),
        CellState.Ignored => new("Ignoré (Ne pas envoyer)", "minus", "neutral"),
        CellState.Locked => new("Verrouillé — non écrit", "lock", "locked"),
        CellState.NotAttempted => new("Non tenté (envoi interrompu)", "clock", "warn"),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Status of a day; a day never attempted is « Prêt » when it holds hours, « Non saisi » otherwise.</summary>
    public static StateLabel Day(DayStatus status, bool hasHours) => status switch
    {
        DayStatus.NotAttempted => hasHours ? new("Prêt", "soumettre", "accent") : new("Non saisi", "ring", "neutral"),
        DayStatus.InProgress => new("Envoi en cours…", "refresh", "info"),
        DayStatus.Sent => new("Envoyé", "check", "ok"),
        DayStatus.SentUnknownFingerprint => new("Envoyé (empreinte inconnue)", "check", "ok"),
        DayStatus.Modified => new("Modifié depuis l'envoi", "refresh", "warn"),
        DayStatus.Partial => new("Envoi partiel", "warn", "warn"),
        DayStatus.Conflict => new("Écart à résoudre", "warn", "danger"),
        DayStatus.Locked => new("Verrouillé", "lock", "locked"),
        DayStatus.Error => new("Erreur d'envoi", "warn", "danger"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>
    /// Status of a planned day: « À relire » when the reading of Forecast was incomplete; otherwise a day never attempted is « Prêt » as soon as
    /// the draft holds hours for it (blocked or already in Forecast included), as the contracts derive it from the hours.
    /// </summary>
    public static StateLabel Day(DayPlan plan) =>
        plan.Gate == DayGate.ReadForecastFirst ? ToReread : Day(plan.Status, plan.Cells.Any(c => c.DraftHours > 0));

    /// <summary>Help line of a status, when the label needs one.</summary>
    public static string? DayHelp(DayStatus status) => status switch
    {
        DayStatus.SentUnknownFingerprint => "Sera comparé à Forecast avant tout nouvel envoi.",
        DayStatus.Partial => "Une partie des heures n'a pas été écrite : le rejeu relit Forecast et n'écrit que ce qui manque.",
        DayStatus.Conflict => "Forecast a été modifié ailleurs : choisissez comment traiter chaque conflit.",
        DayStatus.Locked => "Période approuvée ou facturée dans Forecast : rien n'est écrit ni modifié.",
        DayStatus.Error => "L'envoi a échoué : vérifiez l'état du jour puis relancez-le.",
        _ => null,
    };

    /// <summary>The send button for a gate (contracts §3.2); <see cref="DayGate.ReadForecastFirst"/> reads Forecast and writes nothing.</summary>
    public static DayAction Gate(DayGate gate) => gate switch
    {
        DayGate.Send => new("Soumettre ce jour", true, false, false, null),
        DayGate.SendChanges => new("Envoyer les changements", true, true, false, null),
        DayGate.Resume => new("Rejouer (écrit seulement ce qui manque)", true, false, false, null),
        DayGate.InProgress => new("Envoi en cours…", false, false, false, "Un envoi de ce jour est en cours : attendez qu'il se termine."),
        DayGate.NothingToSend => new("Rien à envoyer", false, false, false, "Aucune heure à envoyer pour ce jour."),
        DayGate.NothingNew => new("Rien de nouveau à envoyer", false, false, false,
            "Forecast contient déjà ces heures : rien n'est écrit, aucun doublon."),
        DayGate.ResolveConflicts => new("Écart à résoudre", false, false, false, "Choisissez comment traiter chaque conflit."),
        DayGate.ReadForecastFirst => new("Relire Forecast d'abord", true, false, true,
            "La lecture de Forecast était incomplète : rien ne sera écrit avant une lecture complète."),
        DayGate.Locked => new("Verrouillé", false, false, false, "Jour approuvé ou facturé dans Forecast : rien n'est écrit ni modifié."),
        _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, null),
    };

    /// <summary>
    /// The send button of a planned day, once the user's conflict choices are known: a day whose conflicts all have an allowed choice can be
    /// sent; a confirmation is asked before any change or removal in Forecast.
    /// </summary>
    public static DayAction Action(DayPlan plan, IReadOnlyDictionary<int, ConflictResolution> choices)
    {
        var conflicts = plan.Cells.Where(c => c.State == CellState.Conflict).ToList();
        var touchesExisting = plan.Cells.Any(c => c.Operation is CellOperation.Update or CellOperation.Delete)
            || conflicts.Any(c => choices.TryGetValue(c.ForecastTaskId, out var r) && IsDestructive(r));

        if (plan.Gate == DayGate.ResolveConflicts)
        {
            if (conflicts.Count == 0)
            {
                var blocked = plan.Cells.FirstOrDefault(c => c.BlockedReason is not null && c.State != CellState.Ignored);
                return Gate(DayGate.ResolveConflicts) with { Label = "Envoi bloqué", Reason = blocked?.BlockedReason ?? Gate(DayGate.ResolveConflicts).Reason };
            }

            var unresolved = conflicts.Count(c => !(choices.TryGetValue(c.ForecastTaskId, out var r) && c.AllowedResolutions.Contains(r)));
            if (unresolved > 0)
                return Gate(DayGate.ResolveConflicts) with
                {
                    Reason = unresolved == 1
                        ? "Choisissez comment traiter chaque conflit (1 conflit sans choix)."
                        : $"Choisissez comment traiter chaque conflit ({unresolved} conflits sans choix).",
                };
            return new("Soumettre ce jour", true, touchesExisting, false, null);
        }

        var action = Gate(plan.Gate);
        return action.Enabled && touchesExisting ? action with { NeedsConfirmation = true } : action;
    }

    public static string Resolution(ConflictResolution resolution) => resolution switch
    {
        ConflictResolution.Keep => "Garder",
        ConflictResolution.AcceptForecast => "Accepter Forecast",
        ConflictResolution.Complement => "Compléter +Δ",
        ConflictResolution.Remove => "Retirer",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null),
    };

    /// <summary>Complement raises and Remove deletes a registration of Forecast: both need a confirmation.</summary>
    public static bool IsDestructive(ConflictResolution resolution) => resolution is ConflictResolution.Complement or ConflictResolution.Remove;

    /// <summary>After a new reading, a choice only stays for a cell still in conflict that allows it.</summary>
    public static void KeepValidChoices(DayPlan plan, IDictionary<int, ConflictResolution> choices)
    {
        foreach (var taskId in choices.Keys.ToList())
            if (!plan.Cells.Any(c => c.ForecastTaskId == taskId && c.State == CellState.Conflict && c.AllowedResolutions.Contains(choices[taskId])))
                choices.Remove(taskId);
    }

    /// <summary>The four choices of a conflict, the unavailable ones with their reason (shown disabled, never hidden).</summary>
    public static IReadOnlyList<ResolutionOption> Options(CellPlan cell, ForecastCapabilities capabilities)
    {
        var f = cell.ForecastHours ?? 0m;
        var delta = cell.DraftHours - f;
        return
        [
            Option(cell, capabilities, ConflictResolution.Keep, "Ne rien modifier dans Forecast."),
            Option(cell, capabilities, ConflictResolution.AcceptForecast, $"Aligner le brouillon sur Forecast ({Hours(f)})."),
            Option(cell, capabilities, ConflictResolution.Complement,
                delta > 0 ? $"Ajouter exactement {Hours(delta)} dans Forecast." : "Ajouter dans Forecast la différence du brouillon."),
            Option(cell, capabilities, ConflictResolution.Remove, $"Retirer la saisie de Forecast ({Hours(f)})."),
        ];
    }

    private static ResolutionOption Option(CellPlan cell, ForecastCapabilities capabilities, ConflictResolution value, string description) =>
        new(value, Resolution(value), description, IsDestructive(value),
            cell.AllowedResolutions.Contains(value) ? null : UnavailableReason(cell, capabilities, value));

    private static string UnavailableReason(CellPlan cell, ForecastCapabilities capabilities, ConflictResolution value)
    {
        if (cell.AmbiguousGroup is not null)
            return "Forecast ne permet pas de savoir à quelle tâche ces heures appartiennent : vérifiez dans Forecast.";
        return value switch
        {
            ConflictResolution.Complement when cell.DraftHours - (cell.ForecastHours ?? 0m) <= 0 =>
                "Le brouillon ne dépasse pas Forecast : rien à compléter.",
            ConflictResolution.Complement => capabilities.Update == ForecastCapability.Available
                ? "La saisie de Forecast n'est pas identifiable seule : modifiez-la dans Forecast."
                : capabilities.UnavailableReason ?? "Le serveur Forecast ne propose pas la modification d'une saisie : modifiez-la dans Forecast.",
            ConflictResolution.Remove => capabilities.Delete == ForecastCapability.Available
                ? "La saisie de Forecast n'est pas identifiable seule : retirez-la dans Forecast."
                : capabilities.UnavailableReason ?? "Le serveur Forecast ne propose pas la suppression d'une saisie : retirez-la dans Forecast.",
            _ => "Choix indisponible pour cette cellule.",
        };
    }

    /// <summary>The line under a cell's badge: what the send does with it (and the chosen resolution, repeated in words).</summary>
    public static string CellNote(CellPlan cell, ConflictResolution? choice)
    {
        if (cell.BlockedReason is not null && cell.State != CellState.Ignored)
            return cell.BlockedReason;

        var f = cell.ForecastHours ?? 0m;
        return cell.State switch
        {
            CellState.New => $"+{Hours(cell.DraftHours)} ajoutées dans Forecast.",
            CellState.Unchanged => "Heures identiques : aucune écriture.",
            CellState.Modified => $"Forecast {Hours(f)} → {Hours(cell.DraftHours)} : mise à jour de la saisie existante (après confirmation).",
            CellState.Deleted => cell.Operation == CellOperation.Delete
                ? $"Forecast garde {Hours(f)} jusqu'à l'envoi : la saisie sera retirée après confirmation."
                : $"Forecast garde {Hours(f)} : l'application ne supprime jamais seule.",
            CellState.Conflict => choice is { } r
                ? $"Résolution : {ChoiceSummary(cell, r)}"
                : $"Forecast {Hours(f)} ≠ brouillon {Hours(cell.DraftHours)} : cellule non modifiée.",
            CellState.Ignored => "Exclue de l'envoi pour la semaine.",
            CellState.Locked => "Période approuvée / facturée dans Forecast.",
            CellState.NotAttempted => "Sera écrite au rejeu.",
            _ => "",
        };
    }

    private static string ChoiceSummary(CellPlan cell, ConflictResolution choice)
    {
        var f = cell.ForecastHours ?? 0m;
        return choice switch
        {
            ConflictResolution.Keep => "garder, rien n'est modifié dans Forecast.",
            ConflictResolution.AcceptForecast => $"accepter Forecast, le brouillon passe à {Hours(f)}.",
            ConflictResolution.Complement => $"ajouter +{Hours(cell.DraftHours - f)} dans Forecast.",
            ConflictResolution.Remove => $"retirer la saisie de {Hours(f)} de Forecast.",
            _ => "",
        };
    }

    /// <summary>« 2 à écrire · 1 déjà dans Forecast · 1 ignorée · 0 non envoyée ».</summary>
    public static string Summary(DayPlan plan)
    {
        var write = plan.Cells.Count(c => c.Operation != CellOperation.None);
        var same = plan.Cells.Count(c => c.State == CellState.Unchanged);
        var ignored = plan.Cells.Count(c => c.State == CellState.Ignored);
        var notSent = plan.Cells.Count(c => c.Operation == CellOperation.None && c.State is not (CellState.Unchanged or CellState.Ignored));
        return $"{write} à écrire · {same} déjà dans Forecast · {ignored} {Plural(ignored, "ignorée", "ignorées")} · "
            + $"{notSent} {Plural(notSent, "non envoyée", "non envoyées")}";
    }

    /// <summary>What a send will do, for the confirmation: « 2 créations, 1 modification, 1 retrait ».</summary>
    public static string Operations(DayPlan plan, IReadOnlyDictionary<int, ConflictResolution> choices)
    {
        int Count(CellOperation op) => plan.Cells.Count(c => c.Operation == op);
        int Chosen(ConflictResolution r) => plan.Cells.Count(c => c.State == CellState.Conflict && choices.TryGetValue(c.ForecastTaskId, out var x) && x == r);
        var creations = Count(CellOperation.Create);
        var updates = Count(CellOperation.Update) + Chosen(ConflictResolution.Complement);
        var removals = Count(CellOperation.Delete) + Chosen(ConflictResolution.Remove);
        var parts = new List<string>();
        if (creations > 0)
            parts.Add($"{creations} {Plural(creations, "création", "créations")}");
        if (updates > 0)
            parts.Add($"{updates} {Plural(updates, "modification", "modifications")}");
        if (removals > 0)
            parts.Add($"{removals} {Plural(removals, "retrait", "retraits")}");
        return parts.Count == 0 ? "aucune écriture dans Forecast" : string.Join(", ", parts);
    }

    /// <summary>Journal line: the action, its icon and tone (the outcome wins over the kind).</summary>
    public static StateLabel Journal(JournalKind kind, JournalOutcome outcome)
    {
        var label = kind switch
        {
            JournalKind.Planned => "Soumettre ce jour",
            JournalKind.Sent => "Saisie créée",
            JournalKind.Updated => "Saisie modifiée",
            JournalKind.Deleted => "Saisie retirée",
            JournalKind.Skipped => "Rien d'écrit",
            JournalKind.Resolved => "Conflit résolu",
            JournalKind.Interrupted => "Envoi interrompu",
            JournalKind.Resumed => "Rejeu",
            JournalKind.Cancelled => "Envoi annulé",
            JournalKind.Failed => "Échec",
            JournalKind.Migrated => "Reprise de l'historique",
            _ => kind.ToString(),
        };
        return outcome switch
        {
            JournalOutcome.Failed => new(label, "x", "danger"),
            JournalOutcome.Partial => new(label, "warn", "warn"),
            _ => kind switch
            {
                JournalKind.Sent => new(label, "plus", "ok"),
                JournalKind.Updated or JournalKind.Resumed => new(label, "refresh", "ok"),
                JournalKind.Deleted => new(label, "x", "warn"),
                JournalKind.Interrupted or JournalKind.Cancelled => new(label, "warn", "warn"),
                JournalKind.Failed => new(label, "x", "danger"),
                JournalKind.Planned or JournalKind.Skipped or JournalKind.Migrated => new(label, "clock", "info"),
                _ => new(label, "check", "ok"),
            },
        };
    }

    /// <summary>« Mercredi 21 juillet ».</summary>
    public static string LongDay(DateOnly day) => TimesheetUi.Capitalize(day.ToString("dddd d MMMM", French));

    /// <summary>« Mer 21 ».</summary>
    public static string ShortDay(DateOnly day) => TimesheetUi.Capitalize(day.ToString("ddd", French).TrimEnd('.')) + " " + day.Day;

    /// <summary>« 7,5 h ».</summary>
    public static string Hours(decimal hours) => hours.ToString("0.##", French) + " h";

    private static string Plural(int count, string one, string many) => count > 1 ? many : one;
}
