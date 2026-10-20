using System.Collections.Concurrent;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Services;

/// <summary>Badge of a day in the context panel and the day strip.</summary>
public enum DayBadge
{
    /// <summary>« Non saisi » : no classified and no unclassified hours.</summary>
    Empty,

    /// <summary>« Brouillon » : some work has no task yet.</summary>
    Draft,

    /// <summary>« Écart » : a gap to look at (never the daily objective, see D4).</summary>
    Gap,

    /// <summary>« Prêt » : classified hours, nothing to look at.</summary>
    Ready,

    /// <summary>« Envoyé » : the week was submitted and the day holds hours.</summary>
    Sent,

    /// <summary>« Modifié depuis l'envoi » : phase 2 only.</summary>
    Modified,
}

/// <summary>Step of the Saisir → Vérifier → Soumettre stepper.</summary>
public enum NavStep { Enter, Review, Submit }

/// <param name="TargetHours">The daily objective (<c>Engine:TargetHoursPerDay</c>, none on the weekend): a reference, never blocking.</param>
/// <param name="GapCount">Gaps of the day to look at; the "below/above the daily objective" information is not counted (D4).</param>
public sealed record DaySummary(
    DateOnly Date,
    decimal ClassifiedHours,
    decimal TargetHours,
    decimal UnclassifiedHours,
    int GapCount,
    DayFlag Flag,
    DayBadge Badge,
    bool IsWeekend);

/// <param name="Days">Always seven days, Monday to Sunday.</param>
/// <param name="WeeklyTargetHours">Sum of the daily objectives (40 h by default): the objective that counts (D4).</param>
public sealed record NavSummary(
    DateOnly WeekStart,
    IReadOnlyList<DaySummary> Days,
    decimal ClassifiedHours,
    decimal WeeklyTargetHours,
    int BlockingGapCount,
    NavStep SuggestedStep,
    bool IsSubmitted);

public interface INavSummaryService
{
    /// <summary>Read only: nothing is written and Forecast is never called. Short cache per week.</summary>
    Task<NavSummary> GetAsync(DateOnly weekStart, CancellationToken cancellationToken = default);

    /// <summary>Forgets the cached summary of a week (after an edit in the day, week or submit page).</summary>
    void Invalidate(DateOnly weekStart);
}

/// <summary>
/// Summary of a week for the navigation shell, built from the engine's week proposal and the local gaps of
/// <see cref="SubmissionReviewService"/> (no Forecast report). Singleton: summaries are cached for <see cref="CacheLifetime"/>.
/// </summary>
public sealed class NavSummaryService(
    WeekProposalBuilder builder,
    ISubmissionGapService gaps,
    TimeProvider timeProvider) : INavSummaryService
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<DateOnly, (NavSummary Summary, DateTimeOffset ExpiresAt)> _cache = new();

    public async Task<NavSummary> GetAsync(DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        var monday = WeekProposalBuilder.WeekStartOf(weekStart);
        if (_cache.TryGetValue(monday, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow())
            return cached.Summary;

        // Cache-only classification: the shell must never call Claude nor write the classification cache.
        var proposal = await builder.BuildAsync(monday, classifyMissing: false, cancellationToken);
        var analysis = await gaps.AnalyzeAsync(DraftOf(proposal), report: null, SubmissionReviewService.ContextOf(proposal), cancellationToken);
        var summary = Summarize(proposal, analysis);

        _cache[monday] = (summary, timeProvider.GetUtcNow() + CacheLifetime);
        return summary;
    }

    public void Invalidate(DateOnly weekStart) => _cache.TryRemove(WeekProposalBuilder.WeekStartOf(weekStart), out _);

    /// <summary>In-memory draft of the proposal (never saved): what the gap analysis reads is the hours of each day.</summary>
    private static TimesheetDraft DraftOf(WeekProposal proposal) => new()
    {
        WeekStart = proposal.WeekStart,
        Entries = proposal.Days
            .SelectMany(day => day.Tasks.Select(task => new TimesheetEntry
            {
                Date = day.Date,
                ForecastTaskId = task.ForecastTaskId,
                Hours = task.Hours,
            }))
            .ToList(),
    };

    private static NavSummary Summarize(WeekProposal proposal, SubmissionAnalysis analysis)
    {
        // The daily objective is information only (D4): it never makes a day a gap nor blocks anything.
        var gaps = analysis.Gaps
            .Where(g => g.Severity != SubmissionGapSeverity.Info
                && g.Type is not (SubmissionGapType.DayBelowTarget or SubmissionGapType.DayAboveTarget))
            .ToList();

        var days = proposal.Days
            .Select(day => SummarizeDay(day, gaps.Count(g => g.Date == day.Date), proposal.IsSubmitted))
            .ToList();

        var classified = days.Sum(d => d.ClassifiedHours);
        var step = proposal.UnclassifiedHours > 0 || classified == 0
            ? NavStep.Enter
            : gaps.Count > 0 ? NavStep.Review : NavStep.Submit;

        return new NavSummary(
            proposal.WeekStart,
            days,
            classified,
            days.Sum(d => d.TargetHours),
            gaps.Count(g => g.Severity == SubmissionGapSeverity.Blocking),
            step,
            proposal.IsSubmitted);
    }

    private static DaySummary SummarizeDay(DayProposal day, int gapCount, bool weekSubmitted)
    {
        DayBadge badge;
        if (weekSubmitted && day.TotalHours > 0)
            badge = DayBadge.Sent;
        else if (day.TotalHours == 0 && day.UnclassifiedHours == 0)
            badge = DayBadge.Empty;
        else if (day.UnclassifiedHours > 0)
            badge = DayBadge.Draft;
        else if (gapCount > 0)
            badge = DayBadge.Gap;
        else
            badge = DayBadge.Ready;

        return new DaySummary(
            day.Date,
            day.TotalHours,
            day.TargetHours,
            day.UnclassifiedHours,
            gapCount,
            day.Flag,
            badge,
            day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
