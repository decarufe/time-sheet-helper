using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Collectors.DevOps.AzureDevOps;

/// <summary>
/// Per organization: work item revisions, pull requests (opened, voted, commented), requested builds and commits
/// of the PAT owner.
/// </summary>
public sealed class AzureDevOpsCollector(
    AzureDevOpsApiClient api,
    ISecretStore secrets,
    IActivitySink sink,
    IOptions<AzureDevOpsOptions> options,
    ILogger<AzureDevOpsCollector> logger) : ICollector
{
    internal const string MissingPatMessage = "PAT Azure DevOps manquant : enregistrez-le dans les paramètres.";
    internal const string MissingOrganizationsMessage = "Aucune organisation Azure DevOps configurée (AzureDevOps:Organizations).";
    private const string ApiVersion = "api-version=7.1";

    public string Name => "azuredevops";

    public Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secrets.Get(SecretNames.AzureDevOpsPat)))
        {
            return Task.FromResult(new CollectorStatus(false, MissingPatMessage));
        }
        return Task.FromResult(Organizations().Count == 0
            ? new CollectorStatus(false, MissingOrganizationsMessage)
            : new CollectorStatus(true));
    }

    public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var pat = secrets.Get(SecretNames.AzureDevOpsPat)?.Trim();
        if (string.IsNullOrEmpty(pat))
        {
            return new CollectorResult(0, [MissingPatMessage]);
        }
        var organizations = Organizations();
        if (organizations.Count == 0)
        {
            return new CollectorResult(0, [MissingOrganizationsMessage]);
        }

        var settings = options.Value;
        var run = new SyncRun(api, sink, settings, pat, fromUtc, toUtc, cancellationToken);
        foreach (var organization in organizations)
        {
            try
            {
                await run.CollectOrganizationAsync(organization);
            }
            catch (ApiRateLimitException ex)
            {
                run.Warnings.Add(ex.Message);
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                run.Warnings.Add($"Azure DevOps ({organization}) : accès refusé, le PAT est peut-être expiré ou n'a pas les droits requis.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Azure DevOps sync failed for {Organization}", organization);
                run.Warnings.Add($"Azure DevOps ({organization}) : {ex.Message}");
            }
        }
        return new CollectorResult(run.Count, run.Warnings);
    }

    private List<string> Organizations() => options.Value.Organizations
        .Where(o => !string.IsNullOrWhiteSpace(o))
        .Select(o => o.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private sealed class SyncRun(
        AzureDevOpsApiClient api, IActivitySink sink, AzureDevOpsOptions settings, string pat,
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
        private readonly string _baseUrl = settings.BaseUrl.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/";

        public List<string> Warnings { get; } = [];
        public int Count { get; private set; }

        public async Task CollectOrganizationAsync(string organization)
        {
            var org = $"{_baseUrl}{Uri.EscapeDataString(organization)}";
            var connection = await api.GetAsync(pat, $"{org}/_apis/connectionData", cancellationToken);
            var me = AzureDevOpsMapper.ParseIdentity(connection)
                ?? throw new HttpRequestException("Identité introuvable (connectionData).", null, HttpStatusCode.Unauthorized);

            await CollectWorkItemsAsync(organization, org, me);

            var projects = new List<JsonElement>();
            await foreach (var project in Values($"{org}/_apis/projects?{ApiVersion}", AzureDevOpsPaging.ContinuationToken))
            {
                projects.Add(project);
            }

            foreach (var project in projects)
            {
                if (project.Str("id") is not { } projectId)
                {
                    continue;
                }
                var projectName = project.Str("name");
                var projectUrl = $"{org}/{projectId}";
                await CollectPullRequestsAsync(organization, projectUrl, me);
                await CollectBuildsAsync(organization, projectUrl, me);
                if (settings.CollectCommits && me.Email is not null)
                {
                    await CollectCommitsAsync(organization, projectUrl, projectName, me);
                }
            }
        }

        private async Task CollectWorkItemsAsync(string organization, string org, AzureDevOpsIdentity me)
        {
            // ChangedDate has day precision in WIQL; the day before absorbs time-zone shifts, revisions are filtered precisely below.
            var since = fromUtc.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            JsonElement result;
            try
            {
                // EVER matches items I changed at some point even if someone else changed them afterwards.
                result = await Wiql(org, $"SELECT [System.Id] FROM WorkItems WHERE [System.ChangedBy] EVER @Me AND [System.ChangedDate] >= '{since}'");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                result = await Wiql(org, $"SELECT [System.Id] FROM WorkItems WHERE [System.ChangedBy] = @Me AND [System.ChangedDate] >= '{since}'");
            }

            var ids = result.Array("workItems").Select(w => w.Long("id")).OfType<long>().Distinct().ToList();
            foreach (var batch in ids.Chunk(200))
            {
                var fields = "System.Title,System.TeamProject";
                var details = await api.GetAsync(pat, $"{org}/_apis/wit/workitems?ids={string.Join(',', batch)}&fields={fields}&errorPolicy=omit&{ApiVersion}", cancellationToken);
                foreach (var workItem in details.Array("value").Select(AzureDevOpsMapper.ParseWorkItem).OfType<AzureDevOpsWorkItem>())
                {
                    await foreach (var update in Values($"{org}/_apis/wit/workitems/{workItem.Id}/updates?{ApiVersion}", AzureDevOpsPaging.Skip))
                    {
                        await EmitAsync(AzureDevOpsMapper.MapWorkItemUpdate(update, organization, workItem, me.Id, _baseUrl));
                    }
                }
            }
        }

        private Task<JsonElement> Wiql(string org, string query)
            => api.PostAsync(pat, $"{org}/_apis/wit/wiql?{ApiVersion}", new { query }, cancellationToken);

        private async Task CollectPullRequestsAsync(string organization, string projectUrl, AzureDevOpsIdentity me)
        {
            var pullRequests = new Dictionary<long, AzureDevOpsPullRequest>();
            var closedSince = Uri.EscapeDataString(JsonExtensions.Iso(fromUtc));
            foreach (var role in new[] { "creatorId", "reviewerId" })
            {
                var criteria = $"{projectUrl}/_apis/git/pullrequests?searchCriteria.{role}={me.Id}";
                string[] queries =
                [
                    $"{criteria}&searchCriteria.status=active&{ApiVersion}",
                    $"{criteria}&searchCriteria.status=all&searchCriteria.queryTimeRangeType=closed&searchCriteria.minTime={closedSince}&{ApiVersion}",
                ];
                foreach (var query in queries)
                {
                    await foreach (var item in Values(query, AzureDevOpsPaging.Skip))
                    {
                        if (AzureDevOpsMapper.ParsePullRequest(item, organization, _baseUrl) is { } pr && AzureDevOpsMapper.IsRelevant(pr, fromUtc, toUtc))
                        {
                            pullRequests.TryAdd(pr.Id, pr);
                        }
                    }
                }
            }

            foreach (var pr in pullRequests.Values)
            {
                await EmitAsync(AzureDevOpsMapper.MapPullRequestOpened(pr, me.Id));
                if (pr.RepoId is null)
                {
                    continue;
                }
                await foreach (var thread in Values($"{projectUrl}/_apis/git/repositories/{pr.RepoId}/pullRequests/{pr.Id}/threads?{ApiVersion}", AzureDevOpsPaging.None))
                {
                    foreach (var activity in AzureDevOpsMapper.MapThread(thread, pr, me.Id))
                    {
                        await EmitAsync(activity);
                    }
                }
            }
        }

        private async Task CollectBuildsAsync(string organization, string projectUrl, AzureDevOpsIdentity me)
        {
            var requestedFor = Uri.EscapeDataString(me.Email ?? me.Id);
            var min = Uri.EscapeDataString(JsonExtensions.Iso(fromUtc));
            var max = Uri.EscapeDataString(JsonExtensions.Iso(toUtc));
            var url = $"{projectUrl}/_apis/build/builds?requestedFor={requestedFor}&minTime={min}&maxTime={max}&queryOrder=queueTimeDescending&$top={AzureDevOpsApiClient.PageSize}&{ApiVersion}";
            await foreach (var build in Values(url, AzureDevOpsPaging.ContinuationToken))
            {
                if (build.Str("requestedFor", "id") is { } requester && !string.Equals(requester, me.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                await EmitAsync(AzureDevOpsMapper.MapBuild(build, organization));
            }
        }

        private async Task CollectCommitsAsync(string organization, string projectUrl, string? projectName, AzureDevOpsIdentity me)
        {
            var repositories = new List<JsonElement>();
            await foreach (var repository in Values($"{projectUrl}/_apis/git/repositories?{ApiVersion}", AzureDevOpsPaging.None))
            {
                if (repository.Str("isDisabled") != "true")
                {
                    repositories.Add(repository);
                }
            }

            var author = Uri.EscapeDataString(me.Email!);
            var from = Uri.EscapeDataString(JsonExtensions.Iso(fromUtc));
            var to = Uri.EscapeDataString(JsonExtensions.Iso(toUtc));
            foreach (var repository in repositories)
            {
                if (repository.Str("id") is not { } repoId)
                {
                    continue;
                }
                var url = $"{projectUrl}/_apis/git/repositories/{repoId}/commits?searchCriteria.author={author}&searchCriteria.fromDate={from}&searchCriteria.toDate={to}&{ApiVersion}";
                try
                {
                    await foreach (var commit in Values(url, AzureDevOpsPaging.SearchCriteriaSkip))
                    {
                        if (string.Equals(commit.Str("author", "email"), me.Email, StringComparison.OrdinalIgnoreCase))
                        {
                            await EmitAsync(AzureDevOpsMapper.MapCommit(commit, organization, projectName, repository.Str("name"), _baseUrl));
                        }
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                {
                    // Empty repositories (no default branch) answer 404/409: nothing to collect.
                }
            }
        }

        private IAsyncEnumerable<JsonElement> Values(string url, AzureDevOpsPaging paging)
            => api.GetValuesAsync(pat, url, paging, settings.MaxPages, Warnings, cancellationToken);

        private async Task EmitAsync(ActivityEvent? activity)
        {
            if (activity is null
                || activity.StartUtc < fromUtc || activity.StartUtc >= toUtc
                || !_emitted.Add(activity.ExternalId!))
            {
                return;
            }
            await sink.WriteAsync(activity, cancellationToken);
            Count++;
        }
    }
}
