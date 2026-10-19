using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>Simple filter of the list of gaps: a severity (null = all). The days shown are those checked in the draft, not a filter.</summary>
public sealed record GapFilter(SubmissionGapSeverity? Severity = null)
{
    public static GapFilter None { get; } = new();

    public bool IsActive => Severity is not null;

    public bool Matches(SubmissionGap gap) => Severity is null || gap.Severity == Severity;

    public static DateOnly? DayOf(SubmissionGap gap) => gap.Cell?.Date ?? gap.Date;
}

/// <summary>The gaps of one type inside a severity.</summary>
public sealed record GapGroup(SubmissionGapSeverity Severity, SubmissionGapType Type, IReadOnlyList<SubmissionGap> Gaps)
{
    public string Heading => GapLabels.TypeHeading(Type);
}

/// <summary>The groups of one severity.</summary>
public sealed record GapSection(SubmissionGapSeverity Severity, IReadOnlyList<GapGroup> Groups)
{
    public int Count => Groups.Sum(g => g.Gaps.Count);
}

/// <summary>
/// The list "Écarts à régler": the open gaps grouped by severity (blocking first) then by type, filtered, with the gaps ignored for the
/// session set apart. Pure: no Blazor, no Forecast.
/// </summary>
public sealed class GapBoard
{
    private static readonly SubmissionGapSeverity[] SeverityOrder =
        [SubmissionGapSeverity.Blocking, SubmissionGapSeverity.ToVerify, SubmissionGapSeverity.Info];

    private readonly List<SubmissionGap> flat;

    private GapBoard(
        IReadOnlyList<GapSection> sections, IReadOnlyList<SubmissionGap> ignored, List<SubmissionGap> flat, List<SubmissionGap> open)
    {
        Sections = sections;
        Ignored = ignored;
        this.flat = flat;
        OpenBlockingCount = open.Count(g => g.Severity == SubmissionGapSeverity.Blocking);
        OpenToVerify = open.Where(g => g.Severity == SubmissionGapSeverity.ToVerify).ToList();
        OpenInfoCount = open.Count(g => g.Severity == SubmissionGapSeverity.Info);
        FirstBlocking = open.FirstOrDefault(g => g.Severity == SubmissionGapSeverity.Blocking);
    }

    /// <summary>The open gaps that match the filter; empty sections are left out.</summary>
    public IReadOnlyList<GapSection> Sections { get; }

    /// <summary>Gaps ignored for the session that match the filter.</summary>
    public IReadOnlyList<SubmissionGap> Ignored { get; }

    /// <summary>Open gaps (ignored ones excluded) before the filter, per severity.</summary>
    public int OpenBlockingCount { get; }

    public int OpenToVerifyCount => OpenToVerify.Count;

    /// <summary>Open gaps to verify (ignored ones excluded), whatever the filter: the fill confirmation lists them.</summary>
    public IReadOnlyList<SubmissionGap> OpenToVerify { get; }

    public int OpenInfoCount { get; }

    public int OpenCount => OpenBlockingCount + OpenToVerifyCount + OpenInfoCount;

    /// <summary>The open gaps that match the filter, in display order.</summary>
    public IReadOnlyList<SubmissionGap> Visible => flat;

    /// <summary>
    /// Whether a gap concerns the days checked for the simulation and the fill. <paramref name="days"/> null: every day. A gap without a
    /// day concerns the whole week (auth, global error...): it is never left out.
    /// </summary>
    public static bool InScope(SubmissionGap gap, IReadOnlyCollection<DateOnly>? days) =>
        days is null || GapFilter.DayOf(gap) is not { } day || days.Contains(day);

    /// <param name="days">
    /// The days checked to simulate and fill (null: all). Gaps of the other days are out of the board altogether, counts included: they
    /// cannot block a fill that never touches those days.
    /// </param>
    public static GapBoard Build(
        IEnumerable<SubmissionGap> gaps, SessionGapState session, GapFilter? filter = null, IReadOnlyCollection<DateOnly>? days = null)
    {
        filter ??= GapFilter.None;
        var all = gaps.Where(g => InScope(g, days)).ToList();
        var open = all.Where(g => !session.IsIgnored(g)).ToList();
        var visible = open.Where(filter.Matches).ToList();

        var sections = new List<GapSection>();
        var flat = new List<SubmissionGap>();
        foreach (var severity in SeverityOrder)
        {
            var groups = visible.Where(g => g.Severity == severity)
                .GroupBy(g => g.Type)
                .OrderBy(g => g.Key)
                .Select(g => new GapGroup(severity, g.Key, g.OrderBy(x => FilterDay(x)).ThenBy(x => x.Id, StringComparer.Ordinal).ToList()))
                .ToList();
            if (groups.Count == 0)
                continue;
            sections.Add(new GapSection(severity, groups));
            flat.AddRange(groups.SelectMany(g => g.Gaps));
        }

        return new GapBoard(
            sections,
            all.Where(session.IsIgnored).Where(filter.Matches).ToList(),
            flat,
            open);
    }

    /// <summary>The id of the gap after <paramref name="currentId"/> in display order, looping back to the first; null when the list is empty.</summary>
    public string? NextId(string? currentId)
    {
        if (flat.Count == 0)
            return null;
        var index = currentId is null ? -1 : flat.FindIndex(g => g.Id == currentId);
        return flat[(index + 1) % flat.Count].Id;
    }

    /// <summary>The gap after the one that was just settled: the one now at its place, else the last one; null when none is left.</summary>
    public string? FocusAfterRemoval(IReadOnlyList<string> previousOrder, string removedId)
    {
        if (flat.Count == 0)
            return null;
        var position = previousOrder.ToList().IndexOf(removedId);
        var remaining = previousOrder.Where(id => flat.Any(g => g.Id == id)).ToList();
        if (position < 0)
            return flat[0].Id;
        return previousOrder.Skip(position + 1).FirstOrDefault(id => flat.Any(g => g.Id == id))
               ?? remaining.LastOrDefault()
               ?? flat[0].Id;
    }

    public SubmissionGap? Find(string id) => flat.FirstOrDefault(g => g.Id == id) ?? Ignored.FirstOrDefault(g => g.Id == id);

    /// <summary>The first open blocking gap, whatever the filter (the status bar links to it).</summary>
    public SubmissionGap? FirstBlocking { get; }

    private static DateOnly FilterDay(SubmissionGap gap) => GapFilter.DayOf(gap) ?? DateOnly.MaxValue;

    /// <summary>The cells of a report that no gap talks about: ready, or already in Forecast.</summary>
    public static IReadOnlyList<ForecastCellResult> CellsWithoutGap(ForecastFillReport? report, IEnumerable<SubmissionGap> gaps)
    {
        if (report is null)
            return [];
        var withGap = gaps
            .Where(g => g.Cell is not null)
            .SelectMany(g => g.RelatedCells.Prepend(g.Cell!))
            .Select(c => (c.ForecastTaskId, c.Date))
            .ToHashSet();
        return report.Cells.Where(c => !withGap.Contains((c.ForecastTaskId, c.Date)) && SubmissionGapService.HasNothingToReport(c)).ToList();
    }
}
