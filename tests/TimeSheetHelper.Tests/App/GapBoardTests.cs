using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

public sealed class GapBoardTests
{
    private static GapBoard Build(IEnumerable<SubmissionGap> gaps, SessionGapState? session = null, GapFilter? filter = null) =>
        GapBoard.Build(gaps, session ?? new SessionGapState(), filter);

    [Fact]
    public void Gaps_are_grouped_by_severity_blocking_first_then_by_type()
    {
        var info = GapFx.Gap(SubmissionGapType.ForecastTimeWithoutProject, SubmissionGapSeverity.Info, taskId: null);
        var verify = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null);
        var locked = GapFx.Gap(SubmissionGapType.CellLocked, SubmissionGapSeverity.Blocking);
        var notFound = GapFx.Gap(SubmissionGapType.TaskNotFound, SubmissionGapSeverity.Blocking, taskId: 2);

        var board = Build([info, verify, locked, notFound]);

        Assert.Equal(
            [SubmissionGapSeverity.Blocking, SubmissionGapSeverity.ToVerify, SubmissionGapSeverity.Info],
            board.Sections.Select(s => s.Severity));
        // Inside a severity the groups follow the declaration order of the types.
        Assert.Equal(
            [SubmissionGapType.CellLocked, SubmissionGapType.TaskNotFound],
            board.Sections[0].Groups.Select(g => g.Type));
        Assert.Equal([locked.Id, notFound.Id, verify.Id, info.Id], board.Visible.Select(g => g.Id));
    }

    [Fact]
    public void Gaps_of_a_group_are_ordered_by_day_then_id_and_gaps_without_day_come_last()
    {
        var late = GapFx.Gap(taskId: 1, day: 3);
        var early = GapFx.Gap(taskId: 2, day: 1);
        var sameDayLowerId = GapFx.Gap(taskId: 1, day: 1);
        var noDay = GapFx.Gap(taskId: null) with { Date = null };

        var board = Build([late, noDay, early, sameDayLowerId]);

        Assert.Equal([sameDayLowerId.Id, early.Id, late.Id, noDay.Id], board.Sections[0].Groups[0].Gaps.Select(g => g.Id));
        Assert.Equal(4, board.Sections[0].Count);
    }

    [Fact]
    public void A_group_heading_is_the_french_label_of_its_type()
    {
        var board = Build([GapFx.Gap(SubmissionGapType.TaskNotFound)]);

        Assert.Equal(GapLabels.TypeHeading(SubmissionGapType.TaskNotFound), board.Sections[0].Groups[0].Heading);
        Assert.Equal("Tâches introuvables dans Forecast", board.Sections[0].Groups[0].Heading);
    }

    [Fact]
    public void The_severity_filter_hides_the_other_gaps_but_the_open_counts_stay_complete()
    {
        var blocking = GapFx.Gap(SubmissionGapType.TaskNotFound);
        var verify = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null);

        var board = Build([blocking, verify], filter: new GapFilter(Severity: SubmissionGapSeverity.ToVerify));

        Assert.Equal([verify.Id], board.Visible.Select(g => g.Id));
        Assert.Single(board.Sections);
        Assert.Equal(1, board.OpenBlockingCount);
        Assert.Equal(1, board.OpenToVerifyCount);
        Assert.Equal(2, board.OpenCount);
    }

    [Fact]
    public void The_gaps_follow_the_checked_days_by_the_day_of_the_cell_or_of_the_gap_and_undated_gaps_always_stay()
    {
        var onCell = GapFx.Gap(taskId: 1, day: 2);
        var onDay = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 2);
        var other = GapFx.Gap(taskId: 3, day: 4);
        var noDay = GapFx.Gap(SubmissionGapType.TaskNotFound, taskId: null) with { Date = null };
        IEnumerable<SubmissionGap> all = [onCell, onDay, other, noDay];

        var onlyWednesday = GapBoard.Build(all, new SessionGapState(), days: [GapFx.Day(2)]);
        var nothingChecked = GapBoard.Build(all, new SessionGapState(), days: []);
        var everything = GapBoard.Build(all, new SessionGapState(), days: null);

        Assert.Equal(new[] { onCell.Id, onDay.Id, noDay.Id }.Order(), onlyWednesday.Visible.Select(g => g.Id).Order());
        Assert.Equal([noDay.Id], nothingChecked.Visible.Select(g => g.Id));
        Assert.Equal(4, everything.Visible.Count);
        Assert.True(new GapFilter(SubmissionGapSeverity.Blocking).IsActive);
        Assert.False(GapFilter.None.IsActive);
    }

    [Fact]
    public void Ignored_gaps_are_set_apart_and_no_longer_count_as_open()
    {
        var verify = GapFx.Gap(SubmissionGapType.UnclassifiedRemaining, SubmissionGapSeverity.ToVerify, taskId: null);
        var blocking = GapFx.Gap();
        var session = new SessionGapState();
        Assert.True(session.Ignore(verify));

        var board = Build([verify, blocking], session);

        Assert.Equal([blocking.Id], board.Visible.Select(g => g.Id));
        Assert.Equal([verify.Id], board.Ignored.Select(g => g.Id));
        Assert.Equal(0, board.OpenToVerifyCount);
        Assert.Empty(board.OpenToVerify);
        Assert.Equal(1, board.OpenCount);
    }

    [Fact]
    public void The_open_gaps_to_verify_ignore_the_filter()
    {
        var verify = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null);

        var board = Build([verify, GapFx.Gap()], filter: new GapFilter(Severity: SubmissionGapSeverity.Blocking));

        Assert.Equal([verify.Id], board.OpenToVerify.Select(g => g.Id));
    }

    [Fact]
    public void The_first_blocking_gap_is_found_even_when_the_filter_hides_it()
    {
        var first = GapFx.Gap(taskId: 1, day: 0);
        var verify = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null);

        var board = Build([verify, first], filter: new GapFilter(Severity: SubmissionGapSeverity.ToVerify));

        Assert.Equal(first.Id, board.FirstBlocking?.Id);
        Assert.Null(Build([verify]).FirstBlocking);
    }

    [Fact]
    public void Next_gap_follows_the_display_order_and_loops_back_to_the_first()
    {
        var a = GapFx.Gap(taskId: 1, day: 0);
        var b = GapFx.Gap(taskId: 2, day: 1);
        var c = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null);
        var board = Build([c, b, a]);

        Assert.Equal(a.Id, board.NextId(null));
        Assert.Equal(b.Id, board.NextId(a.Id));
        Assert.Equal(c.Id, board.NextId(b.Id));
        Assert.Equal(a.Id, board.NextId(c.Id));
        Assert.Equal(a.Id, board.NextId("unknown"));
    }

    [Fact]
    public void Next_gap_is_null_when_nothing_is_open()
    {
        Assert.Null(Build([]).NextId(null));
    }

    [Fact]
    public void After_a_gap_is_settled_the_focus_goes_to_the_one_that_took_its_place()
    {
        var a = GapFx.Gap(taskId: 1);
        var b = GapFx.Gap(taskId: 2);
        var c = GapFx.Gap(taskId: 3);
        var before = Build([a, b, c]).Visible.Select(g => g.Id).ToList();

        Assert.Equal(c.Id, Build([a, c]).FocusAfterRemoval(before, b.Id));
        Assert.Equal(b.Id, Build([b, c]).FocusAfterRemoval(before, a.Id));
    }

    [Fact]
    public void After_the_last_gap_is_settled_the_focus_goes_to_the_last_remaining_one()
    {
        var a = GapFx.Gap(taskId: 1);
        var b = GapFx.Gap(taskId: 2);
        var c = GapFx.Gap(taskId: 3);
        var before = Build([a, b, c]).Visible.Select(g => g.Id).ToList();

        Assert.Equal(b.Id, Build([a, b]).FocusAfterRemoval(before, c.Id));
    }

    [Fact]
    public void After_the_only_gap_is_settled_there_is_nothing_to_focus()
    {
        var a = GapFx.Gap(taskId: 1);

        Assert.Null(Build([]).FocusAfterRemoval([a.Id], a.Id));
    }

    [Fact]
    public void Focus_after_removal_of_an_unknown_gap_falls_back_to_the_first_one()
    {
        var a = GapFx.Gap(taskId: 1);

        Assert.Equal(a.Id, Build([a]).FocusAfterRemoval([], "gone"));
    }

    [Fact]
    public void Find_looks_in_the_open_gaps_then_in_the_ignored_ones()
    {
        var open = GapFx.Gap(taskId: 1);
        var ignored = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null);
        var session = new SessionGapState();
        session.Ignore(ignored);

        var board = Build([open, ignored], session);

        Assert.Equal(open.Id, board.Find(open.Id)?.Id);
        Assert.Equal(ignored.Id, board.Find(ignored.Id)?.Id);
        Assert.Null(board.Find("none"));
    }

    [Fact]
    public void Cells_without_gap_are_the_cells_of_the_report_no_gap_talks_about()
    {
        var report = GapFx.Report(
            GapFx.Cell(1, 0), GapFx.Cell(2, 0), GapFx.Cell(3, 1), GapFx.Cell(1, 1));
        var gaps = new[]
        {
            GapFx.Gap(taskId: 1, day: 0, related: [new SubmissionGapCell(2, "Tâche 2", GapFx.Day(0))]),
        };

        var clean = GapBoard.CellsWithoutGap(report, gaps);

        Assert.Equal([(3, GapFx.Day(1)), (1, GapFx.Day(1))], clean.Select(c => (c.ForecastTaskId, c.Date)));
    }

    [Fact]
    public void Without_a_report_there_are_no_cells_without_gap()
    {
        Assert.Empty(GapBoard.CellsWithoutGap(null, [GapFx.Gap()]));
    }
}
