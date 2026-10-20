using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Forecast.Submission;
using TimeSheetHelper.Tests.Engine;

namespace TimeSheetHelper.Tests.App;

public class NavSummaryServiceTests : IAsyncLifetime
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateOnly Tuesday = Fx.Monday.AddDays(1);
    private static readonly DateOnly Saturday = Fx.Monday.AddDays(5);

    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero));
    private EngineTestHost _host = null!;
    private WeekService _weeks = null!;
    private NavSummaryService _sut = null!;
    private ForecastTask _dev = null!;

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost();
        _weeks = _host.Get<WeekService>();
        _dev = await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc.");
        await _host.AddRuleAsync(RuleField.Repo, RuleOperator.Equals, "contoso/api", _dev.Id);
        _sut = new NavSummaryService(_host.Get<WeekProposalBuilder>(), new SubmissionGapService(), _clock);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task Assign(DateOnly day, int fromHour, int toHour) =>
        _weeks.AssignSlotsAsync(new SlotRange(Fx.Local(day, fromHour), Fx.Local(day, toHour)), _dev.Id);

    private static ActivityEvent Unclassified(DateOnly day, int fromHour, int toHour) =>
        Fx.Act(ActivitySource.VsCode, Fx.Local(day, fromHour), Fx.Local(day, toHour),
            a => { a.Repo = "fabrikam/web"; a.Title = "Index.razor - fabrikam/web"; a.ProcessName = "Code"; });

    [Fact]
    public async Task Empty_week_has_seven_empty_days_and_asks_to_enter_time()
    {
        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Equal(Fx.Monday, summary.WeekStart);
        Assert.Equal(7, summary.Days.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(Fx.Monday.AddDays), summary.Days.Select(d => d.Date));
        Assert.All(summary.Days, d =>
        {
            Assert.Equal(DayBadge.Empty, d.Badge);
            Assert.Equal(0, d.GapCount);
            Assert.Equal(0m, d.ClassifiedHours);
        });
        Assert.Equal(0m, summary.ClassifiedHours);
        Assert.Equal(40m, summary.WeeklyTargetHours);
        Assert.Equal(0, summary.BlockingGapCount);
        Assert.Equal(NavStep.Enter, summary.SuggestedStep);
        Assert.False(summary.IsSubmitted);
    }

    [Fact]
    public async Task Partial_day_is_ready_because_the_daily_objective_never_blocks()
    {
        await Assign(Fx.Monday, 9, 10);

        var monday = (await _sut.GetAsync(Fx.Monday)).Days[0];

        Assert.Equal(1m, monday.ClassifiedHours);
        Assert.Equal(8m, monday.TargetHours);
        Assert.Equal(DayFlag.UnderTarget, monday.Flag);
        Assert.Equal(0, monday.GapCount);
        Assert.Equal(DayBadge.Ready, monday.Badge);
    }

    [Fact]
    public async Task Full_day_is_ready_and_the_week_moves_on_to_submit()
    {
        await Assign(Fx.Monday, 9, 17);

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Equal(8m, summary.Days[0].ClassifiedHours);
        Assert.Equal(DayFlag.Ok, summary.Days[0].Flag);
        Assert.Equal(DayBadge.Ready, summary.Days[0].Badge);
        Assert.Equal(8m, summary.ClassifiedHours);
        Assert.Equal(NavStep.Submit, summary.SuggestedStep);
    }

    [Fact]
    public async Task Day_with_unclassified_work_is_a_draft_with_a_gap_and_the_week_asks_to_enter()
    {
        await Assign(Fx.Monday, 9, 17);
        await _host.AddActivitiesAsync(Unclassified(Tuesday, 9, 11));

        var summary = await _sut.GetAsync(Fx.Monday);

        var tuesday = summary.Days[1];
        Assert.Equal(2m, tuesday.UnclassifiedHours);
        Assert.Equal(1, tuesday.GapCount);
        Assert.Equal(DayBadge.Draft, tuesday.Badge);
        Assert.Equal(DayBadge.Ready, summary.Days[0].Badge);
        Assert.Equal(NavStep.Enter, summary.SuggestedStep);
        Assert.Equal(0, summary.BlockingGapCount);
    }

    [Fact]
    public async Task Summary_never_calls_the_classifier_nor_writes_the_classification_cache()
    {
        await _host.AddActivitiesAsync(Unclassified(Tuesday, 9, 11));
        _host.Classifier.Answer = _ => throw new InvalidOperationException("classifier must not be called");

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.Empty(_host.Classifier.Calls);
        await using var db = _host.Db();
        Assert.Empty(db.ClassificationCache);
        Assert.Equal(DayBadge.Draft, summary.Days[1].Badge);
    }

    [Fact]
    public async Task Weekend_day_has_no_objective_and_counts_its_hours()
    {
        await Assign(Saturday, 10, 12);

        var summary = await _sut.GetAsync(Fx.Monday);

        var saturday = summary.Days[5];
        Assert.True(saturday.IsWeekend);
        Assert.True(summary.Days[6].IsWeekend);
        Assert.False(summary.Days[0].IsWeekend);
        Assert.Equal(0m, saturday.TargetHours);
        Assert.Equal(2m, saturday.ClassifiedHours);
        Assert.Equal(DayBadge.Ready, saturday.Badge);
        Assert.Equal(DayBadge.Empty, summary.Days[6].Badge);
        Assert.Equal(40m, summary.WeeklyTargetHours);
        Assert.Equal(2m, summary.ClassifiedHours);
    }

    [Fact]
    public async Task Submitted_week_marks_days_with_hours_as_sent()
    {
        await Assign(Fx.Monday, 9, 17);
        await using (var db = _host.Db())
        {
            db.TimesheetDrafts.Add(new TimesheetDraft { WeekStart = Fx.Monday, SubmittedUtc = Fx.Clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }

        var summary = await _sut.GetAsync(Fx.Monday);

        Assert.True(summary.IsSubmitted);
        Assert.Equal(DayBadge.Sent, summary.Days[0].Badge);
        Assert.Equal(DayBadge.Empty, summary.Days[1].Badge);
    }

    [Fact]
    public async Task Any_day_of_the_week_gives_the_same_summary()
    {
        var summary = await _sut.GetAsync(Fx.Monday.AddDays(3));

        Assert.Equal(Fx.Monday, summary.WeekStart);
    }

    [Fact]
    public async Task Summary_is_cached_until_it_expires_or_is_invalidated()
    {
        var first = await _sut.GetAsync(Fx.Monday);
        await Assign(Fx.Monday, 9, 10);

        Assert.Same(first, await _sut.GetAsync(Fx.Monday));
        Assert.Equal(0m, (await _sut.GetAsync(Fx.Monday)).ClassifiedHours);

        _sut.Invalidate(Fx.Monday.AddDays(2));
        Assert.Equal(1m, (await _sut.GetAsync(Fx.Monday)).ClassifiedHours);

        await Assign(Fx.Monday, 10, 11);
        Assert.Equal(1m, (await _sut.GetAsync(Fx.Monday)).ClassifiedHours);

        _clock.Now += NavSummaryService.CacheLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(2m, (await _sut.GetAsync(Fx.Monday)).ClassifiedHours);
    }

    [Fact]
    public async Task Summary_is_read_only()
    {
        await Assign(Fx.Monday, 9, 17);
        await using (var db = _host.Db())
            Assert.Empty(db.TimesheetDrafts);

        await _sut.GetAsync(Fx.Monday);

        await using var after = _host.Db();
        Assert.Empty(after.TimesheetDrafts);
    }
}
