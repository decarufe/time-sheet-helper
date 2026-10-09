using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Tests.Engine;

public class SlotAllocatorTests
{
    private const int TaskA = 1;
    private const int TaskB = 2;
    private const int TaskMeeting = 3;

    private static readonly Rule[] Rules =
    [
        new() { Id = 1, Name = "A", Field = RuleField.Repo, Operator = RuleOperator.Equals, Pattern = "contoso/a", ForecastTaskId = TaskA, Order = 1 },
        new() { Id = 2, Name = "B", Field = RuleField.Repo, Operator = RuleOperator.Equals, Pattern = "contoso/b", ForecastTaskId = TaskB, Order = 2 },
        new() { Id = 3, Name = "M", Field = RuleField.Subject, Operator = RuleOperator.Contains, Pattern = "standup", ForecastTaskId = TaskMeeting, Order = 3 },
    ];

    private static (SlotAllocator Allocator, ActivityResolver Resolver) Create(Action<EngineOptions>? configure = null)
    {
        var options = Fx.Options(configure);
        var resolver = new ActivityResolver(new RuleEvaluator(options), new GroupKeyBuilder(options), options);
        return (new SlotAllocator(options, Fx.Clock), resolver);
    }

    private static SlotAssignment Slot(DateTime slotStartUtc, ActivityEvent[] activities, SlotOverride[]? overrides = null, Action<EngineOptions>? configure = null)
    {
        var (allocator, resolver) = Create(configure);
        var resolved = resolver.Resolve(activities, Rules);
        var slots = allocator.AllocateDay(Fx.Monday, resolved, overrides ?? []);
        return slots.Single(s => s.SlotStartUtc == slotStartUtc);
    }

    private static ActivityEvent Focus(DateTime start, DateTime end, string repo, ActivitySource source = ActivitySource.Focus) =>
        Fx.Act(source, start, end, a => { a.Repo = repo; a.ProcessName = "Code"; });

    private static ActivityEvent Meeting(DateTime start, DateTime end, string subject = "Daily standup", string? kind = "meeting") =>
        Fx.Act(ActivitySource.Calendar, start, end, a => { a.Subject = subject; a.Kind = kind; });

    [Fact]
    public void Day_has_96_slots_starting_at_local_midnight()
    {
        var (allocator, _) = Create();
        var slots = allocator.AllocateDay(Fx.Monday, [], []);
        Assert.Equal(96, slots.Count);
        Assert.Equal(new DateTime(2026, 10, 5, 4, 0, 0, DateTimeKind.Utc), slots[0].SlotStartUtc); // EDT = UTC-4
        Assert.All(slots, s => Assert.Equal(SlotKind.Empty, s.Kind));
    }

    [Fact]
    public void Dst_end_day_has_100_slots()
    {
        var (allocator, _) = Create();
        Assert.Equal(100, allocator.AllocateDay(new DateOnly(2026, 11, 1), [], []).Count);
    }

    [Fact]
    public void Full_focus_on_a_rule_gives_the_task_with_full_confidence()
    {
        var slot = Slot(Fx.Mon(9), [Focus(Fx.Mon(9), Fx.Mon(9, 15), "contoso/a")]);
        Assert.Equal(SlotKind.Activity, slot.Kind);
        Assert.Equal(TaskA, slot.TaskId);
        Assert.Null(slot.GroupKey);
        Assert.Equal(1.0, slot.Confidence, 3);
        Assert.Empty(slot.Alternatives);
        Assert.Equal(TimeSpan.FromMinutes(15), slot.ActiveTime);
    }

