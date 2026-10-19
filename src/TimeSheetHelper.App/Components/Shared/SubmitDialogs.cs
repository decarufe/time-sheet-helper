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

    /// <summary>
    /// What the day(s) selected for the fill are called: the whole week (as before, null or every day of it selected), a single day
    /// by name, or a short list of day names.
    /// </summary>
    public static string DaysLabel(IReadOnlyCollection<DateOnly>? selectedDays, DateOnly monday)
    {
        var week = Enumerable.Range(0, 7).Select(monday.AddDays).ToHashSet();
        if (selectedDays is null || week.SetEquals(selectedDays))
            return UiLabels.WeekTitle(monday).ToLower(UiLabels.French);

        var ordered = selectedDays.Order().ToList();
        if (ordered.Count == 0)
            return "aucune journée";
        if (ordered.Count == 1)
            return $"le {UiLabels.Day(ordered[0])}";

        return $"{ordered.Count} journées ({string.Join(", ", ordered.Select(UiLabels.Day))})";
    }

    /// <summary>What the confirmation of the fill says: what is typed, what stays out, and the open points to verify.</summary>
    public static IReadOnlyList<string> FillParagraphs(
        decimal draftHours, int cellCount, DateOnly monday, IReadOnlyList<SubmissionGap> openToVerify, int ignoredCount, SubmissionSummary? summary,
        PendingAssignments? pending = null, IReadOnlyCollection<DateOnly>? selectedDays = null)
    {
        // A gap without a date concerns the whole week (auth, global error...): it is never left out by a day selection.
        var scopedToVerify = selectedDays is null
            ? openToVerify
            : openToVerify.Where(g => g.Date is not { } d || selectedDays.Contains(d)).ToList();

        var paragraphs = new List<string>
        {
            $"Time Sheet Helper va créer des entrées de temps pour {TimesheetUi.HoursLabel(draftHours)} réparties sur "
            + $"{cellCount} {(cellCount > 1 ? "cellules" : "cellule")} dans votre vraie feuille de temps Forecast ({DaysLabel(selectedDays, monday)}).",
            "Les cellules qui contiennent déjà d'autres heures ne sont pas modifiées : elles sont signalées dans le rapport.",
        };

        if (scopedToVerify.Count > 0)
        {
            var titles = string.Join(" ; ", scopedToVerify.Take(5).Select(g => g.Title));
            var more = scopedToVerify.Count > 5 ? $" ; et {scopedToVerify.Count - 5} autre(s)" : "";
            paragraphs.Add(
                $"{scopedToVerify.Count} {(scopedToVerify.Count > 1 ? "points à vérifier restent ouverts" : "point à vérifier reste ouvert")} "
                + $"et ne bloque{(scopedToVerify.Count > 1 ? "nt" : "")} pas la saisie : {titles}{more}.");
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

    /// <summary>The title of the fill confirmation: generic for the whole week, naming the day(s) otherwise.</summary>
    public static string FillTitle(IReadOnlyCollection<DateOnly>? selectedDays, DateOnly monday)
    {
        var week = Enumerable.Range(0, 7).Select(monday.AddDays).ToHashSet();
        if (selectedDays is null || selectedDays.Count == 0 || week.SetEquals(selectedDays))
            return "Saisir les heures dans Forecast ?";

        var ordered = selectedDays.Order().ToList();
        return ordered.Count == 1
            ? $"Saisir les heures du {UiLabels.Day(ordered[0])} dans Forecast ?"
            : $"Saisir les heures de {ordered.Count} journées dans Forecast ?";
    }

    /// <summary>The label of the fill button: generic for the whole week, naming the day(s) otherwise.</summary>
    public static string FillButtonLabel(IReadOnlyCollection<DateOnly>? selectedDays, DateOnly monday)
    {
        var week = Enumerable.Range(0, 7).Select(monday.AddDays).ToHashSet();
        if (selectedDays is null || selectedDays.Count == 0 || week.SetEquals(selectedDays))
            return "Saisir dans Forecast…";

        var ordered = selectedDays.Order().ToList();
        return ordered.Count == 1
            ? $"Saisir {UiLabels.Day(ordered[0])} dans Forecast…"
            : $"Saisir {ordered.Count} journées dans Forecast…";
    }

    /// <summary>Capitalizes the first letter of a day/week label embedded at the start of a sentence (the labels carry no case of their own).</summary>
    private static string Capitalize(string label) => label.Length == 0 ? label : char.ToUpper(label[0], UiLabels.French) + label[1..];

    /// <summary>What the fill report says once every selected day is entered but the rest of the week still needs its own fill.</summary>
    public static string ScopeEnteredMessage(string reportSummary, IReadOnlyCollection<DateOnly> selectedDays, DateOnly monday) =>
        $"{reportSummary}. {Capitalize(DaysLabel(selectedDays, monday))} est saisi dans Forecast. "
        + "Les autres journées de la semaine restent à soumettre avant qu'elle soit marquée comme saisie.";

    /// <summary>What the fill report says when some cells of the selected day(s) did not match and were left out of the fill.</summary>
    public static string ScopeNotEnteredMessage(string reportSummary, IReadOnlyCollection<DateOnly> selectedDays, DateOnly monday) =>
        $"{reportSummary}. Certaines cellules ne correspondent pas : réglez les écarts ci-dessous. "
        + $"{Capitalize(DaysLabel(selectedDays, monday))} n'est pas marqué(e) comme saisi(e).";

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
