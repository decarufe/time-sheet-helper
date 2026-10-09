using System.Text.Json;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.DevOps.AzureDevOps;

/// <summary>The authenticated user as reported by <c>_apis/connectionData</c>.</summary>
public sealed record AzureDevOpsIdentity(string Id, string? DisplayName, string? Email);

public sealed record AzureDevOpsWorkItem(long Id, string? Title, string? Project);

/// <summary>Pull request context for thread comments.</summary>
public sealed record AzureDevOpsPullRequest(
    string Organization, long Id, string? Title, string? Project, string? ProjectId, string? Repo, string? RepoId,
    string? CreatorId, DateTime? CreatedUtc, DateTime? ClosedUtc, string? Status, string WebUrl)
{
    public string Subject => Title is null ? $"PR {Id}" : $"PR {Id} {Title}";
}

/// <summary>Maps Azure DevOps REST payloads to activity events. Keeps metadata only: never comment bodies.</summary>
public static class AzureDevOpsMapper
{
    public static class Kinds
    {
        public const string WorkItemUpdate = "workitem-update";
        public const string PullRequestOpened = "pr-opened";
        public const string PullRequestReview = "pr-review";
        public const string PullRequestComment = "pr-comment";
        public const string Build = "build";
        public const string Commit = "commit";
    }

    public static AzureDevOpsIdentity? ParseIdentity(JsonElement connectionData)
    {
        var id = connectionData.Str("authenticatedUser", "id");
        if (id is null)
        {
            return null;
        }
        return new AzureDevOpsIdentity(
            id,
            connectionData.Str("authenticatedUser", "providerDisplayName"),
            connectionData.Str("authenticatedUser", "properties", "Account", "$value"));
    }

    /// <summary>Item of <c>_apis/wit/workitems?ids=</c>.</summary>
    public static AzureDevOpsWorkItem? ParseWorkItem(JsonElement item)
        => item.Long("id") is { } id
            ? new AzureDevOpsWorkItem(id, item.Str("fields", "System.Title"), item.Str("fields", "System.TeamProject"))
            : null;

    /// <summary>
    /// Item of <c>_apis/wit/workitems/{id}/updates</c>. Only updates made by <paramref name="me"/> produce an event;
    /// the change time is the new System.ChangedDate of that update.
    /// </summary>
    public static ActivityEvent? MapWorkItemUpdate(JsonElement update, string organization, AzureDevOpsWorkItem workItem, string me, string baseUrl)
    {
        if (!SameId(update.Str("revisedBy", "id"), me)
            || update.Utc("fields", "System.ChangedDate", "newValue") is not { } changed
            || update.Str("id") is not { } updateId)
        {
            return null;
        }
        var project = workItem.Project ?? update.Str("fields", "System.TeamProject", "newValue");
        return new ActivityEvent
        {
            Source = ActivitySource.AzureDevOps,
            Kind = Kinds.WorkItemUpdate,
            StartUtc = changed,
            Project = ProjectKey(organization, project),
            Subject = workItem.Title is null ? $"#{workItem.Id}" : $"#{workItem.Id} {workItem.Title}",
            Url = project is null
                ? $"{baseUrl}{Escape(organization)}/_workitems/edit/{workItem.Id}"
                : $"{baseUrl}{Escape(organization)}/{Escape(project)}/_workitems/edit/{workItem.Id}",
            ExternalId = $"wi:{organization}/{workItem.Id}/{updateId}",
        };
    }

    /// <summary>Item of <c>_apis/git/pullrequests</c>.</summary>
    public static AzureDevOpsPullRequest? ParsePullRequest(JsonElement pr, string organization, string baseUrl)
    {
        if (pr.Long("pullRequestId") is not { } id)
        {
            return null;
        }
        var project = pr.Str("repository", "project", "name");
        var repo = pr.Str("repository", "name");
        var webUrl = project is not null && repo is not null
            ? $"{baseUrl}{Escape(organization)}/{Escape(project)}/_git/{Escape(repo)}/pullrequest/{id}"
            : $"{baseUrl}{Escape(organization)}";
        return new AzureDevOpsPullRequest(
            organization, id, pr.Str("title"), project, pr.Str("repository", "project", "id"), repo, pr.Str("repository", "id"),
            pr.Str("createdBy", "id"), pr.Utc("creationDate"), pr.Utc("closedDate"), pr.Str("status"), webUrl);
    }

