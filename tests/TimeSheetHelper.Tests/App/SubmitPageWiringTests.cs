using System.Text.RegularExpressions;
using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// The Submit page cannot be rendered in these tests (no component test host), so the rules of the fill are tested in FillGuard; these
/// tests pin that the page really goes through FillGuard at every decision point and keeps no guard of its own beside it.
/// </summary>
public sealed class SubmitPageWiringTests
{
    private static readonly Lazy<string> Razor = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimeSheetHelper.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir.FullName, "src", "TimeSheetHelper.App", "Components", "Pages", "Submit.razor"));
    });

    private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    // The stale-gap window of the page: the report of the fill is on screen before its analysis lands, so the gaps are older than the report.
    [Fact]
    public void A_cell_with_a_finding_is_not_without_gap_even_when_the_gaps_in_hand_do_not_know_it_yet()
    {
        var cell = GapFx.Cell(1, 0, ForecastCellStatus.Ok) with { Findings = [new ForecastCellFinding(ForecastFindingKind.TaskClosed, "Terminée")] };
        var report = GapFx.Report(cell);

        Assert.Empty(GapBoard.CellsWithoutGap(report, []));
    }

    [Theory]
    [InlineData(ForecastCellStatus.Mismatch)]
    [InlineData(ForecastCellStatus.RowNotFound)]
    [InlineData(ForecastCellStatus.Error)]
    [InlineData(ForecastCellStatus.Locked)]
    [InlineData(ForecastCellStatus.Unknown)]
    public void A_cell_that_is_not_ready_nor_in_forecast_is_never_without_gap_even_without_any_gap(ForecastCellStatus status)
    {
        Assert.Empty(GapBoard.CellsWithoutGap(GapFx.Report(GapFx.Cell(1, 0, status)), []));
    }

    [Fact]
    public void Ready_and_entered_cells_without_finding_are_listed_when_no_gap_talks_about_them()
    {
        var report = GapFx.Report(GapFx.Cell(1, 0, ForecastCellStatus.Planned), GapFx.Cell(2, 0, ForecastCellStatus.Ok));

        Assert.Equal([1, 2], GapBoard.CellsWithoutGap(report, []).Select(c => c.ForecastTaskId).Order().ToList());
    }

    [Fact]
    public void The_page_marks_the_week_typed_only_from_the_verdict_of_FillGuard()
    {
        // Scoped to the selected day(s): one call checks the scope, a second (gated by the first) checks the whole draft.
        Assert.Contains("var weekEntered = scopeEntered && FillGuard.IsWeekFullyEntered(report, current, tasks);", Razor.Value);
        Assert.Equal(2, Count(Razor.Value, "FillGuard.IsWeekFullyEntered("));
        Assert.Equal(1, Count(Razor.Value, "SubmittedUtc = DateTime.UtcNow"));
    }

    [Fact]
    public void The_week_is_marked_typed_before_the_page_checks_that_it_still_shows_that_week_so_that_a_week_change_loses_nothing()
    {
        var razor = Razor.Value;
        var verdict = razor.IndexOf("var scopeEntered = FillGuard.IsWeekFullyEntered(", StringComparison.Ordinal);
        var mark = razor.IndexOf("SubmittedUtc = DateTime.UtcNow", StringComparison.Ordinal);
        var nextScreenCheck = razor.IndexOf("FillGuard.ResultApplies(", verdict, StringComparison.Ordinal);

        Assert.True(verdict >= 0 && mark > verdict && nextScreenCheck > mark, "marking must sit between the verdict and the screen check");
        Assert.Contains("if (weekEntered)", razor[verdict..mark]);
        Assert.DoesNotContain("ResultApplies", razor[verdict..mark]);
    }

    [Fact]
    public void The_page_asks_FillGuard_before_the_fill_and_again_on_the_draft_read_just_before_writing()
    {
        Assert.Contains("FillGuard.Preflight(canFill, draft, simulationFingerprint) != FillPreflight.Allowed", Razor.Value);
        Assert.Contains("FillGuard.Preflight(canFill, current, simulationFingerprint) != FillPreflight.Allowed", Razor.Value);
    }

    [Fact]
    public void The_page_asks_FillGuard_before_any_simulation_and_before_showing_any_result_of_an_operation()
    {
        Assert.Contains("!FillGuard.CanSimulate(draft is not null, draft?.SubmittedUtc is not null, disposed, fillOp.Busy, selectedDays.Count > 0)", Razor.Value);
        // A week change prunes the selection, and the list of gaps is scoped by the checked days (no day dropdown).
        Assert.Contains("DaySelection.Prune(selectedDays, monday)", Razor.Value);
        Assert.DoesNotContain("Days=\"grid.Days\"", Razor.Value);
        // Ask-for-confirmation, result of the fill (shown), mark-then-show, failure: each checks the week still on screen.
        Assert.True(Count(Razor.Value, "FillGuard.ResultApplies(week, monday, disposed)") >= 5);
    }

    [Fact]
    public void The_page_keeps_no_guard_of_its_own_next_to_FillGuard()
    {
        var razor = Razor.Value;

        Assert.DoesNotContain("ForecastCellStatus", razor);
        Assert.DoesNotContain("ForecastFillPlanner", razor);
        Assert.DoesNotContain(".Cells.All(", razor);
        Assert.DoesNotContain(".Cells.Any(", razor);
        Assert.DoesNotContain("expectedCells", razor);
        Assert.DoesNotMatch(@"(?<!FillGuard)(?<!\.)\bFingerprint\(", razor);
    }
}
