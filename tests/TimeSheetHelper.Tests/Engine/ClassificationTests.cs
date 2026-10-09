using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Tests.Engine;

public class ClassificationServiceTests : IAsyncLifetime
{
    private EngineTestHost _host = null!;
    private ClassificationService _sut = null!;
    private List<ForecastTask> _tasks = null!;

    private static ActivityGroup Group(string key, int minutes = 60) =>
        new(key, [ActivitySource.VsCode], ["Program.cs - api"], [@"C:\src\api"], [], TimeSpan.FromMinutes(minutes));

    public async Task InitializeAsync()
    {
        _host = new EngineTestHost();
        _sut = _host.Get<ClassificationService>();
        _tasks = [await _host.AddTaskAsync("Contoso", "Dev", "Contoso Inc."), await _host.AddTaskAsync("Fabrikam", "Support")];
        _host.Classifier.Answer = _ => new ClassificationResult(_tasks[0].Id, 0.8, "Dépôt du client Contoso", new SuggestedRule(RuleField.Repo, RuleOperator.Equals, "contoso/api"));
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Cache_hit_avoids_a_second_classifier_call()
    {
        var first = await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);
        var second = await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);

        Assert.Single(_host.Classifier.Calls);
        var verdict = second["repo:contoso/api"];
        Assert.Equal(first["repo:contoso/api"].TaskId, verdict.TaskId);
        Assert.Equal(0.8, verdict.Confidence);
        Assert.Equal("Dépôt du client Contoso", verdict.Reason);
        Assert.Equal(new SuggestedRule(RuleField.Repo, RuleOperator.Equals, "contoso/api"), verdict.SuggestedRule);
    }

    [Fact]
    public async Task Cache_is_keyed_by_sha256_of_group_and_task_list()
    {
        await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);

        await using var db = _host.Db();
        var entry = await db.ClassificationCache.SingleAsync();
        Assert.Equal(64, entry.Fingerprint.Length);
        Assert.Equal(ClassificationService.Fingerprint("repo:contoso/api", ClassificationService.TaskListVersion(_tasks)), entry.Fingerprint);
        Assert.Contains("\"Repo\"", entry.SuggestedRuleJson);
    }

    [Fact]
    public async Task New_task_list_invalidates_the_cache()
    {
        await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);
        _tasks.Add(await _host.AddTaskAsync("Adatum", "Audit"));
        await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);

        Assert.Equal(2, _host.Classifier.Calls.Count);
    }

    [Fact]
    public async Task Unavailable_classifier_is_never_called_but_cache_is_read()
    {
        await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);
        _host.Classifier.IsAvailable = false;

        var result = await _sut.ClassifyAsync([Group("repo:contoso/api"), Group("repo:other")], _tasks, []);

        Assert.Single(_host.Classifier.Calls);
        Assert.True(result.ContainsKey("repo:contoso/api"));
        Assert.False(result.ContainsKey("repo:other"));
    }

    [Fact]
    public async Task Short_groups_are_not_sent()
    {
        await _sut.ClassifyAsync([Group("repo:tiny", minutes: 10)], _tasks, []);
        Assert.Empty(_host.Classifier.Calls);
    }

    [Fact]
    public async Task Failed_classification_is_not_cached()
    {
        _host.Classifier.Answer = _ => null;
        await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);
        await _sut.ClassifyAsync([Group("repo:contoso/api")], _tasks, []);

        Assert.Equal(2, _host.Classifier.Calls.Count);
        await using var db = _host.Db();
        Assert.Empty(db.ClassificationCache);
    }
}

public class ClaudeClassifierTests
{
    private static readonly List<ForecastTask> Tasks =
    [
        new() { Id = 1, ExternalId = "1", ProjectName = "Contoso", TaskName = "Dev", ClientName = "Contoso Inc." },
        new() { Id = 2, ExternalId = "2", ProjectName = "Fabrikam", TaskName = "Support" },
    ];

    private static ClaudeClassifier Create(bool enabled, string? apiKey)
    {
        var options = Fx.Options(o => o.Claude.Enabled = enabled);
        var secrets = new MemorySecretStore();
        if (apiKey is not null)
            secrets.Set(SecretNames.AnthropicApiKey, apiKey);
        return new ClaudeClassifier(options, new RuleEvaluator(options), NullLogger<ClaudeClassifier>.Instance, secrets);
    }

    [Theory]
    [InlineData(false, "sk-test", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "sk-test", true)]
    public void Available_only_when_enabled_and_keyed(bool enabled, string? key, bool expected) =>
        Assert.Equal(expected, Create(enabled, key).IsAvailable);

    [Fact]
    public async Task Disabled_classifier_returns_null_without_network()
    {
        var group = new ActivityGroup("repo:x", [ActivitySource.VsCode], [], [], [], TimeSpan.FromHours(1));
        Assert.Null(await Create(false, "sk-test").ClassifyAsync(group, Tasks, []));
    }

    [Fact]
    public void Payload_contains_only_metadata()
    {
        var group = new ActivityGroup("domain:fabrikam.com", [ActivitySource.Email], ["Proposal"], [], ["RE: Proposal"], TimeSpan.FromMinutes(42));
        var rules = new List<Rule> { new() { Name = "r", Field = RuleField.Repo, Operator = RuleOperator.Equals, Pattern = "contoso/api", ForecastTaskId = 1 } };

        using var doc = JsonDocument.Parse(ClaudeClassifier.BuildPayload(group, Tasks, rules));
        var root = doc.RootElement;
        Assert.Equal("domain:fabrikam.com", root.GetProperty("group").GetProperty("key").GetString());
        Assert.Equal(42, root.GetProperty("group").GetProperty("totalMinutes").GetInt32());
        Assert.Equal(2, root.GetProperty("tasks").GetArrayLength());
        Assert.Equal("Repo", root.GetProperty("rules")[0].GetProperty("field").GetString());
        Assert.False(root.GetProperty("group").TryGetProperty("participants", out _));
    }

    [Fact]
    public void Parse_accepts_a_valid_answer()
    {
        var result = Create(true, "k").Parse(
            """{"taskId":2,"confidence":0.7,"reason":"Domaine du client","suggestedRule":{"field":"Participants","operator":"Contains","pattern":"@fabrikam.com"}}""",
            Tasks);
        Assert.Equal(new ClassificationResult(2, 0.7, "Domaine du client", new SuggestedRule(RuleField.Participants, RuleOperator.Contains, "@fabrikam.com")), result);
    }

    [Fact]
    public void Parse_drops_unknown_task_and_its_rule()
    {
        var result = Create(true, "k").Parse(
            """{"taskId":99,"confidence":0.9,"reason":"?","suggestedRule":{"field":"Repo","operator":"Equals","pattern":"x"}}""", Tasks);
        Assert.Null(result.TaskId);
        Assert.Equal(0, result.Confidence);
        Assert.Null(result.SuggestedRule);
    }

    [Fact]
    public void Parse_clamps_confidence_and_rejects_invalid_regex()
    {
        var result = Create(true, "k").Parse(
            """{"taskId":1,"confidence":3,"reason":"r","suggestedRule":{"field":"Subject","operator":"Regex","pattern":"(oops"}}""", Tasks);
        Assert.Equal(1, result.Confidence);
        Assert.Null(result.SuggestedRule);
    }

    [Fact]
    public void Parse_accepts_null_task()
    {
        var result = Create(true, "k").Parse("""{"taskId":null,"confidence":0.1,"reason":"Navigation personnelle","suggestedRule":null}""", Tasks);
        Assert.Null(result.TaskId);
        Assert.Equal("Navigation personnelle", result.Reason);
    }
}
