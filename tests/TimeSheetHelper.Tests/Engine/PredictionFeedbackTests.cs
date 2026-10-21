using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Learning;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Tests.Engine;

public class PredictionFeedbackTests
{
    private const int TaskA = 1;
    private const int TaskB = 2;

    private static readonly Rule[] Rules =
    [
        new() { Id = 1, Name = "A", Field = RuleField.Repo, Operator = RuleOperator.Equals, Pattern = "contoso/a", ForecastTaskId = TaskA, Order = 1 },
    ];

    private static SlotAssignment Allocate(string repo, Func<string, IReadOnlyDictionary<int, int>> feedbackFor)
    {
        var options = Fx.Options();
        var resolver = new ActivityResolver(new RuleEvaluator(options), new GroupKeyBuilder(options), options);
        ActivityEvent[] activities = [Fx.Act(ActivitySource.Focus, Fx.Mon(9), Fx.Mon(9, 15), a => { a.Repo = repo; a.ProcessName = "Code"; })];

        var groupKey = resolver.Resolve(activities, Rules, new Dictionary<string, IReadOnlyDictionary<int, int>>()).Single().GroupKey!;
        var feedback = new Dictionary<string, IReadOnlyDictionary<int, int>> { [groupKey] = feedbackFor(groupKey) };
        var resolved = resolver.Resolve(activities, Rules, feedback);
        return new SlotAllocator(options, Fx.Clock).AllocateDay(Fx.Monday, resolved, []).Single(s => s.SlotStartUtc == Fx.Mon(9));
    }

    [Fact]
    public void Without_feedback_the_activity_keeps_its_single_candidate()
    {
        var candidate = Candidate.ForGroup("repo:x");
        Assert.Equal([(candidate, 1.0)], PredictionFeedback.Split(candidate, fromRule: false, null));
        Assert.Equal([(candidate, 1.0)], PredictionFeedback.Split(candidate, fromRule: false, new Dictionary<int, int>()));
    }

    [Fact]
    public void A_single_assignment_of_an_unclassified_group_is_only_an_alternative()
    {
        var slot = Allocate("contoso/z", _ => new Dictionary<int, int> { [TaskB] = 1 });

        Assert.Null(slot.TaskId);
        Assert.NotNull(slot.GroupKey);
        Assert.Contains(slot.Alternatives, a => a.TaskId == TaskB);
    }

    [Fact]
    public void Confirmations_of_an_unclassified_group_predict_the_confirmed_task()
    {
        var slot = Allocate("contoso/z", _ => new Dictionary<int, int> { [TaskB] = 2 });

        Assert.Equal(SlotKind.Activity, slot.Kind);
        Assert.Equal(TaskB, slot.TaskId);
        Assert.Equal(2 / (2 + PredictionFeedback.GroupPrior), slot.Confidence, 3);
        Assert.Contains(slot.Alternatives, a => a.TaskId is null && a.GroupKey is not null);
    }

    [Fact]
    public void A_rule_outweighs_fewer_confirmations_of_another_task_than_its_prior()
    {
        var slot = Allocate("contoso/a", _ => new Dictionary<int, int> { [TaskB] = 1 });

        Assert.Equal(TaskA, slot.TaskId);
        Assert.Equal(2.0 / 3, slot.Confidence, 3);
        Assert.Contains(slot.Alternatives, a => a.TaskId == TaskB);
    }

    [Fact]
    public void Enough_confirmations_override_a_rule()
    {
        var slot = Allocate("contoso/a", _ => new Dictionary<int, int> { [TaskB] = 3 });

        Assert.Equal(TaskB, slot.TaskId);
    }
}
