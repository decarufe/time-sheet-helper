using System.Globalization;
using System.Text;
using TimeSheetHelper.App.Updates;

namespace TimeSheetHelper.App.About;

/// <summary>French wording of the "À propos" surfaces (web page and tray dialog).</summary>
public static class AboutFormat
{
    public const string AppName = "Time Sheet Helper";
    public const string License = "MIT";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-CA");
    private static readonly string[] Units = ["o", "Ko", "Mo", "Go", "To"];

    /// <summary>Retention choices of the activity cleanup; null days = everything.</summary>
    public static IReadOnlyList<(int? Days, string Label)> ClearChoices { get; } =
    [
        (90, "Effacer plus de 90 jours"),
        (30, "Effacer plus de 30 jours"),
        (null, "Tout effacer"),
    ];

    public static string RepositoryUrl(string repository) => $"https://github.com/{repository.Trim().Trim('/')}";

    public static string Size(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} {Units[0]}"
            : $"{value.ToString(value >= 100 ? "0" : "0.#", French)} {Units[unit]}";
    }

    public static string ActivityCount(long count) =>
        count switch
        {
            0 => "Aucune activité",
            1 => "1 activité",
            _ => $"{count.ToString("N0", French)} activités",
        };

    public static string UpdateStatus(UpdatePhase phase, AppVersion? available, string? error) =>
        phase switch
        {
            UpdatePhase.Checking => "Recherche de mise à jour…",
            UpdatePhase.Downloading => "Téléchargement de la mise à jour…",
            UpdatePhase.Installing => "Installation de la mise à jour…",
            UpdatePhase.Failed when available is null => error ?? "Impossible de vérifier les mises à jour.",
            _ when available is not null => $"La version {available} est disponible.",
            _ => "Vous utilisez la dernière version.",
        };

    public static string ClearConfirmation(int? days) =>
        days is { } d
            ? $"Les activités brutes de plus de {d} jours seront supprimées. Cette action est irréversible."
            : "Toutes les activités brutes seront supprimées. Cette action est irréversible.";

    public static string ClearNote =>
        "Les affectations manuelles, les règles et les soumissions sont conservées. "
        + "Les sources synchronisées (Azure DevOps, Microsoft 365) peuvent réimporter leur activité à la prochaine synchronisation.";

    public static string ClearResult(int removed) =>
        removed switch
        {
            0 => "Aucune activité à effacer.",
            1 => "1 activité effacée.",
            _ => $"{removed.ToString("N0", French)} activités effacées.",
        };

    /// <summary>Plain-text body of the tray dialog.</summary>
    public static string Summary(AboutInfo info, string updateStatus)
    {
        var text = new StringBuilder();
        text.AppendLine(AppName);
        text.AppendLine($"Version : {info.Version}");
        text.AppendLine($"Mise à jour : {updateStatus}");
        text.AppendLine($".NET : {info.RuntimeVersion}");
        text.AppendLine($"Licence : {info.License}");
        text.AppendLine($"Dépôt : {info.RepositoryUrl}");
        text.AppendLine();
        text.AppendLine($"Dossier des données : {info.DataFolder}");
        text.AppendLine($"Activités : {ActivityCount(info.ActivityCount)}{(info.ActivityCount > 0 ? $" ({Size(info.ActivityBytes)})" : "")}");
        text.Append($"Base de données : {Size(info.DatabaseBytes)}");
        return text.ToString();
    }
}
