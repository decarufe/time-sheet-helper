namespace TimeSheetHelper.Engine.Allocation;

/// <summary>Helpers for slots the user deleted (see <see cref="TimeSheetHelper.Core.Timesheets.RemovedSlot"/>).</summary>
internal static class RemovedSlots
{
    /// <summary>Starts of the slots a window touches; a point (empty window) touches the slot holding it.</summary>
    public static IEnumerable<DateTime> Touched(Interval window)
    {
        var ticks = EngineOptions.SlotLength.Ticks;
        var slot = new DateTime(window.Start.Ticks / ticks * ticks, DateTimeKind.Utc);
        yield return slot;
        for (slot += EngineOptions.SlotLength; slot < window.End; slot += EngineOptions.SlotLength)
            yield return slot;
    }

    /// <summary>True when every slot the window touches was deleted: the activity no longer counts anywhere.</summary>
    public static bool CoversOnly(Interval window, IReadOnlySet<DateTime> removed) =>
        removed.Count > 0 && Touched(window).All(removed.Contains);
}
