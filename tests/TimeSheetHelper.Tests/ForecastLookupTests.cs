using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public sealed class ForecastLookupTests
{
    private const string Intranet1078 = "M365 - Équipe Laurence - Intranet Centre de Marque";

    private readonly FakeForecastClient _server = new FakeForecastClient().AddLookupTools();
    private readonly ManualClock _clock = new();

    private ForecastLookup CreateLookup(IForecastConnector? connector = null, ForecastOptions? options = null) =>
        new(
            new ForecastApi(connector ?? new FakeForecastConnector(_server), Options.Create(options ?? new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            Options.Create(options ?? new ForecastOptions()),
            _clock,
            NullLogger<ForecastLookup>.Instance);

    // ----- projects

    [Theory]
    [InlineData("P1078")]
    [InlineData("p1078")]
    [InlineData("P-1078")]
    [InlineData("1078")]
    [InlineData("  P1078  ")]
    public async Task Search_projects_finds_the_exact_number(string query)
    {
        var result = await CreateLookup().SearchProjectsAsync(query);

        Assert.True(result.Succeeded);
        var project = Assert.Single(result.Items);
        Assert.Equal(1078, project.Id);
        Assert.Equal("P1078", project.Code);
        Assert.Equal(Intranet1078, project.Name);
        Assert.Equal("Équipe Laurence Inc.", project.ClientName);
        Assert.Equal("RUNNING", project.Status);
    }

    [Fact]
    public async Task Search_projects_by_partial_text_ignores_case_and_accents()
    {
        var byAccentless = await CreateLookup().SearchProjectsAsync("equipe LAURENCE");

        Assert.Equal(1078, Assert.Single(byAccentless.Items).Id);
    }

    [Fact]
    public async Task Search_projects_also_matches_the_client()
    {
        var result = await CreateLookup().SearchProjectsAsync("corporation bcf");

        Assert.Equal(1020, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Search_projects_ranks_a_name_that_starts_with_the_text_before_one_that_contains_it()
    {
        _server.Projects.Add((1500, "Intranet interne", "Nexus", "RUNNING"));

        var result = await CreateLookup().SearchProjectsAsync("intranet");

        Assert.Equal([1500, 1020, 1078, 1057], result.Items.Select(p => p.Id));
    }

    [Fact]
    public void Ranking_gives_the_exact_number_priority_over_text_hits()
    {
        var text = new ForecastProjectMatch(2000, "P2000", "Projet 1078 historique", null, null);
        var exact = new ForecastProjectMatch(1078, "P1078", "Intranet", null, null);

        var bare = ForecastLookup.RankProjects([text, exact], "1078");
        var prefixed = ForecastLookup.RankProjects([text, exact], "P1078");

        Assert.Equal([1078, 2000], bare.Items.Select(p => p.Id));
        Assert.Equal(1078, Assert.Single(prefixed.Items).Id);
    }

    [Fact]
    public async Task Search_projects_limits_the_results_and_reports_there_are_more()
    {
        for (var i = 1; i <= 15; i++)
            _server.Projects.Add((3000 + i, $"Chantier {i:00}", "Client", "RUNNING"));

        var result = await CreateLookup().SearchProjectsAsync("chantier");

        Assert.Equal(ForecastLookup.MaxResults, result.Items.Count);
        Assert.Equal(15, result.Total);
        Assert.True(result.HasMore);
        Assert.Equal("Chantier 01", result.Items[0].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData(" i ")]
    public async Task Search_projects_returns_nothing_without_calling_forecast_when_the_query_is_too_short(string? query)
    {
        var connector = new FakeForecastConnector(_server);

        var result = await CreateLookup(connector).SearchProjectsAsync(query);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
        Assert.Equal(0, connector.Connects);
        Assert.Equal(0, _server.LookupCalls);
    }

    [Fact]
    public async Task Search_projects_returns_nothing_for_an_unknown_text_instead_of_the_suggested_projects()
    {
        var result = await CreateLookup().SearchProjectsAsync("zzzzqqq");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Search_projects_rethrows_the_cancellation_of_the_caller()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateLookup().SearchProjectsAsync("intranet", cts.Token));
    }

    [Fact]
    public async Task Get_project_returns_the_exact_project_or_nothing()
    {
        var lookup = CreateLookup();

        var found = await lookup.GetProjectAsync("p1078");
        var unknown = await lookup.GetProjectAsync("P9999");
        var invalid = await lookup.GetProjectAsync("intranet");

        Assert.Equal("P1078", found.Item?.Code);
        Assert.False(unknown.Found);
        Assert.Null(unknown.Error);
        Assert.False(invalid.Found);
        Assert.Null(invalid.Error);
    }

    // ----- tasks

    [Theory]
    [InlineData("T35647")]
    [InlineData("t35647")]
    [InlineData("35647")]
    public async Task Search_tasks_finds_the_exact_number_with_its_project(string query)
    {
        var result = await CreateLookup().SearchTasksAsync(query);

        var task = Assert.Single(result.Items);
        Assert.Equal("T35647", task.Code);
        Assert.Equal("Automatisation - Nethris", task.Name);
        Assert.Null(task.Description);
        Assert.Equal("Automatisation - Nethris", task.Text);
        Assert.Equal(1078, task.ProjectId);
        Assert.Equal("P1078", task.ProjectCode);
        Assert.Equal(Intranet1078, task.ProjectName);
    }

    [Fact]
    public async Task Search_tasks_by_partial_text_ranks_and_ignores_accents()
    {
        var result = await CreateLookup().SearchTasksAsync("AUTOMATISATION");
        var accents = await CreateLookup().SearchTasksAsync("reunion");

        Assert.Equal(["T35647", "T35648"], result.Items.Select(t => t.Code));
        Assert.Equal("T35701", Assert.Single(accents.Items).Code);
    }

    [Fact]
    public async Task Search_tasks_can_be_restricted_to_a_project()
    {
        var result = await CreateLookup().SearchTasksAsync("automatisation", projectId: 1078);

        Assert.Equal("T35647", Assert.Single(result.Items).Code);
        Assert.Equal(1078, Convert.ToInt32(_server.Calls.Last(c => c.Tool == "find_task").Args["project_id"]));
    }

    [Fact]
    public async Task Search_tasks_in_a_project_drops_a_task_number_of_another_project()
    {
        var result = await CreateLookup().SearchTasksAsync("T35647", projectId: 1057);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Search_tasks_returns_nothing_when_the_query_is_too_short_or_unknown()
    {
        var lookup = CreateLookup();

        Assert.Empty((await lookup.SearchTasksAsync("r")).Items);
        Assert.Equal(0, _server.LookupCalls);
        var unknown = await lookup.SearchTasksAsync("zzzzqqq");
        Assert.True(unknown.Succeeded);
        Assert.Empty(unknown.Items);
    }

    [Fact]
    public async Task Search_tasks_limits_the_results()
    {
        for (var i = 1; i <= 14; i++)
            _server.LookupTasks.Add((40000 + i, $"Support {i:00}", 12));

        var result = await CreateLookup().SearchTasksAsync("support");

        Assert.Equal(ForecastLookup.MaxResults, result.Items.Count);
        Assert.Equal(14, result.Total);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task Search_tasks_rethrows_the_cancellation_of_the_caller()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateLookup().SearchTasksAsync("revue", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Get_task_returns_the_exact_task_whatever_its_project_or_nothing()
    {
        var lookup = CreateLookup();

        var found = await lookup.GetTaskAsync("35647");
        var unknown = await lookup.GetTaskAsync("T99999");
        var invalid = await lookup.GetTaskAsync("revue");

        Assert.Equal("T35647", found.Item?.Code);
        Assert.False(unknown.Found);
        Assert.Null(unknown.Error);
        Assert.False(invalid.Found);
    }

    // ----- errors

    [Fact]
    public async Task A_network_failure_is_a_readable_error_not_an_exception()
    {
        _server.LookupFailure = new HttpRequestException("connection reset");

        var projects = await CreateLookup().SearchProjectsAsync("intranet");
        var tasks = await CreateLookup().SearchTasksAsync("revue");
        var task = await CreateLookup().GetTaskAsync("T35647");

        foreach (var error in new[] { projects.Error, tasks.Error, task.Error })
        {
            Assert.NotNull(error);
            Assert.Contains("injoignable", error);
            Assert.DoesNotContain("connection reset", error);
        }
        Assert.False(projects.Succeeded);
        Assert.Empty(projects.Items);
    }

    [Fact]
    public async Task A_request_timeout_is_a_readable_error()
    {
        _server.LookupFailure = new TaskCanceledException("timeout");

        var result = await CreateLookup().SearchProjectsAsync("intranet");

        Assert.Contains("n'a pas répondu à temps", result.Error);
    }

    [Fact]
    public async Task A_missing_sign_in_gives_the_login_message()
    {
        var result = await CreateLookup(new ThrowingConnector(new ForecastAuthRequiredException())).SearchTasksAsync("revue");

        Assert.Equal(new ForecastAuthRequiredException().Message, result.Error);
        Assert.Contains("forecast login", result.Error);
    }

    [Fact]
    public async Task A_tool_missing_on_the_server_is_a_readable_error()
    {
        var bare = new FakeForecastClient();
        var lookup = new ForecastLookup(
            new ForecastApi(new FakeForecastConnector(bare), Options.Create(new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            Options.Create(new ForecastOptions()), _clock, NullLogger<ForecastLookup>.Instance);

        var projects = await lookup.SearchProjectsAsync("intranet");
        var tasks = await lookup.SearchTasksAsync("revue");

        Assert.Contains("search_projects", projects.Error);
        Assert.Contains("find_task", tasks.Error);
    }

    [Fact]
    public async Task A_tool_that_answers_with_an_error_is_a_readable_error()
    {
        _server.LookupFailure = new ForecastToolException("« find_task » a répondu par une erreur : boum");

        var result = await CreateLookup().SearchTasksAsync("revue");

        Assert.Contains("boum", result.Error);
    }

    // ----- cache

    [Fact]
    public async Task The_same_search_is_answered_from_the_cache_until_it_expires()
    {
        var connector = new FakeForecastConnector(_server);
        var lookup = CreateLookup(connector);

        var first = await lookup.SearchProjectsAsync("Intranet");
        var again = await lookup.SearchProjectsAsync("  intranet ");
        Assert.Equal(1, _server.LookupCalls);
        Assert.Equal(1, connector.Connects);
        Assert.Equal(first.Items, again.Items);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await lookup.SearchProjectsAsync("intranet");

        Assert.Equal(2, _server.LookupCalls);
    }

    [Fact]
    public async Task Tasks_and_project_lookups_share_the_cache_of_their_own_query()
    {
        var lookup = CreateLookup();

        await lookup.SearchTasksAsync("T35647");
        await lookup.GetTaskAsync("35647");
        await lookup.SearchTasksAsync("T35647", projectId: 1078); // another scope: another call

        Assert.Equal(2, _server.LookupCalls);
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        var lookup = CreateLookup();
        _server.LookupFailure = new HttpRequestException();
        Assert.False((await lookup.SearchProjectsAsync("intranet")).Succeeded);

        _server.LookupFailure = null;
        var retry = await lookup.SearchProjectsAsync("intranet");

        Assert.True(retry.Succeeded);
        Assert.NotEmpty(retry.Items);
    }

    [Fact]
    public async Task A_zero_cache_duration_always_asks_forecast()
    {
        var lookup = CreateLookup(options: new ForecastOptions { LookupCacheDuration = TimeSpan.Zero });

        await lookup.SearchProjectsAsync("intranet");
        await lookup.SearchProjectsAsync("intranet");

        Assert.Equal(2, _server.LookupCalls);
    }

    // ----- auto-fill

    [Fact]
    public async Task Auto_fill_with_nothing_typed_does_nothing()
    {
        var connector = new FakeForecastConnector(_server);

        var result = await CreateLookup(connector).AutoFillAsync(" ", null);

        Assert.Same(ForecastAutoFillResult.Empty, result);
        Assert.Equal(0, connector.Connects);
    }

    [Fact]
    public async Task Auto_fill_from_a_project_alone_gives_the_project_and_its_name()
    {
        var result = await CreateLookup().AutoFillAsync("P1078", null);

        Assert.Equal(1078, result.Project?.Id);
        Assert.Null(result.Task);
        Assert.Equal(Intranet1078, result.Description);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Auto_fill_from_a_task_alone_gives_the_task_its_project_and_the_client()
    {
        var result = await CreateLookup().AutoFillAsync(null, "T35647");

        Assert.Equal("T35647", result.Task?.Code);
        Assert.Equal("P1078", result.Project?.Code);
        Assert.Equal("Équipe Laurence Inc.", result.Project?.ClientName);
        Assert.Equal($"{Intranet1078} / Automatisation - Nethris", result.Description);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Auto_fill_from_a_project_and_one_of_its_tasks_gives_both()
    {
        var result = await CreateLookup().AutoFillAsync("1078", "35700");

        Assert.Equal(1078, result.Project?.Id);
        Assert.Equal("T35700", result.Task?.Code);
        Assert.Equal($"{Intranet1078} / Revue de code", result.Description);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Auto_fill_keeps_the_typed_project_and_drops_a_task_of_another_project()
    {
        var result = await CreateLookup().AutoFillAsync("P1057", "T35647");

        Assert.Equal(1057, result.Project?.Id);
        Assert.Null(result.Task);
        Assert.Equal("M365 - ITAQ - Intranet", result.Description);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("T35647", warning);
        Assert.Contains("P1078", warning);
        Assert.Contains("P1057", warning);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Auto_fill_warns_about_an_unknown_task_and_keeps_the_project()
    {
        var result = await CreateLookup().AutoFillAsync("P1078", "T99999");

        Assert.Equal(1078, result.Project?.Id);
        Assert.Null(result.Task);
        Assert.Equal(Intranet1078, result.Description);
        Assert.Contains("T99999", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Auto_fill_warns_about_an_unknown_project_and_still_uses_the_task()
    {
        var result = await CreateLookup().AutoFillAsync("P9999", "T35647");

        Assert.Contains("P9999", Assert.Single(result.Warnings));
        Assert.Equal("T35647", result.Task?.Code);
        Assert.Equal(1078, result.Project?.Id);
    }

    [Fact]
    public async Task Auto_fill_warns_about_numbers_that_are_not_numbers()
    {
        var result = await CreateLookup().AutoFillAsync("intranet", "revue");

        Assert.Null(result.Project);
        Assert.Null(result.Task);
        Assert.Null(result.Description);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Equal(0, _server.LookupCalls);
    }

    [Fact]
    public async Task Auto_fill_reports_a_forecast_failure_as_an_error()
    {
        _server.LookupFailure = new HttpRequestException();

        var result = await CreateLookup().AutoFillAsync("P1078", "T35647");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Null(result.Description);
    }

    [Fact]
    public async Task Auto_fill_rethrows_the_cancellation_of_the_caller()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateLookup().AutoFillAsync("P1078", null, cts.Token));
    }

    [Fact]
    public void Compose_prefers_the_task_description_over_its_name()
    {
        var project = new ForecastProjectMatch(1078, "P1078", "Intranet", null, null);
        var task = new ForecastTaskMatch("T1", "Nethris", "Automatiser la paie", 1078, "P1078", "Intranet", null);

        Assert.Equal("Intranet / Automatiser la paie", ForecastLookup.Compose(project, task));
        Assert.Equal("Automatiser la paie", ForecastLookup.Compose(null, task));
        Assert.Equal("Intranet", ForecastLookup.Compose(project, null));
        Assert.Null(ForecastLookup.Compose(null, null));
    }

    // ----- parsing

    [Fact]
    public void Parse_projects_reads_a_single_project_the_matches_and_ignores_the_suggestions()
    {
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        var single = ForecastJson.ParseProjects(Json("""{"found":true,"id":"UHJvamVjdFR5cGU6","project_id":1078,"custom_project_id":"P1078","name":" Intranet  ","client":"Acme","status":"RUNNING"}"""));
        var many = ForecastJson.ParseProjects(Json("""{"found":true,"matches":[{"id":"x","project_id":1,"name":"A"},{"id":"y","project_id":2,"custom_project_id":"P-2","name":"B"}]}"""));
        var none = ForecastJson.ParseProjects(Json("""{"found":false,"all_projects":[{"id":"x","project_id":1,"name":"A"}]}"""));

        var project = Assert.Single(single);
        Assert.Equal(("1078", "P1078", "Intranet", "Acme", "RUNNING"), (project.Id, project.Code, project.Name, project.ClientName, project.Status));
        Assert.Equal(["1", "2"], many.Select(p => p.Id));
        Assert.Null(many[0].Code);
        Assert.Empty(none);
    }

    [Fact]
    public void Parse_tasks_keeps_the_description_when_the_server_sends_one()
    {
        var json = JsonDocument.Parse("""{"task":{"task_id":7,"task_name":"Dev","description":"Faire le  travail","project_id":5,"project_name":"Alpha"}}""").RootElement;

        var task = Assert.Single(ForecastJson.ParseTasks(json));

        Assert.Equal("Faire le  travail", task.Description);
        Assert.Equal("5", task.ProjectId);
    }

    [Theory]
    [InlineData("P1078", true, "P1078", true)]
    [InlineData("p-1078", true, "P1078", true)]
    [InlineData("1078", true, "P1078", false)]
    [InlineData("P", false, "", false)]
    [InlineData("1078a", false, "", false)]
    [InlineData("intranet", false, "", false)]
    public void Project_numbers_are_recognised_with_or_without_the_prefix(string text, bool ok, string code, bool explicitCode)
    {
        Assert.Equal(ok, ForecastLookup.TryParseProjectNumber(text, out var actual, out var isExplicit));
        Assert.Equal(code, actual);
        Assert.Equal(explicitCode, isExplicit);
    }

    [Theory]
    [InlineData("T35647", true, "T35647", true)]
    [InlineData("35647", true, "T35647", false)]
    [InlineData("t1", true, "T1", true)]
    [InlineData("T", false, "", false)]
    [InlineData("P1078", false, "", false)]
    public void Task_numbers_are_recognised_with_or_without_the_prefix(string text, bool ok, string code, bool explicitCode)
    {
        Assert.Equal(ok, ForecastLookup.TryParseTaskNumber(text, out var actual, out var isExplicit));
        Assert.Equal(code, actual);
        Assert.Equal(explicitCode, isExplicit);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ThrowingConnector(Exception exception) : IForecastConnector
    {
        public bool HasCredentials => false;

        public Task<IForecastToolClient> ConnectAsync(bool interactive, Action<string>? progress = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
