using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine.Learning;

namespace TimeSheetHelper.Tests.Engine;

public class CorrectionLearnerTests : IAsyncLifetime
{
    private EngineTestHost _host = null!;
    private CorrectionLearner _sut = null!;

    public Task InitializeAsync()
    {
        _host = new EngineTestHost(new() { ["Engine:OwnDomains:0"] = "contoso.com" });
        _sut = _host.Get<CorrectionLearner>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static ActivityEvent A(int minutes, Action<ActivityEvent> configure) =>
        Fx.Act(ActivitySource.Focus, Fx.Mon(9), Fx.Mon(9).AddMinutes(minutes), configure);

    [Fact]
    public void Repo_is_preferred()
    {
        var p = _sut.Propose(
        [
            A(30, a => { a.Repo = "contoso/api"; a.Folder = @"C:\src\api"; a.Project = "Contoso"; }),
            A(10, a => a.Folder = @"C:\src\api"),
        ], 7);
        Assert.Equal((RuleField.Repo, RuleOperator.Equals, "contoso/api", 7), (p!.Field, p.Operator, p.Pattern, p.ForecastTaskId));
        Assert.Equal("Dépôt : contoso/api", p.Name);
    }

    [Fact]
    public void Minor_repo_is_not_stable_enough()
    {
        var p = _sut.Propose(
        [
            A(5, a => { a.Repo = "contoso/api"; a.Folder = @"C:\src\contoso\api"; }),
            A(30, a => a.Folder = @"C:\src\contoso\docs\specs"),
        ], 7);
        Assert.Equal((RuleField.Folder, RuleOperator.StartsWith, @"C:\src\contoso"), (p!.Field, p.Operator, p.Pattern));
    }

    [Fact]
    public void Folders_sharing_only_a_generic_root_use_the_dominant_folder()
    {
        var p = _sut.Propose(
        [
            A(30, a => a.Folder = @"C:\src\contoso\"),
            A(10, a => a.Folder = @"C:\docs"),
        ], 7);
        Assert.Equal((RuleField.Folder, RuleOperator.StartsWith, @"C:\src\contoso"), (p!.Field, p.Operator, p.Pattern));
    }

    [Fact]
    public void Url_folders_keep_their_scheme()
    {
        var p = _sut.Propose(
        [
            A(30, a => a.Folder = "https://contoso.sharepoint.com/sites/x/Shared Documents/Plans"),
            A(30, a => a.Folder = "https://contoso.sharepoint.com/sites/x/Shared Documents/Specs"),
        ], 7);
        Assert.Equal("https://contoso.sharepoint.com/sites/x/Shared Documents", p!.Pattern);
    }

    [Fact]
    public void Project_comes_after_folder()
    {
        var p = _sut.Propose([A(30, a => { a.Project = "Fabrikam"; a.Subject = "Bug 123"; })], 7);
        Assert.Equal((RuleField.Project, RuleOperator.Equals, "Fabrikam"), (p!.Field, p.Operator, p.Pattern));
    }

    [Fact]
    public void Subject_becomes_a_regex_tolerant_to_numbers_and_prefixes()
    {
        var p = _sut.Propose([A(30, a => { a.Source = ActivitySource.Calendar; a.Subject = "Sprint 42 Review"; a.Participants = "x@fabrikam.com"; })], 7);
        Assert.Equal((RuleField.Subject, RuleOperator.Regex), (p!.Field, p.Operator));
        Assert.Matches(new Regex(p.Pattern, RegexOptions.IgnoreCase), "RE: Sprint 43 - review");
        Assert.DoesNotMatch(new Regex(p.Pattern, RegexOptions.IgnoreCase), "Sprint planning");
    }

    [Fact]
    public void Participants_domain_when_nothing_else()
    {
        var p = _sut.Propose([A(30, a => { a.Source = ActivitySource.Teams; a.Participants = "me@contoso.com;bob@fabrikam.com"; })], 7);
        Assert.Equal((RuleField.Participants, RuleOperator.Contains, "@fabrikam.com"), (p!.Field, p.Operator, p.Pattern));
    }

    [Fact]
    public void Internal_participant_when_no_external_domain()
    {
        var p = _sut.Propose([A(30, a => { a.Source = ActivitySource.Teams; a.Participants = "Alice@contoso.com"; })], 7);
        Assert.Equal((RuleField.Participants, RuleOperator.Contains, "alice@contoso.com"), (p!.Field, p.Operator, p.Pattern));
    }

    [Fact]
    public void Nothing_stable_gives_no_rule() =>
        Assert.Null(_sut.Propose([A(30, a => a.ProcessName = "chrome"), Fx.Act(ActivitySource.Idle, Fx.Mon(9), Fx.Mon(10))], 7));

    [Theory]
    [InlineData("repo:contoso/api", RuleField.Repo, RuleOperator.Equals, "contoso/api")]
    [InlineData(@"folder:c:\src\api", RuleField.Folder, RuleOperator.StartsWith, @"c:\src\api")]
    [InlineData("meeting:sprint review", RuleField.Subject, RuleOperator.Regex, @"sprint[\W\d_]+review")]
    [InlineData("domain:fabrikam.com", RuleField.Participants, RuleOperator.Contains, "@fabrikam.com")]
    [InlineData("domain:adatum.com,fabrikam.com", RuleField.Participants, RuleOperator.Regex, @"@(?:adatum\.com|fabrikam\.com)")]
    [InlineData("app:winword:plan .docx", RuleField.Title, RuleOperator.Regex, @"plan[\W\d_]+\.docx")]
    [InlineData("app:spotify", RuleField.ProcessName, RuleOperator.Equals, "spotify")]
    public void Rule_from_group_key(string key, RuleField field, RuleOperator op, string pattern)
    {
        var p = _sut.ProposeFromGroupKey(key, 7);
        Assert.Equal((field, op, pattern), (p!.Field, p.Operator, p.Pattern));
    }

    [Fact]
    public async Task Persist_puts_the_correction_first_and_does_not_duplicate()
    {
        var task1 = await _host.AddTaskAsync("Contoso", "Dev");
        var task2 = await _host.AddTaskAsync("Contoso", "Support");
        await _host.AddRuleAsync(RuleField.Folder, RuleOperator.StartsWith, @"C:\src", task1.Id, order: 3);

        var rule = await _sut.PersistAsync(new RuleProposal(RuleField.Repo, RuleOperator.Equals, "contoso/api", task1.Id, "Dépôt : contoso/api"));
        Assert.Equal("correction", rule.Origin);
        Assert.Equal(2, rule.Order);

        var again = await _sut.PersistAsync(new RuleProposal(RuleField.Repo, RuleOperator.Equals, "Contoso/API", task2.Id, "x"), "claude");
        Assert.Equal(rule.Id, again.Id);

        await using var db = _host.Db();
        var stored = await db.Rules.SingleAsync(r => r.Id == rule.Id);
        Assert.Equal(task2.Id, stored.ForecastTaskId);
        Assert.Equal("claude", stored.Origin);
        Assert.Equal(2, await db.Rules.CountAsync());
    }
}
