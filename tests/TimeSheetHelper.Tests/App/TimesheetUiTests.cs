using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Tests.Engine;

namespace TimeSheetHelper.Tests.App;

public sealed class TimesheetUiTests
{
    private static SlotAssignment Slot(SlotKind kind) =>
        new(Fx.Mon(9), kind, null, null, 1.0, [], [], TimeSpan.Zero);

    [Theory]
    [InlineData(SlotKind.Removed, true)]
    [InlineData(SlotKind.ExcludedByOverride, false)]
    [InlineData(SlotKind.Empty, false)]
    [InlineData(SlotKind.Activity, false)]
    [InlineData(SlotKind.Meeting, false)]
    [InlineData(SlotKind.Override, false)]
    [InlineData(SlotKind.Ignored, false)]
    public void Only_deleted_slots_count_as_removed_not_the_ones_marked_not_work(SlotKind kind, bool expected) =>
        Assert.Equal(expected, TimesheetUi.IsRemoved(Slot(kind)));

    [Fact]
    public void Confirm_proposes_the_predicted_task_else_the_best_classified_alternative_but_never_ignore()
    {
        var tasks = new Dictionary<int, ForecastTask>
        {
            [1] = new() { Id = 1, ExternalId = "1", ProjectName = "P", TaskName = "A" },
            [2] = new() { Id = 2, ExternalId = "2", ProjectName = "P", TaskName = "B" },
            [9] = new() { Id = 9, ExternalId = ForecastTask.IgnoredExternalId, ProjectName = "-", TaskName = "-" },
        };
        SlotAssignment Predicted(SlotKind kind, int? taskId, params SlotCandidate[] alternatives) =>
            new(Fx.Mon(9), kind, taskId, taskId is null ? "repo:x" : null, 0.5, alternatives, [], TimeSpan.Zero);

        Assert.Equal(1, TimesheetUi.PredictedTask(Predicted(SlotKind.Activity, 1), tasks));
        Assert.Equal(1, TimesheetUi.PredictedTask(Predicted(SlotKind.Meeting, 1), tasks));
        Assert.Equal(2, TimesheetUi.PredictedTask(Predicted(SlotKind.Activity, null, new(null, "repo:y", 1, 0.3), new(9, null, 1, 0.2), new(2, null, 1, 0.1)), tasks));
        Assert.Null(TimesheetUi.PredictedTask(Predicted(SlotKind.Activity, null, new SlotCandidate(null, "repo:y", 1, 0.5)), tasks));
        Assert.Null(TimesheetUi.PredictedTask(Predicted(SlotKind.Override, 1), tasks));
        Assert.Null(TimesheetUi.PredictedTask(Predicted(SlotKind.Ignored, 9), tasks));
    }

    [Fact]
    public void Meeting_group_keys_propose_the_meeting_title_as_new_task_name()
    {
        Assert.Equal("daily standup", TimesheetUi.MeetingTitle("meeting:daily standup"));
        Assert.Null(TimesheetUi.MeetingTitle("repo:contoso/api"));
        Assert.Null(TimesheetUi.MeetingTitle(null));
    }

    [Fact]
    public void Consecutive_slots_have_no_gap_even_when_a_removed_slot_is_hidden_between_them()
    {
        Assert.True(TimesheetUi.AreConsecutive([Fx.Mon(9), Fx.Mon(9, 15), Fx.Mon(9, 30)]));
        Assert.True(TimesheetUi.AreConsecutive([Fx.Mon(9)]));

        // 9:15 was deleted and is hidden: the rows 9:00 and 9:30 are neighbours in the list but not in time.
        Assert.False(TimesheetUi.AreConsecutive([Fx.Mon(9), Fx.Mon(9, 30)]));
        Assert.False(TimesheetUi.AreConsecutive([]));
    }

