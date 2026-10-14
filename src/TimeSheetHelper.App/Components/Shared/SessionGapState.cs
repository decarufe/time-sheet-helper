using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>A gap that was in the list and no longer is: settled during this session of the page.</summary>
public sealed record ResolvedGap(string Id, SubmissionGapSeverity Severity, string Title, string Context, DateTime AtUtc);

/// <summary>
/// What the page remembers about the gaps for as long as it is open on one week: the gaps ignored knowingly (session only, the draft is
/// untouched and the fill is not changed) and the gaps settled since the page opened. Nothing is persisted.
/// </summary>
public sealed class SessionGapState(Func<DateTime>? utcNow = null)
{
    public const int MaxResolved = 50;

    private readonly Func<DateTime> now = utcNow ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, string> ignored = [];
    private readonly List<ResolvedGap> resolved = [];
    private Dictionary<string, SubmissionGap> lastLocal = [];
    private Dictionary<string, SubmissionGap>? lastReported;

    /// <summary>Settled gaps, the latest first.</summary>
    public IReadOnlyList<ResolvedGap> Resolved => resolved;

    public int IgnoredCount => ignored.Count;

    /// <summary>The gaps born from the local draft (they exist without a simulation); the others come from Forecast's answer.</summary>
    public static bool IsLocal(SubmissionGapType type) =>
        type is SubmissionGapType.UnclassifiedRemaining or SubmissionGapType.DayBelowTarget or SubmissionGapType.DayAboveTarget;

    /// <summary>The situation a decision to ignore was made on: it no longer holds when the hours or the explanation change.</summary>
    public static string SignatureOf(SubmissionGap gap) => $"{gap.ExpectedHours}|{gap.ForecastHours}|{gap.Message}";

    public void Reset()
    {
        ignored.Clear();
        resolved.Clear();
        lastLocal = [];
        lastReported = null;
    }

    public bool IsIgnored(SubmissionGap gap) =>
        gap.Severity != SubmissionGapSeverity.Blocking && ignored.TryGetValue(gap.Id, out var signature) && signature == SignatureOf(gap);

    /// <summary>Ignores a gap for this session. A blocking gap cannot be ignored (it stays in the way of the fill): false, nothing is recorded, the caller says why.</summary>
    public bool Ignore(SubmissionGap gap)
    {
        if (gap.Severity == SubmissionGapSeverity.Blocking)
            return false;
        ignored[gap.Id] = SignatureOf(gap);
        return true;
    }

    public bool Restore(string gapId) => ignored.Remove(gapId);

    /// <summary>
    /// Takes a new analysis into account: drops the decisions to ignore that no longer hold, and notes the gaps that went away.
    /// Gaps that come from Forecast are compared only between two analyses that both had a simulation (without one they are simply not known).
    /// </summary>
    public void Reconcile(SubmissionAnalysis analysis)
    {
        var current = analysis.Gaps.ToDictionary(g => g.Id);
        var hasSimulation = analysis.Summary.HasSimulation;

        foreach (var (id, signature) in ignored.ToList())
        {
            if (current.TryGetValue(id, out var gap))
            {
                if (gap.Severity == SubmissionGapSeverity.Blocking || SignatureOf(gap) != signature)
                    ignored.Remove(id);
            }
            else if (hasSimulation || lastLocal.ContainsKey(id))
            {
                ignored.Remove(id);
            }
        }

        foreach (var gap in analysis.Gaps)
            resolved.RemoveAll(r => r.Id == gap.Id);

        var local = analysis.Gaps.Where(g => IsLocal(g.Type)).ToDictionary(g => g.Id);
        NoteGone(lastLocal, local);
        lastLocal = local;

        if (hasSimulation)
        {
            var reported = analysis.Gaps.Where(g => !IsLocal(g.Type)).ToDictionary(g => g.Id);
            if (lastReported is not null)
                NoteGone(lastReported, reported);
            lastReported = reported;
        }
    }

    private void NoteGone(Dictionary<string, SubmissionGap> before, Dictionary<string, SubmissionGap> after)
    {
        foreach (var gap in before.Values)
        {
            if (after.ContainsKey(gap.Id) || gap.Severity == SubmissionGapSeverity.Info)
                continue;
            resolved.RemoveAll(r => r.Id == gap.Id);
            resolved.Insert(0, new ResolvedGap(gap.Id, gap.Severity, gap.Title, GapLabels.Context(gap), now()));
        }

        if (resolved.Count > MaxResolved)
            resolved.RemoveRange(MaxResolved, resolved.Count - MaxResolved);
    }
}
