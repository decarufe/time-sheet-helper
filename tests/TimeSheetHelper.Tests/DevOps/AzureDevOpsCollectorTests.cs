using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.AzureDevOps;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Tests.DevOps;

public class AzureDevOpsCollectorTests
{
    private static readonly DateTime From = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
    private const string Me = "11111111-aaaa-bbbb-cccc-000000000001";

    private readonly FakeHttpMessageHandler _http = new();
    private readonly RecordingSink _sink = new();
    private FakeActivityHistory _history = new();

    private static FakeHttpMessageHandler Recorded(FakeHttpMessageHandler http) => http
        .OnFixture("/contoso/_apis/connectionData", "AzureDevOps/connection-data.json")
        .OnFixture("/contoso/_apis/wit/wiql", "AzureDevOps/wiql.json")
        .OnFixture("/contoso/_apis/wit/workitems", "AzureDevOps/workitems.json", "ids=101,102")
        .OnFixture("/contoso/_apis/wit/workitems/101/updates", "AzureDevOps/updates-101.json")
        .On("/contoso/_apis/wit/workitems/102/updates", """{"count":0,"value":[]}""")
        .OnFixture("/contoso/_apis/projects", "AzureDevOps/projects.json")
        .OnFixture("/contoso/p1/_apis/git/pullrequests", "AzureDevOps/prs-created.json", $"searchCriteria.creatorId={Me}", "status=active")
        .OnFixture("/contoso/p1/_apis/git/pullrequests", "AzureDevOps/prs-reviewed.json", $"searchCriteria.reviewerId={Me}", "queryTimeRangeType=closed", "minTime=2026-10-05T00:00:00Z")
        .On("/contoso/p1/_apis/git/pullrequests", """{"count":0,"value":[]}""")
        .OnFixture("/contoso/p1/_apis/git/repositories/r1/pullRequests/501/threads", "AzureDevOps/threads-501.json")
        .OnFixture("/contoso/p1/_apis/git/repositories/r1/pullRequests/480/threads", "AzureDevOps/threads-480.json")
        .OnFixture("/contoso/p1/_apis/build/builds", "AzureDevOps/builds.json", "requestedFor=eric@contoso.com")
        .OnFixture("/contoso/p1/_apis/git/repositories", "AzureDevOps/repositories.json")
        .OnFixture("/contoso/p1/_apis/git/repositories/r1/commits", "AzureDevOps/commits.json", "searchCriteria.author=eric@contoso.com");

    private AzureDevOpsCollector CreateCollector(AzureDevOpsOptions? options = null, string? pat = "my-pat")
    {
        var secrets = new FakeSecretStore(pat is null ? [] : new() { [SecretNames.AzureDevOpsPat] = pat });
        return new AzureDevOpsCollector(
            new AzureDevOpsApiClient(new HttpClient(_http)),
            secrets,
            _sink,
            _history,
            Options.Create(options ?? new AzureDevOpsOptions { Organizations = ["contoso"] }),
            NullLogger<AzureDevOpsCollector>.Instance);
    }

