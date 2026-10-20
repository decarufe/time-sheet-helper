using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>The text of the dialog that guards the merge of a local task into a Forecast one. Pure.</summary>
public static class SubmitDialogs
{
    private const decimal HoursPerSlot = 0.25m;

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