    /// <summary>True when the pull request may carry activity in the range: created before its end and not closed before its start.</summary>
    public static bool IsRelevant(AzureDevOpsPullRequest pr, DateTime fromUtc, DateTime toUtc)
        => (pr.CreatedUtc is null || pr.CreatedUtc < toUtc)
            && (pr.ClosedUtc is null || pr.ClosedUtc.Value.Year < 2000 || pr.ClosedUtc >= fromUtc);

    public static ActivityEvent? MapPullRequestOpened(AzureDevOpsPullRequest pr, string me)
    {
        if (!SameId(pr.CreatorId, me) || pr.CreatedUtc is not { } created)
        {
            return null;
        }
        return Create(pr, Kinds.PullRequestOpened, created, pr.WebUrl, $"pr:{pr.Organization}/{pr.Id}");
    }

    /// <summary>
    /// Item of <c>.../pullRequests/{id}/threads</c>. Vote updates (system comments in a "VoteUpdate" thread) become
    /// reviews; text comments become pr-comment. Deleted comments are ignored.
    /// </summary>
    public static IEnumerable<ActivityEvent> MapThread(JsonElement thread, AzureDevOpsPullRequest pr, string me)
    {
        var threadId = thread.Str("id");
        if (threadId is null)
        {
            yield break;
        }
        var isVote = thread.Str("properties", "CodeReviewThreadType", "$value") == "VoteUpdate";

        foreach (var comment in thread.Array("comments"))
        {
            if (!SameId(comment.Str("author", "id"), me)
                || comment.Str("isDeleted") == "true"
                || comment.Utc("publishedDate") is not { } published
                || comment.Str("id") is not { } commentId)
            {
                continue;
            }

            var type = comment.Str("commentType");
            string kind;
            if (isVote)
            {
                kind = Kinds.PullRequestReview;
            }
            else if (type is null or "text")
            {
                kind = Kinds.PullRequestComment;
            }
            else
            {
                continue;
            }
            yield return Create(pr, kind, published, $"{pr.WebUrl}?discussionId={threadId}", $"pr-thread:{pr.Organization}/{pr.Id}/{threadId}/{commentId}");
        }
    }

    /// <summary>Item of <c>_apis/build/builds</c>.</summary>
    public static ActivityEvent? MapBuild(JsonElement build, string organization)
    {
        var id = build.Str("id");
        var start = build.Utc("startTime") ?? build.Utc("queueTime");
        if (id is null || start is null)
        {
            return null;
        }
        var finish = build.Utc("finishTime");
        var definition = build.Str("definition", "name");
        var number = build.Str("buildNumber");
        return new ActivityEvent
        {
            Source = ActivitySource.AzureDevOps,
            Kind = Kinds.Build,
            StartUtc = start.Value,
            EndUtc = finish is { } end && end >= start.Value ? end : null,
            Repo = build.Str("repository", "name"),
            Project = ProjectKey(organization, build.Str("project", "name")),
            Subject = (definition, number) switch
            {
                (not null, not null) => $"{definition} {number}",
                _ => definition ?? number,
            },
            Url = build.Str("_links", "web", "href"),
            ExternalId = $"build:{organization}/{id}",
        };
    }

    /// <summary>Item of <c>.../repositories/{id}/commits</c>.</summary>
    public static ActivityEvent? MapCommit(JsonElement commit, string organization, string? project, string? repo, string baseUrl)
    {
        var sha = commit.Str("commitId");
        var authored = commit.Utc("author", "date");
        if (sha is null || authored is null)
        {
            return null;
        }
        return new ActivityEvent
        {
            Source = ActivitySource.AzureDevOps,
            Kind = Kinds.Commit,
            StartUtc = authored.Value,
            Repo = repo,
            Project = ProjectKey(organization, project),
            Subject = JsonExtensions.FirstLine(commit.Str("comment")),
            Url = commit.Str("remoteUrl")
                ?? (project is not null && repo is not null ? $"{baseUrl}{Escape(organization)}/{Escape(project)}/_git/{Escape(repo)}/commit/{sha}" : null),
            ExternalId = $"commit:{sha}",
        };
    }

    public static string ProjectKey(string organization, string? project)
        => project is null ? organization : $"{organization}/{project}";

    private static ActivityEvent Create(AzureDevOpsPullRequest pr, string kind, DateTime startUtc, string url, string externalId) => new()
    {
        Source = ActivitySource.AzureDevOps,
        Kind = kind,
        StartUtc = startUtc,
        Repo = pr.Repo,
        Project = ProjectKey(pr.Organization, pr.Project),
        Subject = pr.Subject,
        Url = url,
        ExternalId = externalId,
    };

    private static bool SameId(string? actual, string me) => string.Equals(actual, me, StringComparison.OrdinalIgnoreCase);

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