    [Fact]
    public void Too_little_activity_is_not_work()
    {
        var slot = Slot(Fx.Mon(9), [Focus(Fx.Mon(9), Fx.Mon(9, 4, 59), "contoso/a")]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
        Assert.Null(slot.TaskId);
        Assert.False(slot.IsWork);
    }

    [Fact]
    public void Threshold_is_configurable()
    {
        var slot = Slot(Fx.Mon(9), [Focus(Fx.Mon(9), Fx.Mon(9, 2), "contoso/a")], configure: o => o.MinActivityPerSlot = TimeSpan.FromMinutes(1));
        Assert.Equal(TaskA, slot.TaskId);
    }

    [Fact]
    public void Idle_time_inside_a_focus_segment_is_excluded()
    {
        // VS Code extension segment kept running while the user was away 9:02 - 9:13.
        var slot = Slot(Fx.Mon(9),
        [
            Focus(Fx.Mon(9), Fx.Mon(9, 15), "contoso/a", ActivitySource.VsCode),
            Fx.Act(ActivitySource.Idle, Fx.Mon(9, 2), Fx.Mon(9, 13)),
        ]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
        Assert.Equal(TimeSpan.FromMinutes(4), slot.ActiveTime);
    }

    [Fact]
    public void Locked_time_is_not_work()
    {
        var slot = Slot(Fx.Mon(9),
        [
            Focus(Fx.Mon(8), Fx.Mon(10), "contoso/a", ActivitySource.ClaudeCode),
            Fx.Act(ActivitySource.Locked, Fx.Mon(8, 50), Fx.Mon(9, 30)),
        ]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
    }

    [Fact]
    public void Accepted_meeting_overrides_focus()
    {
        var slot = Slot(Fx.Mon(10),
        [
            Meeting(Fx.Mon(10), Fx.Mon(10, 30)),
            Focus(Fx.Mon(10), Fx.Mon(10, 15), "contoso/a"),
        ]);
        Assert.Equal(SlotKind.Meeting, slot.Kind);
        Assert.Equal(TaskMeeting, slot.TaskId);
        Assert.Contains(slot.Alternatives, c => c.TaskId == TaskA);
        Assert.True(slot.Confidence > 0.9);
    }

    [Fact]
    public void Meeting_counts_even_when_the_user_is_idle()
    {
        var slot = Slot(Fx.Mon(10, 15),
        [
            Meeting(Fx.Mon(10), Fx.Mon(10, 30)),
            Fx.Act(ActivitySource.Idle, Fx.Mon(10, 5), Fx.Mon(10, 40)),
        ]);
        Assert.Equal(SlotKind.Meeting, slot.Kind);
        Assert.Equal(TaskMeeting, slot.TaskId);
    }

    [Fact]
    public void Heavy_vs_code_editing_beats_the_meeting()
    {
        var slot = Slot(Fx.Mon(10),
        [
            Meeting(Fx.Mon(10), Fx.Mon(10, 30)),
            Focus(Fx.Mon(10), Fx.Mon(10, 12), "contoso/a", ActivitySource.VsCode),
        ]);
        Assert.Equal(SlotKind.Activity, slot.Kind);
        Assert.Equal(TaskA, slot.TaskId);
        Assert.Contains(slot.Alternatives, c => c.TaskId == TaskMeeting);
    }

    [Fact]
    public void Editing_threshold_is_configurable()
    {
        var slot = Slot(Fx.Mon(10),
            [Meeting(Fx.Mon(10), Fx.Mon(10, 30)), Focus(Fx.Mon(10), Fx.Mon(10, 12), "contoso/a", ActivitySource.VsCode)],
            configure: o => o.MeetingYieldsToEditingAbove = TimeSpan.FromMinutes(14));
        Assert.Equal(SlotKind.Meeting, slot.Kind);
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("tentative")]
    public void Declined_or_tentative_meetings_are_ignored(string kind)
    {
        var slot = Slot(Fx.Mon(10), [Meeting(Fx.Mon(10), Fx.Mon(10, 30), kind: kind)]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
    }

    [Fact]
    public void All_day_events_are_ignored()
    {
        var slot = Slot(Fx.Mon(10), [Meeting(Fx.Mon(0), Fx.Local(Fx.Monday.AddDays(1), 0))]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
    }

    [Fact]
    public void Short_meeting_end_alone_does_not_fill_a_slot()
    {
        var slot = Slot(Fx.Mon(10, 30), [Meeting(Fx.Mon(10), Fx.Mon(10, 33))]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
    }

    [Fact]
    public void Overlapping_sources_are_weighted_by_source()
    {
        // Focus says an unknown folder, VS Code (weight 1.5) says repo B over the same period.
        var slot = Slot(Fx.Mon(14),
        [
            Fx.Act(ActivitySource.Focus, Fx.Mon(14), Fx.Mon(14, 15), a => { a.Folder = @"C:\scratch"; a.ProcessName = "Code"; }),
            Focus(Fx.Mon(14), Fx.Mon(14, 15), "contoso/b", ActivitySource.VsCode),
        ]);
        Assert.Equal(TaskB, slot.TaskId);
        Assert.Equal(0.6, slot.Confidence, 3);
        var alternative = Assert.Single(slot.Alternatives);
        Assert.Equal(@"folder:c:\scratch", alternative.GroupKey);
        Assert.Equal(TimeSpan.FromMinutes(15), slot.ActiveTime); // overlap counted once
    }

    [Fact]
    public void Unclassified_activity_gives_its_group()
    {
        var slot = Slot(Fx.Mon(14), [Focus(Fx.Mon(14), Fx.Mon(14, 15), "fabrikam/web")]);
        Assert.Null(slot.TaskId);
        Assert.Equal("repo:fabrikam/web", slot.GroupKey);
        Assert.True(slot.IsWork);
        Assert.False(slot.IsClassified);
    }

    [Fact]
    public void Point_event_alone_is_not_work()
    {
        var commit = Fx.Act(ActivitySource.GitHub, Fx.Mon(15, 14), null, a => { a.Kind = "commit"; a.Repo = "contoso/a"; });
        var slot = Slot(Fx.Mon(15), [commit]);
        Assert.Equal(SlotKind.Empty, slot.Kind);
        Assert.Contains(commit.Id, slot.Evidence);
    }

    [Fact]
    public void Commit_window_breaks_a_tie_between_two_tasks()
    {
        var commit = Fx.Act(ActivitySource.GitHub, Fx.Mon(15, 15), null, a => { a.Kind = "commit"; a.Repo = "contoso/b"; });
        var slot = Slot(Fx.Mon(15),
        [
            Focus(Fx.Mon(15), Fx.Mon(15, 7, 30), "contoso/a"),
            Fx.Act(ActivitySource.Focus, Fx.Mon(15, 7, 30), Fx.Mon(15, 15), a => { a.ProcessName = "chrome"; a.Title = "PR - contoso/b"; a.Repo = "contoso/b"; }),
            commit,
        ]);
        Assert.Equal(TaskB, slot.TaskId);
        Assert.Contains(commit.Id, slot.Evidence);
    }

    [Fact]
    public void Override_to_a_task_wins_over_everything()
    {
        var slot = Slot(Fx.Mon(10),
            [Meeting(Fx.Mon(10), Fx.Mon(10, 30))],
            [new SlotOverride { SlotStartUtc = Fx.Mon(10), ForecastTaskId = TaskB }]);
        Assert.Equal(SlotKind.Override, slot.Kind);
        Assert.Equal(TaskB, slot.TaskId);
        Assert.Equal(1.0, slot.Confidence);
        Assert.Contains(slot.Alternatives, c => c.TaskId == TaskMeeting);
    }

    [Fact]
    public void Override_on_an_empty_slot_still_counts()
    {
        var slot = Slot(Fx.Mon(20), [], [new SlotOverride { SlotStartUtc = Fx.Mon(20), ForecastTaskId = TaskA }]);
        Assert.Equal(TaskA, slot.TaskId);
        Assert.True(slot.IsClassified);
    }

    [Fact]
    public void Override_without_task_excludes_the_slot()
    {
        var slot = Slot(Fx.Mon(9),
            [Focus(Fx.Mon(9), Fx.Mon(9, 15), "contoso/a")],
            [new SlotOverride { SlotStartUtc = Fx.Mon(9), ForecastTaskId = null }]);
        Assert.Equal(SlotKind.ExcludedByOverride, slot.Kind);
        Assert.False(slot.IsWork);
        Assert.Null(slot.TaskId);
    }

    [Fact]
    public void Activity_across_slots_is_split()
    {
        var (allocator, resolver) = Create();
        var resolved = resolver.Resolve([Focus(Fx.Mon(9, 10), Fx.Mon(9, 40), "contoso/a")], Rules);
        var slots = allocator.AllocateDay(Fx.Monday, resolved, []);

        SlotAssignment At(int h, int m) => slots.Single(s => s.SlotStartUtc == Fx.Mon(h, m));
        Assert.Equal(TaskA, At(9, 0).TaskId);   // exactly 5 minutes reaches the threshold
        Assert.Equal(TaskA, At(9, 15).TaskId);
        Assert.Equal(TaskA, At(9, 30).TaskId);  // 10 minutes
        Assert.Equal(SlotKind.Empty, At(9, 45).Kind);
        Assert.Equal(3, slots.Count(s => s.IsWork));
    }
}
