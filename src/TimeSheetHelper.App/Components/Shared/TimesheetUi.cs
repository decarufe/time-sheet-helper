using System.Globalization;
using TimeSheetHelper.Core.Activities;
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

    public static string SlotKindLabel(SlotKind kind) => kind switch
    {
        SlotKind.Activity => "Activité",
        SlotKind.Meeting => "Réunion",
        SlotKind.Override => "Manuel",
        SlotKind.ExcludedByOverride => "Pas du travail",
        SlotKind.Ignored => "Ignoré",
        _ => "Vide",
    };

    /// <summary>Traits of the sources, set at startup from the plugin catalog; the built-in ones until then.</summary>
    public static ISourceCatalog Sources { get; set; } = SourceCatalog.Default;

    public static string SourceLabel(ActivitySource source) => Sources.Get(source).DisplayName;
}
