using TimeSheetHelper.App.Services;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Proposals;

namespace TimeSheetHelper.Tests.App;

/// <summary>The context the gap service gets from the week proposal.</summary>
public sealed class SubmitPageGadgetTests
{
    private static SlotAssignment Slot(DateTime startUtc, SlotKind kind) => new(startUtc, kind, null, null, 1.0, [], [], TimeSpan.Zero);

    private static WeekProposal Proposal(
        DateOnly weekStart, Dictionary<DateOnly, IReadOnlyList<SlotAssignment>> slots, Dictionary<DateOnly, decimal>? unclassified = null) =>
        new(
            weekStart,
            Enumerable.Range(0, 7).Select(weekStart.AddDays).Select(d => new DayProposal(
                d, [], 0m, d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0m : 8m, unclassified?.GetValueOrDefault(d) ?? 0m, DayFlag.Ok)).ToList(),
            [],
            slots.ToDictionary(p => p.Key, p => p.Value),
            new Dictionary<int, TimeSheetHelper.Core.Forecast.ForecastTask>(),
            false);

    [Fact]
    public void Only_the_slots_marked_not_work_or_ignored_count_as_set_aside_and_deleted_ones_count_for_nothing()
    {
        var monday = GapFx.Monday;
        var start = new DateTime(2026, 10, 12, 13, 0, 0, DateTimeKind.Utc);
        var slots = new List<SlotAssignment>
        {
            Slot(start, SlotKind.ExcludedByOverride), Slot(start.AddMinutes(15), SlotKind.ExcludedByOverride), Slot(start.AddMinutes(30), SlotKind.ExcludedByOverride),
            Slot(start.AddMinutes(45), SlotKind.Ignored),
            Slot(start.AddHours(1), SlotKind.Removed), Slot(start.AddHours(1).AddMinutes(15), SlotKind.Removed),
            Slot(start.AddHours(2), SlotKind.Activity), Slot(start.AddHours(2).AddMinutes(15), SlotKind.Override), Slot(start.AddHours(2).AddMinutes(30), SlotKind.Empty),
        };

        var context = SubmissionReviewService.ContextOf(Proposal(
            monday, new() { [monday] = slots }, new() { [monday.AddDays(1)] = 1.25m }));

        Assert.Equal(monday, context.WeekStart);
        Assert.Equal(Enumerable.Range(0, 7).Select(monday.AddDays), context.Days.Select(d => d.Date));
        Assert.Equal(1.0m, context.Days[0].ExcludedHours);
        Assert.Equal(0m, context.Days[1].ExcludedHours);
        Assert.Equal(1.25m, context.Days[1].UnclassifiedHours);
        Assert.Equal(0m, context.Days[0].UnclassifiedHours);
        Assert.Equal([8m, 8m, 8m, 8m, 8m, 0m, 0m], context.Days.Select(d => d.TargetHours));
    }

    [Fact]
    public void On_the_day_the_clock_falls_back_the_two_slots_of_the_repeated_hour_are_two_different_slots()
    {
        var weekStart = new DateOnly(2026, 10, 26);
        var fallBack = new DateOnly(2026, 11, 1);
        // 01:15 EDT then 01:15 EST: the same wall-clock time, one hour apart.
        var slots = new List<SlotAssignment>
        {
            Slot(new DateTime(2026, 11, 1, 5, 15, 0, DateTimeKind.Utc), SlotKind.ExcludedByOverride),
            Slot(new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc), SlotKind.ExcludedByOverride),
        };

        var context = SubmissionReviewService.ContextOf(Proposal(weekStart, new() { [fallBack] = slots }));

        var sunday = Assert.Single(context.Days, d => d.Date == fallBack);
        Assert.Equal(0.5m, sunday.ExcludedHours);
        Assert.Equal(0m, sunday.TargetHours);
    }

    // ── Defects found by the review (expected behaviour, to enable once fixed) ───────────────────
}
