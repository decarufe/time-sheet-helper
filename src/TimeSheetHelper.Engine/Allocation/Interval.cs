namespace TimeSheetHelper.Engine.Allocation;

/// <summary>Half-open UTC interval [Start, End).</summary>
public readonly record struct Interval(DateTime Start, DateTime End)
{
    public TimeSpan Length => End > Start ? End - Start : TimeSpan.Zero;
    public bool IsEmpty => End <= Start;

    public Interval Clip(Interval bounds) =>
        new(Start < bounds.Start ? bounds.Start : Start, End > bounds.End ? bounds.End : End);

    public bool Overlaps(Interval other) => Start < other.End && other.Start < End;

    /// <summary>Sorted, non-overlapping union of the given intervals.</summary>
    public static List<Interval> Merge(IEnumerable<Interval> intervals)
    {
        var merged = new List<Interval>();
        foreach (var i in intervals.Where(i => !i.IsEmpty).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && i.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { End = i.End > last.End ? i.End : last.End };
            }
            else
            {
                merged.Add(i);
            }
        }
        return merged;
    }

    /// <summary>Parts of <paramref name="source"/> not covered by <paramref name="sortedMasks"/> (output of <see cref="Merge"/>).</summary>
    public static List<Interval> Subtract(Interval source, IReadOnlyList<Interval> sortedMasks)
    {
        var parts = new List<Interval>();
        var cursor = source.Start;
        foreach (var mask in sortedMasks)
        {
            if (mask.End <= cursor)
                continue;
            if (mask.Start >= source.End)
                break;
            if (mask.Start > cursor)
                parts.Add(new(cursor, mask.Start));
            if (mask.End > cursor)
                cursor = mask.End;
            if (cursor >= source.End)
                break;
        }
        if (cursor < source.End)
            parts.Add(new(cursor, source.End));
        return parts;
    }

    public static TimeSpan TotalLength(IEnumerable<Interval> intervals) =>
        Merge(intervals).Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Length);
}
