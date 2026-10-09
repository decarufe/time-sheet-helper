using System.Text.Json;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.DevOps.GitHub;

/// <summary>Pull request or issue found by the search API; context for its reviews and comments.</summary>
public sealed record GitHubIssueRef(string Repo, long Number, string? Title, string? HtmlUrl, bool IsPullRequest)
{
    public string Subject => Title is null ? $"#{Number}" : $"#{Number} {Title}";
}

/// <summary>Maps GitHub REST payloads to activity events. Keeps metadata only: never comment or review bodies.</summary>
public static class GitHubMapper
{
    public static class Kinds
    {
        public const string Commit = "commit";
        public const string PullRequestOpened = "pr-opened";
        public const string PullRequestReview = "pr-review";
        public const string PullRequestComment = "pr-comment";
        public const string IssueComment = "issue-comment";
        public const string WorkflowRun = "workflow-run";
    }

    /// <summary>"https://api.github.com/repos/owner/repo" → "owner/repo".</summary>
    public static string? RepoFromApiUrl(string? repositoryUrl)
    {
        if (repositoryUrl is null)
        {
            return null;
        }
        const string marker = "/repos/";
        var index = repositoryUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : repositoryUrl[(index + marker.Length)..].TrimEnd('/');
    }

    public static string? Owner(string? repo)
    {
        var slash = repo?.IndexOf('/') ?? -1;
        return slash > 0 ? repo![..slash] : null;
    }

    public static GitHubIssueRef? IssueFromSearchItem(JsonElement item)
    {
        var repo = RepoFromApiUrl(item.Str("repository_url"));
        var number = item.Long("number");
        if (repo is null || number is null)
        {
            return null;
        }
        return new GitHubIssueRef(repo, number.Value, item.Str("title"), item.Str("html_url"), item.Path("pull_request") is not null);
    }

    /// <summary>Item from <c>GET /search/commits</c>.</summary>
    public static ActivityEvent? MapCommit(JsonElement item)
    {
        var sha = item.Str("sha");
        var repo = item.Str("repository", "full_name");
        var authored = item.Utc("commit", "author", "date");
        if (sha is null || repo is null || authored is null)
        {
            return null;
        }
        return new ActivityEvent
        {
            Source = ActivitySource.GitHub,
            Kind = Kinds.Commit,
            StartUtc = authored.Value,
            Repo = repo,
            Project = item.Str("repository", "owner", "login") ?? Owner(repo),
            Subject = JsonExtensions.FirstLine(item.Str("commit", "message")),
            Url = item.Str("html_url"),
            ExternalId = $"commit:{sha}",
        };
    }

    /// <summary>Search item of a pull request; produces an event only when the user opened it.</summary>
    public static ActivityEvent? MapPullRequestOpened(JsonElement item, string login)
    {
        if (IssueFromSearchItem(item) is not { IsPullRequest: true } pr
            || !SameUser(item.Str("user", "login"), login)
            || item.Utc("created_at") is not { } created)
        {
            return null;
        }
        return Create(pr, Kinds.PullRequestOpened, created, pr.HtmlUrl, $"pr:{pr.Repo}#{pr.Number}");
    }

    /// <summary>Item from <c>GET /repos/{repo}/pulls/{n}/reviews</c>; pending reviews have no submitted_at and are skipped.</summary>
    public static ActivityEvent? MapReview(JsonElement review, GitHubIssueRef pr, string login)
    {
        if (!SameUser(review.Str("user", "login"), login)
            || review.Utc("submitted_at") is not { } submitted
            || review.Str("id") is not { } id)
        {
            return null;
        }
        return Create(pr, Kinds.PullRequestReview, submitted, review.Str("html_url") ?? pr.HtmlUrl, $"pr-review:{id}");
    }

    /// <summary>Item from <c>GET /repos/{repo}/pulls/{n}/comments</c> (inline review comments).</summary>
    public static ActivityEvent? MapReviewComment(JsonElement comment, GitHubIssueRef pr, string login)
    {
        if (!SameUser(comment.Str("user", "login"), login)
            || comment.Utc("created_at") is not { } created
            || comment.Str("id") is not { } id)
        {
            return null;
        }
        return Create(pr, Kinds.PullRequestComment, created, comment.Str("html_url") ?? pr.HtmlUrl, $"pr-review-comment:{id}");
    }

    /// <summary>Item from <c>GET /repos/{repo}/issues/{n}/comments</c>; on a pull request it is a conversation comment.</summary>
    public static ActivityEvent? MapIssueComment(JsonElement comment, GitHubIssueRef issue, string login)
    {
        if (!SameUser(comment.Str("user", "login"), login)
            || comment.Utc("created_at") is not { } created
            || comment.Str("id") is not { } id)
        {
            return null;
        }
        var kind = issue.IsPullRequest ? Kinds.PullRequestComment : Kinds.IssueComment;
        return Create(issue, kind, created, comment.Str("html_url") ?? issue.HtmlUrl, $"issue-comment:{id}");
    }

    /// <summary>Item of <c>workflow_runs</c> from <c>GET /repos/{repo}/actions/runs</c>.</summary>
    public static ActivityEvent? MapWorkflowRun(JsonElement run, string repo)
    {
        var id = run.Str("id");
        var started = run.Utc("run_started_at") ?? run.Utc("created_at");
        if (id is null || started is null)
        {
            return null;
        }
        var fullName = run.Str("repository", "full_name") ?? repo;
        var completed = run.Str("status") == "completed" ? run.Utc("updated_at") : null;
        var name = run.Str("name");
        var title = JsonExtensions.FirstLine(run.Str("display_title"));
        return new ActivityEvent
        {
            Source = ActivitySource.GitHub,
            Kind = Kinds.WorkflowRun,
            StartUtc = started.Value,
            EndUtc = completed is { } end && end >= started.Value ? end : null,
            Repo = fullName,
            Project = Owner(fullName),
            Subject = (name, title) switch
            {
                (not null, not null) when name != title => $"{name} : {title}",
                _ => name ?? title,
            },
            Url = run.Str("html_url"),
            ExternalId = $"workflow-run:{id}",
        };
    }

    private static ActivityEvent Create(GitHubIssueRef issue, string kind, DateTime startUtc, string? url, string externalId) => new()
    {
        Source = ActivitySource.GitHub,
        Kind = kind,
        StartUtc = startUtc,
        Repo = issue.Repo,
        Project = Owner(issue.Repo),
        Subject = issue.Subject,
        Url = url,
        ExternalId = externalId,
    };

    private static bool SameUser(string? actual, string login) => string.Equals(actual, login, StringComparison.OrdinalIgnoreCase);
}
