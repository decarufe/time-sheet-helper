using System.Globalization;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Grouping;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>Formatting helpers shared by the week and day review pages (French labels).</summary>
public static class TimesheetUi
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-CA");

    /// <summary>Confidence below this is flagged as low in the grids.</summary>
    public const double LowConfidence = 0.6;

    /// <summary>Confidence below this (and above <see cref="LowConfidence"/>) is shown as medium.</summary>
    public const double MediumConfidence = 0.8;

    /// <summary>Hours with two decimals and a French decimal comma, e.g. "1,75".</summary>
    public static string Hours(decimal hours) => hours.ToString("0.00", French);

    /// <summary>The one format of hours with their unit, e.g. "1,75 h" (the tables use <see cref="Hours"/> without the unit).</summary>
    public static string HoursLabel(decimal hours) => Hours(hours) + " h";

    /// <summary>A difference in hours with its sign, e.g. "+1,25 h", "−0,75 h"; "0,00 h" when there is none.</summary>
    public static string SignedHours(decimal hours) =>
        hours > 0 ? "+" + HoursLabel(hours) : hours < 0 ? "−" + HoursLabel(-hours) : HoursLabel(0);

    public static string Percent(double share) => $"{Math.Round(share * 100):0} %";

    public static string ConfidenceClass(double confidence) =>
        confidence < LowConfidence ? "conf-low" : confidence < MediumConfidence ? "conf-mid" : "conf-high";

    /// <summary>Stable hue per task id (golden angle), used as <c>--task-h</c> by the scoped stylesheets.</summary>
    public static int TaskHue(int taskId) => (int)(taskId * 137.508 % 360);

    public static string TaskStyle(int taskId) => $"--task-h: {TaskHue(taskId)}";

    public static DateOnly WeekStartOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static string IsoDate(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseIsoDate(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], French) + text[1..];

    public const string NoProjectLabel = "(sans projet)";

    public const string NoTaskLabel = "(sans tâche)";

    /// <summary>
    /// "Client / Tâche" for a Forecast task, with a placeholder for a missing project or task name so the row is never blank;
    /// a task unknown locally is shown by its id.
    /// </summary>
    public static string TaskLabel(ForecastTask? task, int taskId)
    {
        if (task is null)
            return $"Tâche #{taskId} (inconnue)";
        if (task.IsIgnored)
            return task.DisplayName;

        var project = string.IsNullOrWhiteSpace(task.ClientName) ? task.ProjectName : task.ClientName;
        return $"{(string.IsNullOrWhiteSpace(project) ? NoProjectLabel : project.Trim())} / {(string.IsNullOrWhiteSpace(task.TaskName) ? NoTaskLabel : task.TaskName.Trim())}";
    }

    public static string GroupKindLabel(GroupKind kind) => kind switch
    {
        GroupKind.Repo => "Dépôt",
        GroupKind.Folder => "Dossier",
        GroupKind.Meeting => "Réunion",
        GroupKind.Domain => "Domaine",
        GroupKind.People => "Personnes",
        GroupKind.App => "Application",
        _ => "Source",
    };

    /// <summary>Value part of a group key ("repo:contoso/api" gives "contoso/api").</summary>
    public static string GroupValue(string key) =>
        GroupKey.TryParse(key, out var parsed) ? parsed.Value : key;

    public static string GroupLabel(string key) =>
        GroupKey.TryParse(key, out var parsed) ? $"{GroupKindLabel(parsed.Kind)} : {parsed.Value}" : key;

    /// <summary>Title of the meeting for a meeting group key, otherwise null (used to propose a new task name).</summary>
    public static string? MeetingTitle(string? key) =>
        key is not null && GroupKey.TryParse(key, out var parsed) && parsed.Kind == GroupKind.Meeting ? parsed.Value : null;

    public static string SlotKindLabel(SlotKind kind) => kind switch
    {
        SlotKind.Activity => "Activité",
        SlotKind.Meeting => "Réunion",
        SlotKind.Override => "Manuel",
        SlotKind.ExcludedByOverride => "Pas du travail",
        SlotKind.Ignored => "Ignoré",
        _ => "Vide",
    };

    /// <summary>A slot the user cleared ("Effacer"): no activity nor assignment left, shown as an empty row. Not the same as "Pas du travail", which keeps the activities.</summary>
    public static bool IsRemoved(SlotAssignment slot) => slot.Kind == SlotKind.Removed;

    /// <summary>Whether "Effacer" has something to clear: an assignment, a classification or activities.</summary>
    public static bool HasContent(SlotAssignment slot) =>
        !IsRemoved(slot) && (slot.Kind != SlotKind.Empty || slot.Evidence.Count > 0);

    /// <summary>Task the "Confirmer" action stores for a predicted slot: its task, else its best classified alternative; null when nothing is predicted.</summary>
    public static int? PredictedTask(SlotAssignment slot, IReadOnlyDictionary<int, ForecastTask> tasks)
    {
        if (slot.Kind is not (SlotKind.Activity or SlotKind.Meeting))
        {
            return null;
        }

        bool Assignable(int? id) => id is { } t && tasks.TryGetValue(t, out var task) && !task.IsIgnored;
        if (Assignable(slot.TaskId))
        {
            return slot.TaskId;
        }

        return slot.Alternatives.FirstOrDefault(a => Assignable(a.TaskId))?.TaskId;
    }

    public const int MaxCandidates = 5;

    /// <summary>A work slot predicted by the engine whose winner is not clear enough to trust: the user should pick among candidates.</summary>
    public static bool IsUnclear(SlotAssignment slot) =>
        slot.Kind is SlotKind.Activity or SlotKind.Meeting && slot.Confidence < MediumConfidence;

    /// <summary>The most probable assignable tasks of a slot (winner included), best first, one entry per task; groups without a task and "Ignoré" are left out.</summary>
    public static IReadOnlyList<SlotCandidate> TopCandidates(SlotAssignment slot, IReadOnlyDictionary<int, ForecastTask> tasks, int max = MaxCandidates)
    {
        var all = new List<SlotCandidate>();
        if (slot.TaskId is not null)
            all.Add(new SlotCandidate(slot.TaskId, slot.GroupKey, 0, slot.Confidence));
        all.AddRange(slot.Alternatives);

        return all
            .Where(c => c.TaskId is { } id && tasks.TryGetValue(id, out var task) && !task.IsIgnored)
            .GroupBy(c => c.TaskId!.Value)
            .Select(g => g.OrderByDescending(c => c.Share).First())
            .OrderByDescending(c => c.Share)
            .Take(max)
            .ToList();
    }

    /// <summary>Comment stored with an instant assignment: the meeting title, else the repo, folder or most frequent window title of the slot's activities; null when nothing is known.</summary>
    public static string? SuggestedComment(SlotAssignment slot, IEnumerable<ActivityEvent> activities)
    {
        if (!string.IsNullOrWhiteSpace(slot.Notes))
            return slot.Notes.Trim();
        if (MeetingTitle(slot.GroupKey) is { } meeting)
            return meeting;

        var list = activities.ToList();
        static string? MostFrequent(IEnumerable<string?> values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
                .GroupBy(v => v, StringComparer.CurrentCultureIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        if (MostFrequent(list.Select(a => a.Repo)) is { } repo)
            return $"Travail sur {repo}";
        if (MostFrequent(list.Select(a => a.Folder)) is { } folder)
            return $"Travail dans {folder}";
        return MostFrequent(list.Select(a => a.Title ?? a.Subject));
    }

    public static string RemoveConfirmationTitle(int slotCount) =>
        slotCount == 1 ? "Effacer ce créneau ?" : $"Effacer ces {slotCount} créneaux ?";

    /// <summary>Irreversible-clear warning; the activity count is unknown before clearing, so it is not promised.</summary>
    public static string RemoveConfirmationMessage(int slotCount) =>
        (slotCount == 1 ? "Le contenu de ce créneau de 15 minutes sera effacé" : $"Le contenu de ces {slotCount} créneaux de 15 minutes sera effacé")
        + " : l'assignation est retirée et les activités qui y sont associées sont supprimées définitivement. "
        + "Les activités qui touchent aussi d'autres créneaux sont conservées. "
        + "Cette action est irréversible. Pour garder les activités, utilisez plutôt « Pas du travail ».";

    /// <summary>Outcome of "Effacer": slots cleared, activities erased, and the ones kept because they touch other slots.</summary>
    public static string RemovalResultMessage(SlotRemovalResult result)
    {
        if (result.SlotsRemoved == 0)
            return "Aucun créneau à effacer.";

        var slots = $"{result.SlotsRemoved} {Plural(result.SlotsRemoved, "créneau effacé", "créneaux effacés")}";
        var erased = result.ActivitiesDeleted == 0
            ? "aucune activité effacée"
            : $"{result.ActivitiesDeleted} {Plural(result.ActivitiesDeleted, "activité effacée", "activités effacées")}";
        var text = $"{slots}, {erased}.";
        if (result.ActivitiesKept > 0)
        {
            text += result.ActivitiesKept == 1
                ? " 1 activité conservée car elle touche aussi d'autres créneaux."
                : $" {result.ActivitiesKept} activités conservées car elles touchent aussi d'autres créneaux.";
        }
        return text;
    }

    private static string Plural(int count, string singular, string plural) => count > 1 ? plural : singular;

    /// <summary>True when <paramref name="next"/> starts exactly one slot after <paramref name="previous"/>.</summary>
    public static bool AreAdjacent(DateTime previous, DateTime next) => next - previous == EngineOptions.SlotLength;

    /// <summary>True when the slot starts, in ascending order, follow each other without a gap.</summary>
    public static bool AreConsecutive(IReadOnlyList<DateTime> slotStartsUtc) =>
        slotStartsUtc.Count > 0 && slotStartsUtc.Zip(slotStartsUtc.Skip(1), AreAdjacent).All(adjacent => adjacent);

    /// <summary>Traits of the sources, set at startup from the plugin catalog; the built-in ones until then.</summary>
    public static ISourceCatalog Sources { get; set; } = SourceCatalog.Default;

    public static string SourceLabel(ActivitySource source) => Sources.Get(source).DisplayName;
}
