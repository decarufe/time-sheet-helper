using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Collectors.DevOps.GitHub;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Tests.DevOps;

public class GitHubCollectorTests
{
    private static readonly DateTime From = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeHttpMessageHandler _http = new();
    private readonly RecordingSink _sink = new();

    private static FakeHttpMessageHandler Recorded(FakeHttpMessageHandler http) => http
        .OnFixture("/user", "GitHub/user.json")
        .OnFixture("/search/commits", "GitHub/search-commits.json", "author:edecarufel", "author-date:2026-10-05T00:00:00Z..2026-10-11T23:59:59Z")
        .OnFixture("/search/issues", "GitHub/search-prs.json", "type:pr", "involves:edecarufel")
        .OnFixture("/search/issues", "GitHub/search-issues.json", "type:issue", "commenter:edecarufel")
        .On("/repos/acme/api/pulls/42/reviews", "[]")
        .On("/repos/acme/api/pulls/42/comments", "[]")
        .OnFixture("/repos/acme/api/issues/42/comments", "GitHub/issue-comments-api-42.json", "since=2026-10-05T00:00:00Z")
        .OnFixture("/repos/acme/web/pulls/7/reviews", "GitHub/reviews-web-7.json")
        .OnFixture("/repos/acme/web/pulls/7/comments", "GitHub/review-comments-web-7.json")
        .On("/repos/acme/web/issues/7/comments", "[]")
        .OnFixture("/repos/acme/api/issues/40/comments", "GitHub/issue-comments-api-40.json")
        .OnFixture("/repos/acme/api/actions/runs", "GitHub/workflow-runs-api.json", "actor=edecarufel");

    private GitHubCollector CreateCollector(GitHubOptions? options = null, ISecretStore? secrets = null, IProcessRunner? processes = null)
    {
        var api = new GitHubApiClient(new HttpClient(_http) { BaseAddress = new Uri("https://api.github.com/") });
        var tokens = new GitHubTokenProvider(
            secrets ?? new FakeSecretStore(new() { [SecretNames.GitHubToken] = "ghp_test" }),
            processes ?? new FakeProcessRunner((_, _) => null));
        return new GitHubCollector(api, tokens, _sink, Options.Create(options ?? new GitHubOptions()), NullLogger<GitHubCollector>.Instance);
    }

    [Fact]
    public async Task Sync_maps_all_activity_kinds()
    {
        Recorded(_http);

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Equal(8, result.EventCount);
        Assert.Equal(8, _sink.Events.Count);
        Assert.All(_sink.Events, e => Assert.Equal(ActivitySource.GitHub, e.Source));
        Assert.Equal(
            ["commit:a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "commit:ffeeddccbbaa99887766554433221100ffeeddcc",
             "pr:acme/api#42", "issue-comment:5001", "pr-review:9001", "pr-review-comment:7001",
             "issue-comment:5101", "workflow-run:30433642"],
            _sink.Events.Select(e => e.ExternalId!).ToArray());

        var commit = _sink.Events[0];
        Assert.Equal("commit", commit.Kind);
        Assert.Equal(new DateTime(2026, 10, 6, 14, 15, 0, DateTimeKind.Utc), commit.StartUtc);
        Assert.Equal("acme/api", commit.Repo);
        Assert.Equal("acme", commit.Project);
        Assert.Equal("Add timesheet export endpoint", commit.Subject);

        var review = _sink.Events.Single(e => e.Kind == "pr-review");
        Assert.Equal("#7 New dashboard", review.Subject);
        Assert.Equal("acme/web", review.Repo);
        Assert.Equal("https://github.com/acme/web/pull/7#pullrequestreview-9001", review.Url);

        Assert.Equal("issue-comment", _sink.Events.Single(e => e.ExternalId == "issue-comment:5101").Kind);
        Assert.Equal("pr-comment", _sink.Events.Single(e => e.ExternalId == "issue-comment:5001").Kind);

        var run = _sink.Events.Single(e => e.Kind == "workflow-run");
        Assert.Equal(TimeSpan.FromMinutes(7.5), run.Duration);
        Assert.Equal("CI : Add timesheet export endpoint", run.Subject);

        // Never keep bodies.
        Assert.DoesNotContain(_sink.Events, e => e.Subject?.Contains("nits") == true || e.Subject?.Contains("Reproduced") == true);
        Assert.All(_http.Requests, r => Assert.Equal("ghp_test", r.Headers.Authorization?.Parameter));
    }

