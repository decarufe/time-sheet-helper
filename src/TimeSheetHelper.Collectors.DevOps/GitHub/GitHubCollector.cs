using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.DevOps.GitHub;

/// <summary>
/// Commits, pull requests (opened, reviewed, commented), issue comments and workflow runs of the authenticated user.
/// </summary>
public sealed class GitHubCollector(
    GitHubApiClient api,
    GitHubTokenProvider tokens,
    IActivitySink sink,
    IOptions<GitHubOptions> options,
    ILogger<GitHubCollector> logger) : ICollector
{
    internal const string MissingTokenMessage = "Token GitHub manquant : enregistrez-le dans les paramètres ou connectez-vous avec « gh auth login ».";

    public string Name => "github";

    public async Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => await tokens.GetTokenAsync(cancellationToken) is null
            ? new CollectorStatus(false, MissingTokenMessage)
            : new CollectorStatus(true);

    public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var token = await tokens.GetTokenAsync(cancellationToken);
        if (token is null)
        {
            return new CollectorResult(0, [MissingTokenMessage]);
        }

        var run = new SyncRun(api, sink, options.Value, token, fromUtc, toUtc, cancellationToken);
        try
        {
            await run.ExecuteAsync();
        }
        catch (ApiRateLimitException ex)
        {
            run.Warnings.Add(ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            run.Warnings.Add("Token GitHub refusé (401) : il est peut-être expiré ou révoqué.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "GitHub sync failed");
            run.Warnings.Add($"Erreur GitHub : {ex.Message}");
        }
        return new CollectorResult(run.Count, run.Warnings);
    }

    private sealed class SyncRun(GitHubApiClient api, IActivitySink sink, GitHubOptions options, string token, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
        private readonly HashSet<string> _repos = new(StringComparer.OrdinalIgnoreCase);
        private readonly GitHubOptions _options = options;
        private string _login = "";

        public List<string> Warnings { get; } = [];
        public int Count { get; private set; }

        private string Range => $"{JsonExtensions.Iso(fromUtc)}..{JsonExtensions.Iso(toUtc.AddSeconds(-1))}";
        private string Since => Uri.EscapeDataString(JsonExtensions.Iso(fromUtc));

        public async Task ExecuteAsync()
        {
            var user = await api.GetAsync(token, "user", cancellationToken);
            _login = user.Str("login") ?? throw new HttpRequestException("GitHub : réponse /user sans login.");

            await CollectCommitsAsync();
            await CollectPullRequestsAsync();
            await CollectIssueCommentsAsync();
            await CollectWorkflowRunsAsync();
        }

        private async Task CollectCommitsAsync()
        {
            var query = Search($"author:{_login} author-date:{Range}");
            await foreach (var item in Paged($"search/commits?q={query}&per_page=100", "items"))
            {
                await EmitAsync(GitHubMapper.MapCommit(item));
            }
        }

        private async Task CollectPullRequestsAsync()
        {
            var query = Search($"type:pr involves:{_login} updated:{Range}");
            foreach (var item in await ListAsync($"search/issues?q={query}&per_page=100", "items"))
            {
                if (GitHubMapper.IssueFromSearchItem(item) is not { } pr || !_options.IsIncluded(pr.Repo))
                {
                    continue;
                }
                _repos.Add(pr.Repo);
                await EmitAsync(GitHubMapper.MapPullRequestOpened(item, _login));

                await ForRepoAsync(pr.Repo, async () =>
                {
                    var prefix = $"repos/{pr.Repo}";
                    await foreach (var review in Paged($"{prefix}/pulls/{pr.Number}/reviews?per_page=100", null))
                    {
                        await EmitAsync(GitHubMapper.MapReview(review, pr, _login));
                    }
                    await foreach (var comment in Paged($"{prefix}/pulls/{pr.Number}/comments?since={Since}&per_page=100", null))
                    {
                        await EmitAsync(GitHubMapper.MapReviewComment(comment, pr, _login));
                    }
                    await foreach (var comment in Paged($"{prefix}/issues/{pr.Number}/comments?since={Since}&per_page=100", null))
                    {
                        await EmitAsync(GitHubMapper.MapIssueComment(comment, pr, _login));
                    }
                });
            }
        }

        private async Task CollectIssueCommentsAsync()
        {
            var query = Search($"type:issue commenter:{_login} updated:{Range}");
            foreach (var item in await ListAsync($"search/issues?q={query}&per_page=100", "items"))
            {
                if (GitHubMapper.IssueFromSearchItem(item) is not { } issue || !_options.IsIncluded(issue.Repo))
                {
                    continue;
                }
                _repos.Add(issue.Repo);
                await ForRepoAsync(issue.Repo, async () =>
                {
                    await foreach (var comment in Paged($"repos/{issue.Repo}/issues/{issue.Number}/comments?since={Since}&per_page=100", null))
                    {
                        await EmitAsync(GitHubMapper.MapIssueComment(comment, issue, _login));
                    }
                });
            }
        }

        private async Task CollectWorkflowRunsAsync()
        {
            var created = Uri.EscapeDataString(Range);
            foreach (var repo in _repos.Order(StringComparer.OrdinalIgnoreCase).ToList())
            {
                await ForRepoAsync(repo, async () =>
                {
                    var url = $"repos/{repo}/actions/runs?actor={Uri.EscapeDataString(_login)}&created={created}&per_page=100";
                    await foreach (var workflowRun in Paged(url, "workflow_runs"))
                    {
                        await EmitAsync(GitHubMapper.MapWorkflowRun(workflowRun, repo));
                    }
                }, quietWhenInaccessible: true);
            }
        }

        /// <summary>Per-repository calls: an inaccessible repository must not abort the whole sync.</summary>
        private async Task ForRepoAsync(string repo, Func<Task> action, bool quietWhenInaccessible = false)
        {
            try
            {
                await action();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
            {
                if (!quietWhenInaccessible)
                {
                    Warnings.Add($"GitHub : {repo} inaccessible ({(int)ex.StatusCode!}).");
                }
            }
        }

        private async Task EmitAsync(ActivityEvent? activity)
        {
            if (activity is null
                || activity.StartUtc < fromUtc || activity.StartUtc >= toUtc
                || !_options.IsIncluded(activity.Repo)
                || !_emitted.Add(activity.ExternalId!))
            {
                return;
            }
            if (activity.Repo is not null)
            {
                _repos.Add(activity.Repo);
            }
            await sink.WriteAsync(activity, cancellationToken);
            Count++;
        }

        private IAsyncEnumerable<JsonElement> Paged(string url, string? itemsProperty)
            => api.GetPagedAsync(token, url, itemsProperty, _options.MaxPages, Warnings, cancellationToken);

        /// <summary>Materializes a search so follow-up calls do not interleave with its pagination.</summary>
        private async Task<List<JsonElement>> ListAsync(string url, string itemsProperty)
        {
            var items = new List<JsonElement>();
            await foreach (var item in Paged(url, itemsProperty))
            {
                items.Add(item);
            }
            return items;
        }

        private static string Search(string query) => Uri.EscapeDataString(query);
    }
}
