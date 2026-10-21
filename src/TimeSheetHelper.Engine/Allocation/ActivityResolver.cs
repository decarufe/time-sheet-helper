using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Engine.Allocation;

/// <summary>How an activity takes part in slot allocation.</summary>
public enum ActivityRole
{
    /// <summary>Interval of active work (Focus, VsCode, ClaudeCode).</summary>
    Active,

    /// <summary>Idle or locked time: removes active time, never work by itself.</summary>
    Mask,

    /// <summary>Attended calendar meeting.</summary>
    Meeting,

    /// <summary>Point event (commit, e-mail, ...) widened to an implicit window, or another dated evidence.</summary>
    Evidence,
}

/// <summary>What an activity votes for: a Forecast task (rule match) or an unclassified group.</summary>
public readonly record struct Candidate(int? TaskId, string? GroupKey)
{
    public static Candidate ForTask(int taskId) => new(taskId, null);
    public static Candidate ForGroup(string groupKey) => new(null, groupKey);

    public override string ToString() => TaskId is { } id ? $"task:{id}" : GroupKey ?? "";
}

/// <summary>
/// An activity ready for allocation: its role, effective interval and candidate. With learned feedback an activity
/// can be split over several candidates; <paramref name="Weight"/> is then the share of the activity this candidate gets.
/// <paramref name="GroupKey"/> is the activity's group, known even when a rule picked its task.
/// </summary>
public sealed record ResolvedActivity(ActivityEvent Activity, ActivityRole Role, Interval Window, Candidate? Candidate, int? RuleId = null, string? GroupKey = null, double Weight = 1.0);

/// <summary>Applies rules, group keys, evidence windows and meeting filters to raw activities.</summary>
public sealed class ActivityResolver(RuleEvaluator ruleEvaluator, GroupKeyBuilder groupKeys, IOptions<EngineOptions> options, ISourceCatalog? catalog = null)
{
    private readonly ISourceCatalog _catalog = catalog ?? SourceCatalog.Default;

    public const string CommitKind = "commit";

    /// <param name="activities">Activities, ideally including some before the range so commit windows can find the previous commit.</param>
    /// <param name="rules">Rules; disabled ones are skipped.</param>
    /// <param name="feedback">Learned confirmations per group key (see <see cref="Learning.PredictionFeedback"/>); null to predict from rules and groups only.</param>
    public IReadOnlyList<ResolvedActivity> Resolve(IEnumerable<ActivityEvent> activities, IEnumerable<Rule> rules, IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>? feedback = null)
    {
        var ordered = RuleEvaluator.Prepare(rules);
        var sorted = activities.OrderBy(a => a.StartUtc).ToList();
        var windows = ComputeEvidenceWindows(sorted);
        var result = new List<ResolvedActivity>(sorted.Count);

        foreach (var activity in sorted)
        {
            var role = RoleOf(activity);
            if (role is null)
                continue;

            if (role == ActivityRole.Mask)
            {
                result.Add(new(activity, ActivityRole.Mask, new(activity.StartUtc, activity.EndUtc!.Value), null));
                continue;
            }

            var window = role == ActivityRole.Evidence && windows.TryGetValue(activity, out var w)
                ? w
                : new Interval(activity.StartUtc, activity.EndUtc ?? activity.StartUtc);
            if (window.IsEmpty)
                continue;

            var rule = ruleEvaluator.FirstMatch(activity, ordered);
            var groupKey = rule is null || feedback is not null ? groupKeys.Build(activity) : null;
            var candidate = rule is not null
                ? Candidate.ForTask(rule.ForecastTaskId)
                : Candidate.ForGroup(groupKey!);
            var counts = groupKey is not null ? feedback?.GetValueOrDefault(groupKey) : null;
            foreach (var (share, weight) in Learning.PredictionFeedback.Split(candidate, rule is not null, counts))
                result.Add(new(activity, role.Value, window, share, share == candidate ? rule?.Id : null, groupKey, weight));
        }

        return result;
    }

    /// <summary>Role of an activity, or null when it carries no time information to allocate.</summary>
    public ActivityRole? RoleOf(ActivityEvent activity)
    {
        var o = options.Value;
        var isPoint = activity.EndUtc is null || activity.EndUtc <= activity.StartUtc;

        switch (_catalog.Get(activity.Source).Behavior)
        {
            case SourceBehavior.NonWork:
                return isPoint ? null : ActivityRole.Mask;

            case SourceBehavior.Presence:
                return isPoint ? ActivityRole.Evidence : ActivityRole.Active;

            case SourceBehavior.Meeting:
                if (isPoint || o.IsIgnoredCalendarKind(activity.Kind) || activity.Duration > o.MaxMeetingDuration)
                    return null;
                return ActivityRole.Meeting;

            default:
                return ActivityRole.Evidence;
        }
    }

    /// <summary>
    /// Implicit window before each point event. A commit's window never reaches back before the previous
    /// commit in the same repository: the work before that belongs to the earlier commit.
    /// </summary>
    public Dictionary<ActivityEvent, Interval> ComputeEvidenceWindows(IReadOnlyList<ActivityEvent> sortedActivities)
    {
        var o = options.Value;
        var result = new Dictionary<ActivityEvent, Interval>(ReferenceEqualityComparer.Instance);
        var lastCommitByRepo = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var previousCommitStart = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var activity in sortedActivities)
        {
            var isPoint = activity.EndUtc is null || activity.EndUtc <= activity.StartUtc;
            var traits = _catalog.Get(activity.Source);
            if (!isPoint || traits.Behavior is SourceBehavior.NonWork or SourceBehavior.Meeting)
                continue;

            var start = activity.StartUtc - o.GetEvidenceWindow(activity.Source, activity.Kind, traits);

            if (string.Equals(activity.Kind, CommitKind, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(activity.Repo))
            {
                // The same commit can be reported by several sources (LocalGit + GitHub) at the same instant;
                // only a strictly earlier commit bounds the window.
                if (lastCommitByRepo.TryGetValue(activity.Repo, out var last) && last < activity.StartUtc)
                    previousCommitStart[activity.Repo] = last;

                if (previousCommitStart.TryGetValue(activity.Repo, out var previous) && previous > start)
                    start = previous;

                lastCommitByRepo[activity.Repo] = activity.StartUtc;
            }

            result[activity] = new Interval(start, activity.StartUtc);
        }

        return result;
    }
}