    [Fact]
    public void Adjacent_means_exactly_one_slot_later()
    {
        Assert.True(TimesheetUi.AreAdjacent(Fx.Mon(9), Fx.Mon(9, 15)));
        Assert.False(TimesheetUi.AreAdjacent(Fx.Mon(9), Fx.Mon(9, 30)));
        Assert.False(TimesheetUi.AreAdjacent(Fx.Mon(9, 15), Fx.Mon(9)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(14 * 60 + 59, false)]
    [InlineData(15 * 60, true)]
    [InlineData(15 * 60 + 1, false)]
    [InlineData(-15 * 60, false)]
    public void Adjacent_is_exact_to_the_second(int offsetSeconds, bool expected) =>
        Assert.Equal(expected, TimesheetUi.AreAdjacent(Fx.Mon(9), Fx.Mon(9).AddSeconds(offsetSeconds)));

    [Fact]
    public void Slots_around_midnight_are_adjacent_across_the_day_change()
    {
        var lastOfMonday = Fx.Mon(23, 45);
        var firstOfTuesday = Fx.Local(Fx.Monday.AddDays(1), 0);

        Assert.True(TimesheetUi.AreAdjacent(lastOfMonday, firstOfTuesday));
        Assert.True(TimesheetUi.AreConsecutive([Fx.Mon(23, 30), lastOfMonday, firstOfTuesday]));
    }

    [Fact]
    public void Adjacency_follows_real_time_on_the_daylight_saving_days()
    {
        // Fall back (2026-11-01, Toronto): 01:45 EDT is followed by 01:00 EST on the wall clock, yet by one slot in time.
        var beforeRepeat = new DateTime(2026, 11, 1, 5, 45, 0, DateTimeKind.Utc);
        var afterRepeat = new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
        Assert.True(TimesheetUi.AreAdjacent(beforeRepeat, afterRepeat));
        Assert.True(TimesheetUi.AreConsecutive([beforeRepeat, afterRepeat, afterRepeat.AddMinutes(15)]));

        // The same wall-clock time an hour apart is not adjacent: 01:15 EDT then 01:15 EST.
        Assert.False(TimesheetUi.AreAdjacent(new DateTime(2026, 11, 1, 5, 15, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 6, 15, 0, DateTimeKind.Utc)));

        // Spring forward (2026-03-08): 01:45 EST is followed by 03:00 EDT, one slot later in time.
        var beforeGap = new DateTime(2026, 3, 8, 6, 45, 0, DateTimeKind.Utc);
        Assert.True(TimesheetUi.AreAdjacent(beforeGap, beforeGap.AddMinutes(15)));
        Assert.Equal(new TimeSpan(1, 45, 0), TimeZoneInfo.ConvertTimeFromUtc(beforeGap, FixedTimeProvider.Toronto).TimeOfDay);
        Assert.Equal(new TimeSpan(3, 0, 0), TimeZoneInfo.ConvertTimeFromUtc(beforeGap.AddMinutes(15), FixedTimeProvider.Toronto).TimeOfDay);
    }

    [Fact]
    public void Consecutive_rejects_duplicates_unordered_input_and_a_single_gap_anywhere()
    {
        Assert.False(TimesheetUi.AreConsecutive([Fx.Mon(9), Fx.Mon(9)]));
        Assert.False(TimesheetUi.AreConsecutive([Fx.Mon(9, 15), Fx.Mon(9)]));
        Assert.False(TimesheetUi.AreConsecutive([Fx.Mon(9), Fx.Mon(9, 15), Fx.Mon(9, 45), Fx.Mon(10)]));
        Assert.False(TimesheetUi.AreConsecutive([Fx.Mon(9), Fx.Mon(9, 30), Fx.Mon(9, 45)]));
        Assert.True(TimesheetUi.AreConsecutive(Enumerable.Range(0, 96).Select(i => Fx.Mon(0).AddMinutes(15 * i)).ToList()));
    }

    [Theory]
    [InlineData(SlotKind.Removed, 0, false)]
    [InlineData(SlotKind.Empty, 0, false)]
    [InlineData(SlotKind.Empty, 1, true)]
    [InlineData(SlotKind.Activity, 1, true)]
    [InlineData(SlotKind.Override, 0, true)]
    [InlineData(SlotKind.ExcludedByOverride, 1, true)]
    public void Only_a_slot_with_an_assignment_or_activities_can_be_cleared(SlotKind kind, int activities, bool expected) =>
        Assert.Equal(expected, TimesheetUi.HasContent(new SlotAssignment(
            Fx.Mon(9), kind, null, null, 0, [], Enumerable.Range(1, activities).Select(i => (long)i).ToList(), TimeSpan.Zero)));

    [Theory]
    [InlineData(1, "Effacer ce créneau ?")]
    [InlineData(2, "Effacer ces 2 créneaux ?")]
    [InlineData(12, "Effacer ces 12 créneaux ?")]
    public void Remove_confirmation_title_counts_the_slots(int count, string expected) =>
        Assert.Equal(expected, TimesheetUi.RemoveConfirmationTitle(count));

    [Fact]
    public void Remove_confirmation_message_warns_it_is_irreversible_and_points_to_not_work_for_keeping_activities()
    {
        var one = TimesheetUi.RemoveConfirmationMessage(1);
        Assert.StartsWith("Le contenu de ce créneau de 15 minutes sera effacé", one);
        Assert.Contains("définitivement", one);
        Assert.Contains("irréversible", one);
        Assert.Contains("d'autres créneaux", one);
        Assert.Contains("Pas du travail", one);

        var many = TimesheetUi.RemoveConfirmationMessage(3);
        Assert.StartsWith("Le contenu de ces 3 créneaux de 15 minutes sera effacé", many);
        Assert.Contains("irréversible", many);
    }

    [Theory]
    [InlineData(0, 0, 0, "Aucun créneau à effacer.")]
    [InlineData(1, 0, 0, "1 créneau effacé, aucune activité effacée.")]
    [InlineData(1, 1, 0, "1 créneau effacé, 1 activité effacée.")]
    [InlineData(3, 5, 0, "3 créneaux effacés, 5 activités effacées.")]
    [InlineData(1, 2, 1, "1 créneau effacé, 2 activités effacées. 1 activité conservée car elle touche aussi d'autres créneaux.")]
    [InlineData(2, 0, 3, "2 créneaux effacés, aucune activité effacée. 3 activités conservées car elles touchent aussi d'autres créneaux.")]
    public void Removal_result_message_reports_slots_erased_and_kept_activities(int slots, int deleted, int kept, string expected) =>
        Assert.Equal(expected, TimesheetUi.RemovalResultMessage(new SlotRemovalResult(slots, deleted, kept)));

    private static ForecastTask Task(string project, string task, string? client = null, string? projectCode = null) =>
        new() { ExternalId = "x", ProjectName = project, TaskName = task, ClientName = client, ProjectCode = projectCode };

    [Fact]
    public void Task_label_shows_a_task_without_project_or_task_number_like_any_other()
    {
        Assert.Equal("Contoso / Dev", TimesheetUi.TaskLabel(Task("P1", "Dev", "Contoso", projectCode: null), 7));
        Assert.Equal("Projet X / Dev", TimesheetUi.TaskLabel(Task("Projet X", "Dev"), 7));
    }

    [Fact]
    public void Task_label_uses_placeholders_instead_of_a_blank_name()
    {
        Assert.Equal("(sans projet) / Dev", TimesheetUi.TaskLabel(Task(" ", "Dev"), 7));
        Assert.Equal("Contoso / (sans tâche)", TimesheetUi.TaskLabel(Task("P1", "", "Contoso"), 7));
        Assert.Equal("Tâche #7 (inconnue)", TimesheetUi.TaskLabel(null, 7));
    }

    [Fact]
    public void Task_label_keeps_the_ignored_task_label() =>
        Assert.Equal(ForecastTask.IgnoredLabel,
            TimesheetUi.TaskLabel(new ForecastTask { ExternalId = ForecastTask.IgnoredExternalId, ProjectName = "", TaskName = "" }, 1));
}
