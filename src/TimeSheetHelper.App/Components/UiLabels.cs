using System.Globalization;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components;

/// <summary>French labels shared by the Rules, Sources and Submit pages.</summary>
public static class UiLabels
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-CA");

    public static string Field(RuleField field) => field switch
    {
        RuleField.Source => "Source",
        RuleField.ProcessName => "Application (processus)",
        RuleField.Title => "Titre de fenêtre",
        RuleField.FilePath => "Chemin du fichier",
        RuleField.Folder => "Dossier",
        RuleField.Url => "URL",
        RuleField.Repo => "Dépôt",
        RuleField.Project => "Projet / organisation",
        RuleField.Subject => "Sujet",
        RuleField.Participants => "Participants",
        _ => field.ToString(),
    };

    public static string Operator(RuleOperator op) => op switch
    {
        RuleOperator.Equals => "est égal à",
        RuleOperator.StartsWith => "commence par",
        RuleOperator.Contains => "contient",
        RuleOperator.Regex => "correspond à l'expression régulière",
        _ => op.ToString(),
    };

    public static string Origin(string? origin) => origin switch
    {
        "user" => "Utilisateur",
        "correction" => "Correction",
        "claude" => "Claude",
        null or "" => "—",
        _ => origin,
    };

    public static string Collector(string name) => name.ToLowerInvariant() switch
    {
        "github" => "GitHub",
        "azuredevops" => "Azure DevOps",
        "localgit" => "Git local",
        "calendar" => "Calendrier Outlook",
        "email" => "Courriels envoyés",
        "teams" => "Microsoft Teams",
        "claudecode" => "Sessions Claude Code",
        _ => name,
    };

    public static string CellStatus(ForecastCellStatus status) => status switch
    {
        ForecastCellStatus.Planned => "Prévu",
        ForecastCellStatus.Ok => "OK",
        ForecastCellStatus.Mismatch => "Écart",
        ForecastCellStatus.RowNotFound => "Tâche introuvable",
        ForecastCellStatus.Error => "Erreur",
        _ => status.ToString(),
    };

    public static string Hours(decimal hours) => hours.ToString("0.##", French) + " h";

    public static string Day(DateOnly date) => date.ToString("ddd d MMM", French);

    public static string LocalDateTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("ddd d MMM HH:mm", French);

    /// <summary>"il y a 3 min", "il y a 2 h", or the date when older than a day.</summary>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var elapsed = nowUtc - utc;
        if (elapsed < TimeSpan.FromMinutes(1))
            return "à l'instant";
        if (elapsed < TimeSpan.FromHours(1))
            return $"il y a {(int)elapsed.TotalMinutes} min";
        if (elapsed < TimeSpan.FromDays(1))
            return $"il y a {(int)elapsed.TotalHours} h";
        return "le " + LocalDateTime(utc);
    }

    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static string WeekTitle(DateOnly monday) =>
        $"Semaine du {monday.ToString("d MMMM", French)} au {monday.AddDays(6).ToString("d MMMM yyyy", French)}";
}
