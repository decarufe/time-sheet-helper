using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// WP12 — what the day by day submission shows for the guarantees: every state has words and an icon (never the colour alone), and the send
/// button of a real plan never invites a duplicate, a write over a conflict or a write on a locked or half-read day.
/// </summary>
public sealed class DayStateLabelsTests : IDisposable
{
    private static readonly DateOnly Monday = DaySubmissionHost.Monday;

    private readonly DaySubmissionHost _host = new();

    private FakeForecastWriteApi Server => _host.Server;

    public void Dispose() => _host.Dispose();

    public static TheoryData<string> AllStates()
    {
        var data = new TheoryData<string>();
        foreach (var state in Enum.GetValues<CellState>())
            data.Add($"cell:{state}");
        foreach (var status in Enum.GetValues<DayStatus>())
        {
            data.Add($"day:{status}:hours");
            data.Add($"day:{status}:empty");
        }
        return data;
    }

    private static StateLabel LabelOf(string key)
    {
        var parts = key.Split(':');
        return parts[0] == "cell"
            ? DayStateLabels.Cell(Enum.Parse<CellState>(parts[1]))
            : DayStateLabels.Day(Enum.Parse<DayStatus>(parts[1]), parts[2] == "hours");
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Labels_EveryState_HasWordsIconAndTone(string key)
    {
        var label = LabelOf(key);

        Assert.False(string.IsNullOrWhiteSpace(label.Label));
        Assert.False(string.IsNullOrWhiteSpace(label.Icon));
        Assert.False(string.IsNullOrWhiteSpace(label.Tone));
    }

    [Fact]
    public void Labels_StatesSharingATone_AreToldApartByTheirWords()
    {
        var cells = Enum.GetValues<CellState>().Select(DayStateLabels.Cell).Append(DayStateLabels.NotCompared).ToList();
        var days = Enum.GetValues<DayStatus>().Select(s => DayStateLabels.Day(s, hasHours: true))
            .Append(DayStateLabels.Day(DayStatus.NotAttempted, hasHours: false)).Append(DayStateLabels.ToReread).Append(DayStateLabels.NothingToSend).ToList();

        Assert.Equal(cells.Count, cells.Select(l => l.Label).Distinct().Count());
        Assert.Equal(days.Count, days.Select(l => l.Label).Distinct().Count());
    }

    [Fact]
    public void Gates_EveryDisabledButton_SaysWhy()
    {
        foreach (var gate in Enum.GetValues<DayGate>())
        {
            var action = DayStateLabels.Gate(gate);
            Assert.False(string.IsNullOrWhiteSpace(action.Label));
            if (!action.Enabled)
                Assert.False(string.IsNullOrWhiteSpace(action.Reason), $"{gate} is disabled without a reason");
        }
    }

    [Fact]
    public async Task G1_Labels_SentDay_OffersNoSecondSend()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);

