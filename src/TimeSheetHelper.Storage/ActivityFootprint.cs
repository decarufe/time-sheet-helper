namespace TimeSheetHelper.Storage;

/// <param name="Count">Number of stored activities.</param>
/// <param name="ApproxBytes">Estimated size of those rows in the database.</param>
public sealed record ActivityFootprint(long Count, long ApproxBytes)
{
    /// <summary>Fixed cost of a row (id, dates, source key, index entries) on top of its text columns.</summary>
    public const int RowOverheadBytes = 64;
}
