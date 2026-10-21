using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;
using TimeSheetHelper.Tests.Engine;

namespace TimeSheetHelper.Tests.App;

// Exercises the gap rules of NavSummaryService with a scripted gap service: the real local analysis never produces a
// "Gap" badge on a fully classified day, only a Forecast report would.
public class NavSummaryGapRulesTests : IAsyncLifetime
{
    private sealed class ScriptedGapService : ISubmissionGapService
    {
        public List<SubmissionGap> Gaps { get; } = [];
        public int Calls { get; private set; }
        public TimesheetDraft? LastDraft { get; private set; }
        public ForecastFillReport? LastReport { get; private set; }
        public SubmissionDraftContext? LastContext { get; private set; }

        public Task<SubmissionAnalysis> AnalyzeAsync(
            TimesheetDraft draft, ForecastFillReport? report, SubmissionDraftContext? context = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDraft = draft;
            LastReport = report;
            LastContext = context;
            var summary = new SubmissionSummary(0, 0, 0, 0, 0, 0m, null, null, null, 0m, 0m, 0m, false, true, false, [], 0m);
            return Task.FromResult(new SubmissionAnalysis(draft.WeekStart, Gaps, summary));
        }

        public SubmissionGap CreateFailureGap(Exception exception, DateOnly weekStart) => throw new NotSupportedException();
    }

    private readonly ScriptedGapService _gaps = new();
    private EngineTestHost _host = null!;
    private NavSummaryService _sut = null!;
    private ForecastTask _dev = null!;

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost();
        _dev = await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc.");
        _sut = new NavSummaryService(_host.Get<WeekProposalBuilder>(), _gaps, TimeProvider.System);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task Assign(DateOnly day, int fromHour, int toHour) =>
        _host.Get<WeekService>().AssignSlotsAsync(new SlotRange(Fx.Local(day, fromHour), Fx.Local(day, toHour)), _dev.Id);

    private static SubmissionGap Gap(SubmissionGapType type, SubmissionGapSeverity severity, DateOnly? date) => new()
    {
        Id = $"{type}||{date:yyyyMMdd}",
        Type = type,
        Severity = severity,
        Date = date,
        Title = "t",
        Message = "m",
    };

    [Fact]
    public async Task Blocking_gap_on_a_classified_day_makes_the_day_a_gap_and_the_week_to_review()
    {
        await Assign(Fx.Monday, 9, 17);
        _gaps.Gaps.Add(Gap(SubmissionGapType.TaskNotFound, SubmissionGapSeverity.Blocking, Fx.Monday));

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Equal(DayBadge.Gap, summary.Days[0].Badge);
        Assert.Equal(1, summary.Days[0].GapCount);
        Assert.Equal(1, summary.BlockingGapCount);
        Assert.Equal(NavStep.Review, summary.SuggestedStep);
    }

    [Fact]
    public async Task Gap_only_marks_the_day_it_belongs_to()
    {
        await Assign(Fx.Monday, 9, 17);
        await Assign(Fx.Monday.AddDays(1), 9, 17);
        _gaps.Gaps.Add(Gap(SubmissionGapType.TaskNotFound, SubmissionGapSeverity.Blocking, Fx.Monday.AddDays(1)));

        var days = (await _sut.GetAsync(Fx.Monday)).Days;

        Assert.Equal(DayBadge.Ready, days[0].Badge);
        Assert.Equal(DayBadge.Gap, days[1].Badge);
    }

    [Fact]
    public async Task To_verify_gap_counts_for_the_day_and_the_step_but_is_not_blocking()
    {
        await Assign(Fx.Monday, 9, 17);
        _gaps.Gaps.Add(Gap(SubmissionGapType.TaskClosed, SubmissionGapSeverity.ToVerify, Fx.Monday));

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Equal(DayBadge.Gap, summary.Days[0].Badge);
        Assert.Equal(1, summary.Days[0].GapCount);
        Assert.Equal(0, summary.BlockingGapCount);
        Assert.Equal(NavStep.Review, summary.SuggestedStep);
    }

    [Theory]
    [InlineData(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify)]
    [InlineData(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.Blocking)]
    [InlineData(SubmissionGapType.DayAboveTarget, SubmissionGapSeverity.ToVerify)]
    [InlineData(SubmissionGapType.ForecastTimeWithoutProject, SubmissionGapSeverity.Info)]
    [InlineData(SubmissionGapType.ForecastLower, SubmissionGapSeverity.Info)]
    public async Task Daily_objective_and_info_gaps_never_count(SubmissionGapType type, SubmissionGapSeverity severity)
    {
        await Assign(Fx.Monday, 9, 17);
        _gaps.Gaps.Add(Gap(type, severity, Fx.Monday));

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Equal(DayBadge.Ready, summary.Days[0].Badge);
        Assert.Equal(0, summary.Days[0].GapCount);
        Assert.Equal(0, summary.BlockingGapCount);
        Assert.Equal(NavStep.Submit, summary.SuggestedStep);
    }

    [Fact]
    public async Task Blocking_gap_without_a_date_blocks_the_week_without_marking_a_day()
    {
        await Assign(Fx.Monday, 9, 17);
        _gaps.Gaps.Add(Gap(SubmissionGapType.AuthRequired, SubmissionGapSeverity.Blocking, null));

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.All(summary.Days, d => Assert.Equal(0, d.GapCount));
        Assert.Equal(DayBadge.Ready, summary.Days[0].Badge);
        Assert.Equal(1, summary.BlockingGapCount);
        Assert.Equal(NavStep.Review, summary.SuggestedStep);
    }

    [Fact]
    public async Task Unclassified_work_stays_a_draft_even_with_a_gap_on_the_day()
    {
        await _host.AddActivitiesAsync(Fx.Act(
            TimeSheetHelper.Core.Activities.ActivitySource.VsCode, Fx.Local(Fx.Monday, 9), Fx.Local(Fx.Monday, 11),
            a => { a.Repo = "fabrikam/web"; a.Title = "Index.razor - fabrikam/web"; a.ProcessName = "Code"; }));
        await Assign(Fx.Monday.AddDays(1), 9, 17);
        _gaps.Gaps.Add(Gap(SubmissionGapType.UnclassifiedRemaining, SubmissionGapSeverity.Blocking, Fx.Monday));

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Equal(DayBadge.Draft, summary.Days[0].Badge);
        Assert.Equal(1, summary.Days[0].GapCount);
        Assert.Equal(NavStep.Enter, summary.SuggestedStep);
    }

    [Fact]
    public async Task Gaps_are_analysed_locally_from_the_draft_hours_without_a_forecast_report()
    {
        await Assign(Fx.Monday, 9, 17);

        await _sut.GetAsync(Fx.Monday);

        Assert.Equal(1, _gaps.Calls);
        Assert.Null(_gaps.LastReport);
        Assert.Equal(Fx.Monday, _gaps.LastDraft!.WeekStart);
        var entry = Assert.Single(_gaps.LastDraft.Entries);
        Assert.Equal(Fx.Monday, entry.Date);
        Assert.Equal(_dev.Id, entry.ForecastTaskId);
        Assert.Equal(8m, entry.Hours);
        Assert.Equal(Fx.Monday, _gaps.LastContext!.WeekStart);
        Assert.Equal(7, _gaps.LastContext.Days.Count);
    }
}
