using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Tests.Engine;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _sut = new(Fx.Options(o => o.RegexTimeout = TimeSpan.FromMilliseconds(50)));

    private static readonly ActivityEvent Activity = Fx.Act(ActivitySource.VsCode, Fx.Mon(9), Fx.Mon(10), a =>
    {
        a.Repo = "Contoso/Api";
        a.Folder = @"C:\src\contoso\api";
        a.Title = "OrderService.cs - api - Visual Studio Code";
        a.Subject = "Sprint 42 review";
        a.Participants = "alice@contoso.com;bob@fabrikam.com";
    });

    private static Rule Rule(RuleField field, RuleOperator op, string pattern, int taskId = 1, int order = 0, bool enabled = true) =>
        new() { Name = pattern, Field = field, Operator = op, Pattern = pattern, ForecastTaskId = taskId, Order = order, Enabled = enabled };

    [Theory]
    [InlineData(RuleField.Repo, RuleOperator.Equals, "contoso/api", true)]
    [InlineData(RuleField.Repo, RuleOperator.Equals, "contoso/ap", false)]
    [InlineData(RuleField.Folder, RuleOperator.StartsWith, @"c:\SRC\contoso", true)]
    [InlineData(RuleField.Folder, RuleOperator.StartsWith, @"contoso", false)]
    [InlineData(RuleField.Title, RuleOperator.Contains, "orderservice", true)]
    [InlineData(RuleField.Participants, RuleOperator.Contains, "@FABRIKAM.com", true)]
    [InlineData(RuleField.Subject, RuleOperator.Regex, @"^sprint\s+\d+\s+REVIEW$", true)]
    [InlineData(RuleField.Subject, RuleOperator.Regex, @"^planning", false)]
    [InlineData(RuleField.Source, RuleOperator.Equals, "vscode", true)]
    [InlineData(RuleField.Project, RuleOperator.Contains, "x", false)] // field is null
    public void Operators_are_case_insensitive(RuleField field, RuleOperator op, string pattern, bool expected) =>
        Assert.Equal(expected, _sut.Matches(Rule(field, op, pattern), Activity));

    [Fact]
    public void Invalid_regex_never_matches()
    {
        Assert.False(_sut.Matches(Rule(RuleField.Title, RuleOperator.Regex, "(unclosed"), Activity));
        Assert.False(_sut.IsValidRegex("(unclosed"));
    }

    [Fact]
    public void Catastrophic_regex_times_out_without_throwing()
    {
        var evil = Fx.Act(ActivitySource.Focus, Fx.Mon(9), Fx.Mon(10), a => a.Title = new string('a', 5000) + "!");
        Assert.False(_sut.Matches(Rule(RuleField.Title, RuleOperator.Regex, "^(a|aa)+$"), evil));
    }

    [Fact]
    public void First_enabled_rule_by_order_wins()
    {
        var rules = RuleEvaluator.Prepare(
        [
            Rule(RuleField.Title, RuleOperator.Contains, "OrderService", taskId: 3, order: 5),
            Rule(RuleField.Repo, RuleOperator.Equals, "contoso/api", taskId: 1, order: 1, enabled: false),
            Rule(RuleField.Folder, RuleOperator.StartsWith, @"C:\src", taskId: 2, order: 2),
        ]);

        Assert.Equal(2, _sut.FirstMatch(Activity, rules)?.ForecastTaskId);
    }

    [Fact]
    public void No_match_returns_null() =>
        Assert.Null(_sut.FirstMatch(Activity, [Rule(RuleField.Repo, RuleOperator.Equals, "other/repo")]));
}
