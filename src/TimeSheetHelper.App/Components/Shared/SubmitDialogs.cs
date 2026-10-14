using TimeSheetHelper.Engine;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>Groups of unclassified work given a task but not yet confirmed (« À confirmer »): real overrides already counted in the draft.</summary>
public sealed record PendingAssignments(int Groups, decimal Hours)
{
    public static PendingAssignments None { get; } = new(0, 0m);

    public static PendingAssignments Of(IReadOnlyCollection<PendingGroupAssignment> pending) =>
        pending.Count == 0 ? None : new(pending.Count, pending.Sum(p => p.Hours));
}

/// <summary>The text of the dialogs that guard the two writing actions of the page (fill, merge of a task). Pure.</summary>
public static class SubmitDialogs
{
    private const decimal HoursPerSlot = 0.25m;

    /// <summary>What the confirmation of the fill says: what is typed, what stays out, and the open points to verify.</summary>
    public static IReadOnlyList<string> FillParagraphs(
        decimal draftHours, int cellCount, DateOnly monday, IReadOnlyList<SubmissionGap> openToVerify, int ignoredCount, SubmissionSummary? summary,
        PendingAssignments? pending = null)
    {
        var paragraphs = new List<string>
        {
            $"Time Sheet Helper va créer des entrées de temps pour {TimesheetUi.HoursLabel(draftHours)} réparties sur "
            + $"{cellCount} {(cellCount > 1 ? "cellules" : "cellule")} dans votre vraie feuille de temps Forecast ({UiLabels.WeekTitle(monday).ToLower(UiLabels.French)}).",
            "Les cellules qui contiennent déjà d'autres heures ne sont pas modifiées : elles sont signalées dans le rapport.",
        };

        if (openToVerify.Count > 0)
        {
            var titles = string.Join(" ; ", openToVerify.Take(5).Select(g => g.Title));
            var more = openToVerify.Count > 5 ? $" ; et {openToVerify.Count - 5} autre(s)" : "";
            paragraphs.Add(
                $"{openToVerify.Count} {(openToVerify.Count > 1 ? "points à vérifier restent ouverts" : "point à vérifier reste ouvert")} "
                + $"et ne bloque{(openToVerify.Count > 1 ? "nt" : "")} pas la saisie : {titles}{more}.");
        }

        if (pending is { Groups: > 0 })
        {
            paragraphs.Add(
                $"{pending.Groups} {(pending.Groups > 1 ? "groupes" : "groupe")} d'activités en attente « À confirmer » ({TimesheetUi.HoursLabel(pending.Hours)}) : "
                + "leur attribution est déjà comptée dans le brouillon et sera saisie dans Forecast telle quelle (cela ne bloque pas la saisie). "
                + "Confirmez-la ou annulez-la dans la vue Semaine avant de saisir si besoin.");
        }

        if (ignoredCount > 0)
            paragraphs.Add($"{ignoredCount} {(ignoredCount > 1 ? "écarts ignorés" : "écart ignoré")} pour cette session : le brouillon n'est pas modifié et la saisie reste la même.");

        if (summary is not null && summary.ForecastNonProjectHours > 0)
            paragraphs.Add($"{TimesheetUi.HoursLabel(summary.ForecastNonProjectHours)} sans projet restent dans Forecast : elles sont exclues de la saisie.");

        paragraphs.Add("La semaine ne sera pas envoyée pour approbation : vous le ferez vous-même dans Forecast.");
        return paragraphs;
    }

    /// <summary>What merging a local task into one Forecast already knows touches.</summary>
    public sealed record MergeImpact(string LocalName, string TargetName, string TargetCode, int DraftCells, decimal DraftHours, int Rules);

    public static string MergeTitle(MergeImpact impact) => $"Fusionner « {impact.LocalName} » avec {impact.TargetCode} ?";

    public static string MergeMessage(MergeImpact impact)
    {
        var slots = (int)Math.Round(impact.DraftHours / HoursPerSlot, MidpointRounding.AwayFromZero);
        return $"La tâche locale « {impact.LocalName} » sera fusionnée avec la tâche {impact.TargetCode} déjà connue (« {impact.TargetName} »). "
               + $"{Count(impact.Rules, "règle", "règles")} et {Count(impact.DraftCells, "cellule du brouillon", "cellules du brouillon")} "
               + $"({TimesheetUi.HoursLabel(impact.DraftHours)}, soit {Count(slots, "créneau", "créneaux")} de 15 minutes) lui seront rattachées, "
               + "puis la tâche locale sera supprimée. Cette action est irréversible.";
    }

    private static string Count(int count, string singular, string plural) => $"{count} {(count > 1 ? plural : singular)}";
}