        var plan = await _host.Service().PlanAsync(Monday);
        var action = DayStateLabels.Action(plan, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.NothingNew, plan.Gate);
        Assert.False(action.Enabled);
        Assert.Contains("aucun doublon", action.Reason);
        Assert.Contains("pas de doublon", DayStateLabels.Cell(plan.Cells.Single().State).Label);
        Assert.Equal("aucune écriture dans Forecast", DayStateLabels.Operations(plan, DaySubmissionHost.NoResolutions));
    }

    [Fact]
    public async Task G4_Labels_IncompleteRead_OnlyOffersToReadAgain()
    {
        Server.TruncatedRead = true;
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));

        var plan = await _host.Service().PlanAsync(Monday);
        var action = DayStateLabels.Action(plan, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.ReadForecastFirst, plan.Gate);
        Assert.True(action.ReadsForecast);
        Assert.Contains("rien ne sera écrit", action.Reason);
        // The day holds hours but nothing is known of Forecast: neither « Non saisi » nor « Prêt ».
        Assert.Equal(("À relire", "warn", "warn"), (DayStateLabels.Day(plan).Label, DayStateLabels.Day(plan).Icon, DayStateLabels.Day(plan).Tone));
        // Its cells were never compared nor attempted: not « envoi interrompu », and no technical detail (ISO date, count read) in the note.
        var cell = plan.Cells.Single();
        Assert.Equal(DayStateLabels.NotCompared, DayStateLabels.Cell(cell, plan));
        Assert.DoesNotContain("interrompu", DayStateLabels.Cell(cell, plan).Label);
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", DayStateLabels.CellNote(cell, null));
        Assert.Contains("Relisez Forecast", DayStateLabels.CellNote(cell, null));
    }

    [Fact]
    public async Task Labels_DayWhoseHoursAreAllIgnored_IsNothingToSend_NotReady()
    {
        await _host.IgnoreForTheWeekAsync(_host.Dev, true);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));

        var plan = await _host.Service().PlanAsync(Monday);

        Assert.False(plan.IsReady);
        Assert.Equal(DayStatus.NotAttempted, plan.Status);
        Assert.Equal(("Rien à envoyer", "minus"), (DayStateLabels.Day(plan).Label, DayStateLabels.Day(plan).Icon));
    }

    [Fact]
    public async Task Labels_DayWithAnIgnoredAndASendableTask_StaysReady()
    {
        await _host.IgnoreForTheWeekAsync(_host.Support, true);
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m), (_host.Support, 1m));

        var plan = await _host.Service().PlanAsync(Monday);

        Assert.True(plan.IsReady);
        Assert.Equal("Prêt", DayStateLabels.Day(plan).Label);
        // With a complete reading, a cell keeps its own words.
        Assert.Equal(DayStateLabels.Cell(CellState.New), DayStateLabels.Cell(plan.Cells.First(c => c.State == CellState.New), plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Labels_NeverAttemptedDay_WithHoursItCannotWriteYet_IsNotShownAsEmpty(bool blocked)
    {
        // Blocked: the server offers no creation. Otherwise: the hours are already in Forecast (typed by the weekly fill).
        if (blocked)
            Server.Tools.RemoveAll(t => t.Name.EndsWith("time_registration_create", StringComparison.Ordinal));
        else
            Server.Add(Monday, 3m, 3382);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);

        Assert.False(plan.HasWork);
        Assert.Equal(DayStatus.NotAttempted, plan.Status);
        Assert.Equal(("Prêt", "soumettre"), (DayStateLabels.Day(plan).Label, DayStateLabels.Day(plan).Icon));
    }

    [Fact]
    public async Task Labels_DayWithoutHours_StaysNotEntered()
    {
        await _host.SetDraftAsync(Monday);

        var plan = await _host.Service().PlanAsync(Monday);

        Assert.Equal("Non saisi", DayStateLabels.Day(plan).Label);
    }

    [Fact]
    public async Task G5_Labels_ModifiedDay_AsksConfirmationBeforeUpdating()
    {
        await _host.SetDraftAsync(Monday, (_host.Dev, 2m));
        await _host.Service().SendAsync(Monday, DaySubmissionHost.NoResolutions);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);
        var action = DayStateLabels.Action(plan, DaySubmissionHost.NoResolutions);

        Assert.True(action.Enabled);
        Assert.True(action.NeedsConfirmation);
        Assert.Equal("1 modification", DayStateLabels.Operations(plan, DaySubmissionHost.NoResolutions));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(ConflictResolution.Keep, true, false)]
    [InlineData(ConflictResolution.AcceptForecast, true, false)]
    [InlineData(ConflictResolution.Complement, true, true)]
    [InlineData(ConflictResolution.Remove, true, true)]
    public async Task G6_Labels_Conflict_SendsOnlyAfterAChoice_AndConfirmsChangesToForecast(ConflictResolution? choice, bool enabled, bool confirm)
    {
        Server.Add(Monday, 1m, 3382);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));
        var choices = choice is { } c ? new Dictionary<int, ConflictResolution> { [_host.Dev.Id] = c } : [];

        var plan = await _host.Service().PlanAsync(Monday);
        var action = DayStateLabels.Action(plan, choices);

        Assert.Equal((enabled, confirm), (action.Enabled, action.NeedsConfirmation));
        if (!enabled)
            Assert.Contains("1 conflit sans choix", action.Reason);
    }

    [Fact]
    public async Task G6_Labels_ChoiceUnavailable_IsShownWithItsReason()
    {
        Server.WithoutEditTools();
        Server.Add(Monday, 1m, 3382);
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);
        var options = DayStateLabels.Options(plan.Cells.Single(), plan.Capabilities);

        Assert.Equal([ConflictResolution.Keep, ConflictResolution.AcceptForecast, ConflictResolution.Complement, ConflictResolution.Remove],
            options.Select(o => o.Value));
        Assert.All(options.Where(o => o.Value is ConflictResolution.Complement or ConflictResolution.Remove), o =>
        {
            Assert.False(o.Available);
            Assert.False(string.IsNullOrWhiteSpace(o.UnavailableReason));
        });
        Assert.False(DayStateLabels.Action(plan, new Dictionary<int, ConflictResolution> { [_host.Dev.Id] = ConflictResolution.Complement }).Enabled);
    }

    [Fact]
    public async Task G7_Labels_LockedDay_HasNoSendButton()
    {
        Server.Add(Monday, 2m, 3382, approvalStatus: "APPROVED");
        await _host.SetDraftAsync(Monday, (_host.Dev, 3m));

        var plan = await _host.Service().PlanAsync(Monday);
        var action = DayStateLabels.Action(plan, DaySubmissionHost.NoResolutions);

        Assert.Equal(DayGate.Locked, plan.Gate);
        Assert.False(action.Enabled);
        Assert.Equal(("Verrouillé", "lock"), (DayStateLabels.Day(plan).Label, DayStateLabels.Day(plan).Icon));
    }
}
