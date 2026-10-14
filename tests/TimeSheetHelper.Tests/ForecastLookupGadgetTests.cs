using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

/// <summary>
/// Edge cases of <see cref="IForecastLookup"/> that <see cref="ForecastLookupTests"/> does not pin down: ranking tiers, scope of a task search,
/// special characters in the query, cache boundaries, cancellation while Forecast answers, lifetime of the MCP session, fallbacks of the auto-fill.
/// </summary>
public sealed class ForecastLookupGadgetTests
{
    private readonly FakeForecastClient _server = new FakeForecastClient().AddLookupTools();
    private readonly Clock _clock = new();

    private ForecastLookup Create(IForecastConnector? connector = null, ForecastOptions? options = null, List<string>? log = null)
    {
        var settings = Options.Create(options ?? new ForecastOptions());
        return new ForecastLookup(
            new ForecastApi(connector ?? new FakeForecastConnector(_server), settings, new RecordingLogger<ForecastApi>(log ?? [])),
            settings,
            _clock,
            new RecordingLogger<ForecastLookup>(log ?? []));
    }

    private ForecastLookup CreateOn(IForecastToolClient client, FakeForecastConnector? connector = null) =>
        Create(connector ?? new FakeForecastConnector(client));

    // ----- ranking

    [Fact]
    public void Projects_are_ranked_same_name_then_name_prefix_then_name_contains_then_client_then_the_rest()
    {
        var contains = new ForecastProjectMatch(1, "P1", "Zeta Intranet", null, null);
        var exact = new ForecastProjectMatch(2, "P2", "Intranet", null, null);
        var prefix = new ForecastProjectMatch(3, "P3", "Intranet Plus", null, null);
        var byClient = new ForecastProjectMatch(4, "P4", "Autre", "Intranet Corp", null);
        var other = new ForecastProjectMatch(5, "P5", "Rien", null, null);

        var result = ForecastLookup.RankProjects([other, byClient, contains, prefix, exact], "  INTRANET ");

        Assert.Equal([2, 3, 1, 4, 5], result.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task Tasks_are_ranked_by_the_client_whatever_the_order_of_the_server()
    {
        var client = CannedClient.Answering(_ => Json("""
            {"tasks":[
              {"task_id":4,"task_name":"Planning","project_id":9,"project_name":"Revue annuelle"},
              {"task_id":3,"task_name":"Pré-revue","project_id":9,"project_name":"Autre"},
              {"task_id":2,"task_name":"Revue de code","project_id":9,"project_name":"Autre"},
              {"task_id":1,"task_name":"REVUE","project_id":9,"project_name":"Autre"}]}
            """));

        var result = await CreateOn(client).SearchTasksAsync("revue");

        Assert.Equal(["T1", "T2", "T3", "T4"], result.Items.Select(t => t.Code));
    }

    [Fact]
    public async Task A_bare_task_number_puts_the_exact_task_first_and_keeps_the_text_hits_but_a_prefixed_one_keeps_only_the_task()
    {
        var client = CannedClient.Answering(_ => Json("""
            {"tasks":[
              {"task_id":40,"task_name":"Note 35647","project_id":1,"project_name":"Alpha"},
              {"task_id":35647,"task_name":"Zeta","project_id":1,"project_name":"Alpha"}]}
            """));
        var lookup = CreateOn(client);

        var bare = await lookup.SearchTasksAsync("35647");
        var prefixed = await lookup.SearchTasksAsync("T35647");

        Assert.Equal(["T35647", "T40"], bare.Items.Select(t => t.Code));
        Assert.Equal("T35647", Assert.Single(prefixed.Items).Code);
        Assert.False(prefixed.HasMore);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public async Task The_list_is_cut_at_ten_and_only_reports_more_when_there_really_are_more(int count, bool hasMore)
    {
        for (var i = 1; i <= count; i++)
            _server.Projects.Add((3000 + i, $"Chantier {i:00}", "Client", "RUNNING"));

        var result = await Create().SearchProjectsAsync("chantier");

        Assert.Equal(10, result.Items.Count);
        Assert.Equal(count, result.Total);
        Assert.Equal(hasMore, result.HasMore);
    }

    [Fact]
    public async Task The_same_project_or_task_returned_twice_by_the_server_is_listed_once()
    {
        var client = CannedClient.Answering(tool => tool == "search_projects"
            ? Json("""{"found":true,"matches":[{"project_id":1,"name":"Alpha"},{"project_id":1,"name":"Alpha"},{"project_id":2,"name":"Alpha bis"}]}""")
            : Json("""{"tasks":[{"task_id":5,"task_name":"Alpha","project_id":1},{"task_id":5,"task_name":"Alpha","project_id":1}]}"""));
        var lookup = CreateOn(client);

        var projects = await lookup.SearchProjectsAsync("alpha");
        var tasks = await lookup.SearchTasksAsync("alpha");

        Assert.Equal(2, projects.Total);
        Assert.Equal(1, tasks.Total);
    }

    // ----- scope of a task search

    [Fact]
    public async Task A_text_search_scoped_to_a_project_drops_other_projects_even_when_the_tool_cannot_scope()
    {
        _server.Tools.RemoveAll(t => t.Name == "find_task");
        _server.Tools.Add(ToolInfo("find_task", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""));

        var result = await Create().SearchTasksAsync("automatisation", projectId: 1078);

        Assert.Equal("T35647", Assert.Single(result.Items).Code);
        Assert.Equal(1, result.Total);
        Assert.False(_server.Calls.Single(c => c.Tool == "find_task").Args.ContainsKey("project_id"));
    }

    [Fact]
    public async Task Scoped_and_unscoped_task_searches_do_not_share_a_cache_entry()
    {
        var lookup = Create();

        var everywhere = await lookup.SearchTasksAsync("automatisation");
        var inProject = await lookup.SearchTasksAsync("automatisation", projectId: 1078);

        Assert.Equal(2, everywhere.Items.Count);
        Assert.Single(inProject.Items);
        Assert.Equal(2, _server.LookupCalls);
    }

    // ----- what reaches the MCP tool

    [Theory]
    [InlineData("'; DROP TABLE projets; --", "'; DROP TABLE projets; --")]
    [InlineData("{\"name\":\"x\",\"project_id\":5}", "{\"name\":\"x\",\"project_id\":5}")]
    [InlineData("<script>alert(1)</script>", "<script>alert(1)</script>")]
    [InlineData("..\\..\\windows\\system32", "..\\..\\windows\\system32")]
    [InlineData("%_", "%_")]
    [InlineData("  a\n\tb   c  ", "a b c")]
    public async Task A_query_reaches_the_tool_as_one_plain_value_with_no_extra_argument_and_no_side_effect(string query, string expected)
    {
        var lookup = Create();

        var projects = await lookup.SearchProjectsAsync(query);
        var tasks = await lookup.SearchTasksAsync(query);
        var scoped = await lookup.SearchTasksAsync(query, projectId: 1078);

        Assert.True(projects.Succeeded && tasks.Succeeded && scoped.Succeeded);
        Assert.Empty(projects.Items);
        Assert.Empty(tasks.Items);
        Assert.Empty(scoped.Items);

        var calls = _server.Calls.Where(c => c.Tool is "search_projects" or "find_task").ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Equal(expected, call.Args["name"]));
        Assert.Equal(["name"], calls[0].Args.Keys);
        Assert.Equal(["name"], calls[1].Args.Keys);
        Assert.Equal(["name", "project_id"], calls[2].Args.Keys);
        Assert.Equal(1078, calls[2].Args["project_id"]);
        Assert.Empty(_server.Entries);
        Assert.DoesNotContain(_server.Calls, c => c.Tool.StartsWith("plan_") || c.Tool.StartsWith("confirm_"));
    }

    [Fact]
    public async Task Whatever_the_typed_text_the_log_never_receives_it()
    {
        var log = new List<string>();
        var lookup = Create(log: log);

        await lookup.SearchProjectsAsync("mot-de-passe-secret");
        _server.LookupFailure = new HttpRequestException("réseau coupé");
        await lookup.SearchTasksAsync("mot-de-passe-secret");
        await lookup.AutoFillAsync("P1078", "T35647");

        Assert.NotEmpty(log);
        Assert.DoesNotContain(log, line => line.Contains("mot-de-passe-secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_tool_error_shows_a_bounded_message()
    {
        var client = CannedClient.Answering(_ => new ForecastToolResult(true, new string('x', 5000), null));

        var result = await CreateOn(client).SearchProjectsAsync("intranet");

        Assert.False(result.Succeeded);
        Assert.Contains("search_projects", result.Error);
        Assert.True(result.Error!.Length < 500, $"The error is {result.Error.Length} characters long.");
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_an_empty_result_not_an_error()
    {
        var client = CannedClient.Answering(_ => new ForecastToolResult(false, "Internal oops", null));
        var lookup = CreateOn(client);

        var projects = await lookup.SearchProjectsAsync("intranet");
        var tasks = await lookup.SearchTasksAsync("revue");

        Assert.True(projects.Succeeded);
        Assert.Empty(projects.Items);
        Assert.True(tasks.Succeeded);
        Assert.Empty(tasks.Items);
    }

    [Fact]
    public async Task A_task_description_is_one_line_bounded_and_dropped_when_it_only_repeats_the_name()
    {
        var longText = "ligne un\n" + new string('x', 400);
        var client = CannedClient.Answering(tool => tool == "find_task"
            ? Json(JsonSerializer.Serialize(new { task = new { task_id = 7, task_name = "Dév", description = longText, project_id = 1, project_name = "Alpha" } }))
            : Json("""{"found":false}"""));
        var lookup = CreateOn(client);

        var task = (await lookup.GetTaskAsync("T7")).Item;

        Assert.NotNull(task);
        Assert.DoesNotContain('\n', task.Description!);
        Assert.Equal(201, task.Description!.Length);
        Assert.EndsWith("…", task.Description);

        var same = CannedClient.Answering(_ => Json("""{"task":{"task_id":8,"task_name":"Dév","description":"DEV","project_id":1,"project_name":"Alpha"}}"""));
        var repeated = (await CreateOn(same).GetTaskAsync("T8")).Item;
        Assert.Null(repeated?.Description);
        Assert.Equal("Dév", repeated?.Text);
    }

    [Fact]
    public void Parse_projects_ignores_entries_without_a_usable_number_or_name()
    {
        var json = JsonDocument.Parse("""
            {"found":true,"matches":[
              {"id":"UHJvamVjdFR5cGU6","name":"Relay id only"},
              {"id":"12","name":"Numeric id only"},
              {"project_id":5},
              {"project_id":6,"name":"  Complet  "}]}
            """).RootElement;

        var projects = ForecastJson.ParseProjects(json);

        Assert.Equal(["12", "6"], projects.Select(p => p.Id));
        Assert.Equal("Complet", projects[1].Name);
    }

    // ----- cache

    [Fact]
    public async Task The_cache_ignores_case_accents_and_spacing_of_the_query()
    {
        var lookup = Create();

        await lookup.SearchProjectsAsync("ÉQUIPE laurence");
        await lookup.SearchProjectsAsync("  equipe    LAURENCE ");

        Assert.Equal(1, _server.LookupCalls);
    }

    [Fact]
    public async Task The_cache_lives_exactly_the_default_two_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), new ForecastOptions().LookupCacheDuration);
        var lookup = Create();

        await lookup.SearchTasksAsync("revue");
        _clock.Advance(TimeSpan.FromSeconds(119));
        await lookup.SearchTasksAsync("revue");
        Assert.Equal(1, _server.LookupCalls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await lookup.SearchTasksAsync("revue");
        Assert.Equal(2, _server.LookupCalls);
    }

    [Fact]
    public async Task A_failed_task_search_is_not_cached_and_a_zero_duration_disables_the_task_cache_too()
    {
        var lookup = Create();
        _server.LookupFailure = new HttpRequestException();
        Assert.False((await lookup.SearchTasksAsync("revue")).Succeeded);
        _server.LookupFailure = null;
        Assert.NotEmpty((await lookup.SearchTasksAsync("revue")).Items);

        var uncached = Create(options: new ForecastOptions { LookupCacheDuration = TimeSpan.Zero });
        var before = _server.LookupCalls;
        await uncached.SearchTasksAsync("revue");
        await uncached.SearchTasksAsync("revue");

        Assert.Equal(before + 2, _server.LookupCalls);
    }

    [Fact]
    public async Task The_cache_is_bounded_the_oldest_answers_are_dropped_and_the_latest_one_stays()
    {
        var lookup = Create();
        for (var i = 1; i <= 250; i++)
            await lookup.SearchProjectsAsync($"chantier {i:000}");
        var before = _server.LookupCalls;

        await lookup.SearchProjectsAsync("chantier 250");
        Assert.Equal(before, _server.LookupCalls);
        await lookup.SearchProjectsAsync("chantier 001");

        Assert.Equal(before + 1, _server.LookupCalls);
    }

    // ----- cancellation and session lifetime

    [Fact]
    public async Task Cancelling_while_forecast_answers_is_propagated_by_every_entry_point_and_closes_the_session()
    {
        Func<ForecastLookup, CancellationToken, Task>[] calls =
        [
            (l, t) => l.SearchProjectsAsync("intranet", t),
            (l, t) => l.SearchTasksAsync("revue", null, t),
            (l, t) => l.GetProjectAsync("P1078", t),
            (l, t) => l.GetTaskAsync("T35647", t),
            (l, t) => l.AutoFillAsync("P1078", "T35647", t),
        ];

        foreach (var call in calls)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new CannedClient(async (_, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Raw("unreachable");
            });
            using var cts = new CancellationTokenSource();

            var running = call(CreateOn(client), cts.Token);
            await started.Task;
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(1, client.Disposed);
        }
    }

    [Fact]
    public async Task Cancelling_while_the_connection_is_opening_is_propagated_not_reported_as_an_error()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(new BlockingConnector()).SearchProjectsAsync("intranet", cts.Token));
    }

    [Fact]
    public async Task Every_search_that_asks_forecast_closes_its_session_and_a_cached_one_opens_none()
    {
        var client = CannedClient.Answering(_ => Json("""{"found":false}"""));
        var connector = new FakeForecastConnector(client);
        var lookup = CreateOn(client, connector);

        await lookup.SearchProjectsAsync("intranet");
        await lookup.SearchProjectsAsync("intranet");

        Assert.Equal(1, connector.Connects);
        Assert.Equal(1, client.Disposed);
    }

    [Fact]
    public async Task A_failing_call_still_closes_its_session()
    {
        var client = new CannedClient((_, _, _) => throw new HttpRequestException("reset"));

        var result = await CreateOn(client).SearchTasksAsync("revue");

        Assert.False(result.Succeeded);
        Assert.Equal(1, client.Disposed);
    }

    [Fact]
    public async Task A_failure_while_listing_the_tools_closes_the_client_and_is_a_readable_error()
    {
        var client = CannedClient.Answering(_ => Json("""{"found":false}"""));
        client.ListToolsFailure = new HttpRequestException("reset");

        var result = await CreateOn(client).SearchProjectsAsync("intranet");

        Assert.Contains("injoignable", result.Error);
        Assert.Equal(1, client.Disposed);
    }

    // ----- auto-fill

    [Fact]
    public async Task Auto_fill_asks_forecast_through_one_single_session()
    {
        var both = new FakeForecastConnector(_server);
        await Create(both).AutoFillAsync("P1078", "T35647");
        var callsOfBoth = _server.LookupCalls;

        var taskOnly = new FakeForecastConnector(_server);
        await Create(taskOnly).AutoFillAsync(null, "T35700");

        Assert.Equal(1, both.Connects);
        Assert.True(callsOfBoth >= 2);
        Assert.Equal(1, taskOnly.Connects);
        Assert.True(_server.LookupCalls - callsOfBoth >= 2);
    }

    [Fact]
    public async Task Auto_fill_of_a_task_keeps_what_the_task_says_of_its_project_when_the_project_cannot_be_read_again()
    {
        var client = CannedClient.Answering(tool => tool == "search_projects"
            ? throw new HttpRequestException("reset")
            : Json("""{"task":{"task_id":35647,"task_name":"Automatisation","project_id":1078,"project_name":"Intranet"}}"""));

        var result = await CreateOn(client).AutoFillAsync(null, "T35647");

        Assert.True(result.Succeeded);
        Assert.Equal("T35647", result.Task?.Code);
        Assert.Equal(new ForecastProjectMatch(1078, "P1078", "Intranet", null, null), result.Project);
        Assert.Empty(result.Warnings);
        Assert.Equal("Intranet / Automatisation", result.Description);
    }

    [Fact]
    public async Task Auto_fill_drops_a_task_without_project_when_a_project_was_typed()
    {
        var client = CannedClient.Answering(tool => tool == "search_projects"
            ? Json("""{"found":true,"project_id":1078,"custom_project_id":"P1078","name":"Intranet","client":"Acme","status":"RUNNING"}""")
            : Json("""{"task":{"task_id":200,"task_name":"Réunions"}}"""));

        var result = await CreateOn(client).AutoFillAsync("P1078", "T200");

        Assert.Equal(1078, result.Project?.Id);
        Assert.Null(result.Task);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("T200", warning);
        Assert.Contains("aucun projet", warning);
        Assert.Equal("Intranet", result.Description);
    }

    [Fact]
    public async Task Auto_fill_with_two_unknown_numbers_gives_two_warnings_and_nothing_to_fill()
    {
        var client = CannedClient.Answering(tool => tool == "search_projects"
            ? Json("""{"found":false,"all_projects":[{"project_id":1,"name":"Suggestion"}]}""")
            : Json("""{"found":false,"count":0,"tasks":[]}"""));

        var result = await CreateOn(client).AutoFillAsync("P9999", "T99999");

        Assert.True(result.Succeeded);
        Assert.Null(result.Project);
        Assert.Null(result.Task);
        Assert.Null(result.Description);
        Assert.Equal(2, result.Warnings.Count);
    }

    // ----- numbers

    [Theory]
    [InlineData("123456", true)]
    [InlineData("1234567", false)]
    [InlineData("P1234567", false)]
    [InlineData("P 1078", false)]
    [InlineData("1 078", false)]
    public void A_project_number_has_one_to_six_digits_and_nothing_else(string text, bool isNumber) =>
        Assert.Equal(isNumber, ForecastLookup.TryParseProjectNumber(text, out _, out _));

    [Theory]
    [InlineData("1234567", true)]
    [InlineData("12345678", false)]
    [InlineData("T12345678", false)]
    [InlineData("T 35647", false)]
    public void A_task_number_has_one_to_seven_digits_and_nothing_else(string text, bool isNumber) =>
        Assert.Equal(isNumber, ForecastLookup.TryParseTaskNumber(text, out _, out _));

    // ----- doubles

    private static ForecastToolInfo ToolInfo(string name, string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return new ForecastToolInfo(name, null, doc.RootElement.Clone());
    }

    private static ForecastToolResult Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ForecastToolResult(false, json, doc.RootElement.Clone());
    }

    private static ForecastToolResult Raw(string text) => new(false, text, null);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>A Forecast server that answers with what the test says, offering only the two lookup tools; counts how often it is closed.</summary>
    private sealed class CannedClient(Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<ForecastToolResult>> handler) : IForecastToolClient
    {
        public int Disposed { get; private set; }

        public Exception? ListToolsFailure { get; set; }

        public static CannedClient Answering(Func<string, ForecastToolResult> answer) =>
            new((tool, _, _) => Task.FromResult(answer(tool)));

        public Task<IReadOnlyList<ForecastToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            ListToolsFailure is not null
                ? throw ListToolsFailure
                : Task.FromResult<IReadOnlyList<ForecastToolInfo>>(
                [
                    ToolInfo("search_projects", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""),
                    ToolInfo("find_task", """{"type":"object","properties":{"name":{"type":"string"},"project_id":{"type":"integer"}},"required":["name"]}"""),
                ]);

        public Task<ForecastToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
            handler(toolName, arguments, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingConnector : IForecastConnector
    {
        public bool HasCredentials => true;

        public async Task<IForecastToolClient> ConnectAsync(bool interactive, Action<string>? progress = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + exception);
    }
}
