using System.Text.RegularExpressions;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// WP12 — the day strip, the day panel and the « jours prêts » bar cannot be rendered in these tests (no component test host), so their states
/// and buttons are tested in <see cref="DayStateLabelsTests"/>; these tests pin that the markup really shows those labels (icon + words,
/// never the colour alone), only lets the send button act as the labels say, and that every write goes through the page's single service.
/// </summary>
public sealed class DayStripWiringTests
{
    private static readonly string[] Shared = ["DayStrip", "DayPanel", "SendReadyBar", "ConflictChooser", "CellStateBadge", "DayJournal"];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimeSheetHelper.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string Component(string name) =>
        File.ReadAllText(Path.Combine(Root(), "src", "TimeSheetHelper.App", "Components", "Shared", name + ".razor"));

    private static string SubmitPage() =>
        File.ReadAllText(Path.Combine(Root(), "src", "TimeSheetHelper.App", "Components", "Pages", "Submit.razor"));

    private static int Count(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    [Theory]
    [InlineData("DayStrip", "day.State")]
    [InlineData("CellStateBadge", "State")]
    public void Labels_StateBadges_ShowIconAndWords_NotOnlyATone(string component, string state)
    {
        var badge = new Regex($"""<span class="badge @{Regex.Escape(state)}\.Tone[^"]*">\s*<Icon Name="@{Regex.Escape(state)}\.Icon"[^>]*/>\s*<span>@{Regex.Escape(state)}\.Label</span>""");

        Assert.Matches(badge, Component(component));
    }

    [Fact]
    public void G1_G4_G6_G7_DayPanel_SendButton_ActsOnlyAsTheLabelsAllow()
    {
        var panel = Component("DayPanel");
        var act = Regex.Match(panel, @"Task ActAsync\(DayAction action\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains("DayStateLabels.Action(Plan, Choices)", panel);
        Assert.Contains("disabled=\"@(!enabled)\"", panel);
        Assert.Matches(new Regex("""<p id="@reasonId" class="dp-reason">@Reason\(action\)</p>"""), panel);
        // Disabled (nothing new, conflict without choice, locked) → nothing; half read → read again; change in Forecast → confirm first.
        var order = new[] { "!action.Enabled", "action.ReadsForecast", "OnReplan.InvokeAsync()", "action.NeedsConfirmation", "confirmSend = true", "OnSend.InvokeAsync()" }
            .Select(s => act.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Equal(1, Count(act, "OnSend.InvokeAsync()"));
    }

    [Fact]
    public void G1_G9_Components_NeverWriteThemselves_EverySendGoesThroughThePageService()
    {
        foreach (var component in Shared)
        {
            var markup = Component(component);
            Assert.DoesNotContain("IDaySubmissionService", markup);
            Assert.DoesNotContain("DaySubmissionService", markup);
            Assert.DoesNotContain("CallToolAsync", markup);
        }

        var page = SubmitPage();
        Assert.Equal(1, Count(page, "DaySubmissions.SendAsync("));
        Assert.Equal(1, Count(page, "DaySubmissions.SendReadyAsync("));
        Assert.Contains("<DayStrip ", page);
        Assert.Contains("<DayPanel ", page);
        Assert.Contains("<SendReadyBar ", page);
    }

    [Fact]
    public void G9_WeeklyFill_NeverRunsDuringASendPerDay()
    {
        var page = SubmitPage();
        var fill = Regex.Match(page, @"private async Task FillAsync\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline).Groups["body"].Value;

        // The page-wide busy flag (status bar, « Recalculer », day selection, week change) and the fill button include the sends per day.
        Assert.Matches(new Regex(@"private bool DayBusy => sendingDay is not null \|\| sendingReady \|\| dayReading\.Count > 0;"), page);
        Assert.Matches(new Regex(@"private bool AnyBusy => [^;]*\bDayBusy;"), page);
        Assert.Contains("fillOp.Busy || DayBusy,", page);
        // The fill itself refuses to start, then holds its days (other tabs and processes) around the only write to Forecast.
        var order = new[] { "if (DayBusy", "DayLocks.TryHoldDaysAsync(week, days)", "if (hold is null)", "await using (hold)", "ForecastSubmitMode.Fill" }
            .Select(s => fill.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Equal(1, Count(page, "ForecastSubmitMode.Fill"));
    }

    [Fact]
    public void G9_SendReadyBar_ListsTheDaysItWillNotSend_WithTheirReason()
    {
        var bar = Component("SendReadyBar");

        Assert.Contains("DayStateLabels.Action(plan, NoChoices).Reason", bar);
        Assert.Matches(new Regex(@"@foreach \(var \(date, reason\) in OtherDays\)\s*\{?\s*<li>@DayStateLabels\.ShortDay\(date\) — @reason</li>"), bar);
    }

    [Fact]
    public void ConfirmDialog_KeepsFocusInside_AndTheReadyBarGivesItBackToItsButton()
    {
        var dialog = Component("ConfirmDialog");
        var bar = Component("SendReadyBar");

        // Tab past the last button wraps to the first, Shift+Tab past the first wraps to the last.
        Assert.Matches(new Regex("""<span class="focus-sentinel" tabindex="0" @onfocus="FocusCancelAsync"></span>\s*<h2"""), dialog);
        Assert.Matches(new Regex("""</div>\s*<span class="focus-sentinel" tabindex="0" @onfocus="FocusConfirmAsync"></span>\s*</div>\s*@code"""), dialog);
        // Both the cancel and the confirm path close the dialog through CloseConfirm, which asks for the focus back on the trigger.
        Assert.Equal(2, Count(bar, "@ref=\"trigger\""));
        Assert.Contains("OnCancel=\"CloseConfirm\"", bar);
        Assert.Matches(new Regex(@"private Task ConfirmAsync\(\)\s*\{\s*CloseConfirm\(\);"), bar);
        Assert.Contains("await trigger.FocusAsync()", bar);
    }
}
