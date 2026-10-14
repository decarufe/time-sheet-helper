using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Services;

/// <summary>
/// What the Submit page asks to know where the week stands: the engine's week proposal (daily objective, unclassified work, work set
/// aside) and the result of the simulation are handed to <see cref="ISubmissionGapService"/>. No Blazor, no Forecast call.
/// </summary>
public sealed class SubmissionReviewService(WeekProposalBuilder builder, ISubmissionGapService gaps)
{
    private const decimal HoursPerSlot = 0.25m;

    /// <summary>
    /// The gaps and summary of the week. <paramref name="report"/> is the last simulation or fill (null before the first one).
    /// Classifier verdicts come from the cache only: this never calls Claude.
    /// </summary>
    public async Task<SubmissionAnalysis> AnalyzeAsync(TimesheetDraft draft, ForecastFillReport? report, CancellationToken cancellationToken = default)
    {
        var proposal = await builder.BuildAsync(draft.WeekStart, classifyMissing: false, cancellationToken);
        return await gaps.AnalyzeAsync(draft, report, ContextOf(proposal), cancellationToken);
    }

    /// <summary>The blocking gap of an operation that threw (not signed in, Forecast unreachable...).</summary>
    public SubmissionGap CreateFailureGap(Exception exception, DateOnly weekStart) => gaps.CreateFailureGap(exception, weekStart);

    /// <summary>
    /// The daily objective is the engine's (<c>Engine:TargetHoursPerDay</c>, 8 h by default, none on the weekend); the work set aside is
    /// what the user marked "not work" or gave to the ignored task.
    /// </summary>
    public static SubmissionDraftContext ContextOf(WeekProposal proposal) =>
        new(proposal.WeekStart, proposal.Days.Select(day => new SubmissionDayContext(
            day.Date,
            day.TargetHours,
            day.UnclassifiedHours,
            proposal.Slots.TryGetValue(day.Date, out var slots)
                ? slots.Count(s => s.Kind is SlotKind.ExcludedByOverride or SlotKind.Ignored) * HoursPerSlot
                : 0m)).ToList());
}