    [Fact]
    public async Task Sync_maps_work_items_pull_requests_builds_and_commits()
    {
        Recorded(_http);

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Equal(
            ["wi:contoso/101/2", "wi:contoso/101/4", "pr:contoso/501", "pr-thread:contoso/501/1/1",
             "pr-thread:contoso/480/10/1", "pr-thread:contoso/480/11/1", "build:contoso/9001",
             "commit:c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00"],
            _sink.Events.Select(e => e.ExternalId!).ToArray());
        Assert.Equal(8, result.EventCount);
        Assert.All(_sink.Events, e => Assert.Equal(ActivitySource.AzureDevOps, e.Source));
        Assert.All(_sink.Events, e => Assert.Equal("contoso/Timesheets", e.Project));

        var update = _sink.Events[0];
        Assert.Equal("workitem-update", update.Kind);
        Assert.Equal("#101 Export timesheet to Forecast", update.Subject);
        Assert.Equal(new DateTime(2026, 10, 6, 13, 20, 0, DateTimeKind.Utc), update.StartUtc);
        Assert.Equal("https://dev.azure.com/contoso/Timesheets/_workitems/edit/101", update.Url);

        Assert.Equal("pr-opened", _sink.Events[2].Kind);
        Assert.Equal("timesheet-api", _sink.Events[2].Repo);
        Assert.Equal("pr-review", _sink.Events[4].Kind);
        Assert.Equal("pr-comment", _sink.Events[5].Kind);
        Assert.Equal("PR 480 Rounding rules", _sink.Events[5].Subject);

        var build = _sink.Events[6];
        Assert.Equal("build", build.Kind);
        Assert.Equal(TimeSpan.FromMinutes(12), build.Duration);
        Assert.Equal("timesheet-api-ci 20261006.3", build.Subject);

        Assert.Equal("Round to 15 minutes", _sink.Events[7].Subject);

        // PR 300 was closed before the range: its threads are never fetched.
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/300/"));
        // Disabled repositories are not scanned.
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/r2/"));

        var expectedAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes(":my-pat"));
        Assert.All(_http.Requests, r => Assert.Equal(expectedAuth, r.Headers.Authorization?.Parameter));
        Assert.Contains(_http.Bodies, b => b?.Contains("[System.ChangedBy] EVER @Me") == true);
    }

    [Fact]
    public async Task Wiql_falls_back_when_ever_is_rejected()
    {
        _http.On("/contoso/_apis/wit/wiql", [], request =>
        {
            var body = request.Content!.ReadAsStringAsync().Result;
            return body.Contains("EVER")
                ? FakeHttpMessageHandler.Json("""{"message":"TF51005"}""", HttpStatusCode.BadRequest)
                : FakeHttpMessageHandler.Json("""{"workItems":[]}""");
        });
        Recorded(_http);

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Equal(6, result.EventCount);
        Assert.Contains(_http.Bodies, b => b?.Contains("[System.ChangedBy] = @Me") == true);
    }

    [Fact]
    public async Task Invalid_pat_gives_warning_per_organization()
    {
        _http.On("/contoso/_apis/connectionData", [], _ =>
            new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html") });

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Equal(0, result.EventCount);
        Assert.Contains("accès refusé", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Throttling_stops_with_warning()
    {
        _http.OnFixture("/contoso/_apis/connectionData", "AzureDevOps/connection-data.json")
            .On("/contoso/_apis/wit/wiql", [], _ => FakeHttpMessageHandler.Json("{}", HttpStatusCode.TooManyRequests));

        var result = await CreateCollector(new AzureDevOpsOptions { Organizations = ["contoso", "fabrikam"] }).SyncAsync(From, To);

        Assert.Contains("429", Assert.Single(result.Warnings));
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("fabrikam"));
    }

    [Fact]
    public async Task Status_reports_missing_pat_then_missing_organizations()
    {
        var noPat = await CreateCollector(pat: null).GetStatusAsync();
        var noOrg = await CreateCollector(new AzureDevOpsOptions()).GetStatusAsync();
        var ok = await CreateCollector().GetStatusAsync();

        Assert.False(noPat.IsConfigured);
        Assert.Contains("PAT Azure DevOps manquant", noPat.Message);
        Assert.False(noOrg.IsConfigured);
        Assert.Contains("organisation", noOrg.Message);
        Assert.True(ok.IsConfigured);
    }

    private const string TwoProjects = """
        {"value":[
          {"id":"p1","name":"Timesheets","lastUpdateTime":"2026-09-01T00:00:00Z"},
          {"id":"p9","name":"Unrelated","lastUpdateTime":"2026-10-01T00:00:00Z"}
        ]}
        """;

    [Fact]
    public async Task Only_projects_with_my_activity_are_scanned()
    {
        _http.On("/contoso/_apis/projects", TwoProjects);
        Recorded(_http);

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Equal(8, result.EventCount);
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/p9/"));
    }

    [Fact]
    public async Task Without_any_trace_only_most_recent_projects_are_scanned_with_warning()
    {
        _http.On("/contoso/_apis/wit/wiql", """{"workItems":[]}""")
            .On("/contoso/_apis/projects", TwoProjects)
            .On("/contoso/p9/_apis/git/pullrequests", """{"value":[]}""")
            .On("/contoso/p9/_apis/build/builds", """{"value":[]}""")
            .On("/contoso/p9/_apis/git/repositories", """{"value":[]}""");
        Recorded(_http);

        var result = await CreateCollector(new AzureDevOpsOptions { Organizations = ["contoso"], MaxProjects = 1 }).SyncAsync(From, To);

        Assert.Contains("AzureDevOps:Projects", Assert.Single(result.Warnings));
        Assert.Contains(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/p9/"));
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/p1/"));
    }

    [Fact]
    public async Task Projects_of_local_clones_are_scanned()
    {
        _history = new FakeActivityHistory(new ActivityEvent
        {
            Source = ActivitySource.LocalGit, StartUtc = From.AddDays(-3), Repo = "unrelated-api", Project = "contoso/Unrelated",
        });
        _http.On("/contoso/_apis/wit/wiql", """{"workItems":[]}""")
            .On("/contoso/_apis/projects", TwoProjects)
            .On("/contoso/p9/_apis/git/pullrequests", """{"value":[]}""")
            .On("/contoso/p9/_apis/build/builds", """{"value":[]}""")
            .On("/contoso/p9/_apis/git/repositories", """{"value":[]}""");
        Recorded(_http);

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Contains(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/p9/"));
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("/p1/"));
    }

    [Fact]
    public async Task Organization_level_pull_request_search_replaces_per_project_queries()
    {
        _http.On("/contoso/_apis/git/pullrequests", """
            {"value":[{"pullRequestId":777,"title":"Org-wide","creationDate":"2026-10-07T15:00:00Z","status":"active",
              "createdBy":{"id":"11111111-aaaa-bbbb-cccc-000000000001"},
              "repository":{"id":"r1","name":"timesheet-api","project":{"id":"p1","name":"Timesheets"}}}]}
            """)
            .On("/contoso/p1/_apis/git/repositories/r1/pullRequests/777/threads", """{"value":[]}""");
        Recorded(_http);

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Contains(_sink.Events, e => e.ExternalId == "pr:contoso/777");
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/p1/_apis/git/pullrequests"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Skip_paging_requests_next_page_when_full()
    {
        var page = "{\"value\":[" + string.Join(',', Enumerable.Range(0, AzureDevOpsApiClient.PageSize).Select(i => $"{{\"id\":{i}}}")) + "]}";
        _http.On("/org/_apis/things", page, "$skip=0")
            .On("/org/_apis/things", """{"value":[{"id":100}]}""", "$skip=100");
        var api = new AzureDevOpsApiClient(new HttpClient(_http));

        var count = 0;
        await foreach (var _ in api.GetValuesAsync("pat", "https://dev.azure.com/org/_apis/things?api-version=7.1", AzureDevOpsPaging.Skip, 10, []))
        {
            count++;
        }

        Assert.Equal(101, count);
        Assert.Equal(2, _http.Requests.Count);
    }
}