    [Fact]
    public async Task Organizations_filter_drops_other_owners()
    {
        Recorded(_http);

        var result = await CreateCollector(new GitHubOptions { Organizations = ["ACME"] }).SyncAsync(From, To);

        Assert.Equal(7, result.EventCount);
        Assert.DoesNotContain(_sink.Events, e => e.Repo == "other-org/tool");
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.Contains("other-org"));
    }

    [Fact]
    public async Task Rate_limit_stops_gracefully_with_warning()
    {
        _http.OnFixture("/user", "GitHub/user.json")
            .On("/search/commits", [], _ =>
            {
                var response = FakeHttpMessageHandler.Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);
                response.Headers.Add("x-ratelimit-remaining", "0");
                response.Headers.Add("x-ratelimit-reset", "1791640800");
                return response;
            });

        var result = await CreateCollector().SyncAsync(From, To);

        Assert.Equal(0, result.EventCount);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("limite", warning);
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/search/issues"));
    }

    [Fact]
    public async Task Missing_token_reports_status_and_warning()
    {
        var collector = CreateCollector(secrets: new FakeSecretStore(), processes: new FakeProcessRunner((_, _) => FakeProcessRunner.Fail()));

        var status = await collector.GetStatusAsync();
        var result = await collector.SyncAsync(From, To);

        Assert.False(status.IsConfigured);
        Assert.Contains("Token GitHub manquant", status.Message);
        Assert.Equal(0, result.EventCount);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Token_falls_back_to_gh_cli()
    {
        var processes = new FakeProcessRunner((file, args) => file == "gh" && args.SequenceEqual(["auth", "token"]) ? FakeProcessRunner.Ok("gho_cli\n") : null);
        var provider = new GitHubTokenProvider(new FakeSecretStore(), processes);

        Assert.Equal("gho_cli", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task Pagination_follows_link_header()
    {
        _http.On("/repos/acme/api/pulls/1/reviews", [], request =>
        {
            if (request.RequestUri!.Query.Contains("page=2"))
            {
                return FakeHttpMessageHandler.Json("""[{"id":2}]""");
            }
            var first = FakeHttpMessageHandler.Json("""[{"id":1}]""");
            first.Headers.TryAddWithoutValidation("Link",
                "<https://api.github.com/repos/acme/api/pulls/1/reviews?per_page=1&page=2>; rel=\"next\", <https://api.github.com/repos/acme/api/pulls/1/reviews?per_page=1&page=2>; rel=\"last\"");
            return first;
        });
        var api = new GitHubApiClient(new HttpClient(_http) { BaseAddress = new Uri("https://api.github.com/") });
        var warnings = new List<string>();

        var ids = new List<long?>();
        await foreach (var item in api.GetPagedAsync("t", "repos/acme/api/pulls/1/reviews?per_page=1", null, 10, warnings))
        {
            ids.Add(item.GetProperty("id").GetInt64());
        }

        Assert.Equal([1, 2], ids);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Mapper_skips_pending_reviews_and_other_users()
    {
        var pr = new GitHubIssueRef("acme/web", 7, "New dashboard", "https://github.com/acme/web/pull/7", true);
        using var reviews = JsonDocument.Parse(Fixture.Read("GitHub/reviews-web-7.json"));

        var mapped = reviews.RootElement.EnumerateArray()
            .Select(r => GitHubMapper.MapReview(r, pr, "EDECARUFEL"))
            .OfType<ActivityEvent>()
            .ToList();

        var review = Assert.Single(mapped);
        Assert.Equal("pr-review:9001", review.ExternalId);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/acme/api", "acme/api")]
    [InlineData("https://ghe.example.com/api/v3/repos/team/tool/", "team/tool")]
    [InlineData("https://api.github.com/users/acme", null)]
    public void Repo_is_extracted_from_api_url(string url, string? expected)
        => Assert.Equal(expected, GitHubMapper.RepoFromApiUrl(url));
}
