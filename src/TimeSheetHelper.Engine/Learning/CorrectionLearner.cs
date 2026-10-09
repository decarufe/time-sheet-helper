using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Engine.Learning;

/// <summary>A rule the engine proposes; nothing is stored until <see cref="CorrectionLearner.PersistAsync"/>.</summary>
public sealed record RuleProposal(RuleField Field, RuleOperator Operator, string Pattern, int ForecastTaskId, string Name);

/// <summary>
/// Turns a user correction (these activities belong to that task) into a rule on the most specific stable field:
/// Repo, then Folder prefix, then Project, then Subject regex, then Participants.
/// </summary>
public sealed class CorrectionLearner(IDbContextFactory<TimeSheetDbContext> dbFactory, GroupKeyBuilder groupKeys, TimeProvider timeProvider)
{
    public const string CorrectionOrigin = "correction";
    public const string ClaudeOrigin = "claude";

    /// <summary>Share of the corrected activities (weighted by duration) a value must cover to be considered stable.</summary>
    public const double StableShare = 0.5;

    private static readonly TimeSpan MinWeight = TimeSpan.FromMinutes(1);

    /// <summary>Proposes a rule from the activities the user reassigned, or null when no field is stable enough.</summary>
    public RuleProposal? Propose(IEnumerable<ActivityEvent> activities, int forecastTaskId)
    {
        var items = activities
            .Where(a => a.Source is not (ActivitySource.Idle or ActivitySource.Locked))
            .Select(a => (Activity: a, Weight: Math.Max(a.Duration.TotalSeconds, MinWeight.TotalSeconds)))
            .ToList();
        if (items.Count == 0)
            return null;
        var total = items.Sum(i => i.Weight);

        if (Dominant(items, a => a.Repo?.Trim(), total) is { } repo)
            return Create(RuleField.Repo, RuleOperator.Equals, repo, forecastTaskId);

        if (FolderPrefix(items, total) is { } folder)
            return Create(RuleField.Folder, RuleOperator.StartsWith, folder, forecastTaskId);

        if (Dominant(items, a => a.Project?.Trim(), total) is { } project)
            return Create(RuleField.Project, RuleOperator.Equals, project, forecastTaskId);

        if (Dominant(items, a => NullIfEmpty(GroupKeyBuilder.NormalizeSubject(a.Subject)), total) is { } subject)
            return Create(RuleField.Subject, RuleOperator.Regex, WordsRegex(subject), forecastTaskId);

        if (Dominant(items, a => FirstExternalDomain(a.Participants), total) is { } domain)
            return Create(RuleField.Participants, RuleOperator.Contains, "@" + domain, forecastTaskId);

        var participant = items
            .SelectMany(i => GroupKeyBuilder.SplitParticipants(i.Activity.Participants ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => (Participant: p.ToLowerInvariant(), i.Weight)))
            .GroupBy(p => p.Participant)
            .Select(g => (g.Key, Weight: g.Sum(p => p.Weight)))
            .OrderByDescending(g => g.Weight)
            .FirstOrDefault();
        if (participant.Key is not null && participant.Weight >= total * StableShare)
            return Create(RuleField.Participants, RuleOperator.Contains, participant.Key, forecastTaskId);

        return null;
    }

    /// <summary>Rule matching what defines an unclassified group (used when accepting a group as a whole).</summary>
    public RuleProposal? ProposeFromGroupKey(string groupKey, int forecastTaskId)
    {
        if (!GroupKey.TryParse(groupKey, out var key) || string.IsNullOrWhiteSpace(key.Value))
            return null;

        switch (key.Kind)
        {
            case GroupKind.Repo:
                return Create(RuleField.Repo, RuleOperator.Equals, key.Value, forecastTaskId);
            case GroupKind.Folder:
                return Create(RuleField.Folder, RuleOperator.StartsWith, key.Value, forecastTaskId);
            case GroupKind.Meeting:
                return Create(RuleField.Subject, RuleOperator.Regex, WordsRegex(key.Value), forecastTaskId);
            case GroupKind.Domain:
            {
                var domains = key.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                return domains.Length == 1
                    ? Create(RuleField.Participants, RuleOperator.Contains, "@" + domains[0], forecastTaskId)
                    : Create(RuleField.Participants, RuleOperator.Regex, "@(?:" + string.Join('|', domains.Select(Regex.Escape)) + ")", forecastTaskId);
            }
            case GroupKind.People:
            {
                var people = key.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                return people.Length == 1
                    ? Create(RuleField.Participants, RuleOperator.Contains, people[0], forecastTaskId)
                    : Create(RuleField.Participants, RuleOperator.Regex, string.Concat(people.Select(p => $"(?=.*{Regex.Escape(p)})")), forecastTaskId);
            }
            case GroupKind.App:
            {
                var colon = key.Value.IndexOf(':');
                if (colon < 0)
                    return Create(RuleField.ProcessName, RuleOperator.Equals, key.Value, forecastTaskId);
                var stem = key.Value[(colon + 1)..];
                return Create(RuleField.Title, RuleOperator.Regex, WordsRegex(stem), forecastTaskId);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Stores the proposal ahead of existing rules (corrections are the user's latest word). An identical rule
    /// (same field, operator and pattern) is re-pointed to the task instead of being duplicated.
    /// </summary>
    public async Task<Rule> PersistAsync(RuleProposal proposal, string origin = CorrectionOrigin, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.Rules.ToListAsync(cancellationToken);
        var minOrder = rules.Count == 0 ? 1 : rules.Min(r => r.Order);

        var rule = rules.FirstOrDefault(r =>
            r.Field == proposal.Field
            && r.Operator == proposal.Operator
            && string.Equals(r.Pattern, proposal.Pattern, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            rule = new Rule
            {
                Name = proposal.Name,
                Field = proposal.Field,
                Operator = proposal.Operator,
                Pattern = proposal.Pattern,
                CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
            };
            db.Rules.Add(rule);
        }

        rule.ForecastTaskId = proposal.ForecastTaskId;
        rule.Enabled = true;
        rule.Origin = origin;
        rule.Order = minOrder - 1;

        await db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    /// <summary>Case-insensitive regex matching the words of a normalized text, whatever separators or numbers sit between them.</summary>
    public static string WordsRegex(string normalized)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
        return string.Join(@"[\W\d_]+", words);
    }

    public static string FieldLabel(RuleField field) => field switch
    {
        RuleField.Source => "Source",
        RuleField.ProcessName => "Application",
        RuleField.Title => "Titre",
        RuleField.FilePath => "Fichier",
        RuleField.Folder => "Dossier",
        RuleField.Url => "URL",
        RuleField.Repo => "Dépôt",
        RuleField.Project => "Projet",
        RuleField.Subject => "Sujet",
        RuleField.Participants => "Participants",
        _ => field.ToString(),
    };

    private static RuleProposal Create(RuleField field, RuleOperator op, string pattern, int taskId) =>
        new(field, op, pattern, taskId, $"{FieldLabel(field)} : {pattern}");

    private static string? Dominant(List<(ActivityEvent Activity, double Weight)> items, Func<ActivityEvent, string?> selector, double total)
    {
        var best = items
            .Select(i => (Value: selector(i.Activity), i.Weight))
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .GroupBy(v => v.Value!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, Weight: g.Sum(v => v.Weight)))
            .OrderByDescending(g => g.Weight)
            .FirstOrDefault();
        return best.Key is not null && best.Weight >= total * StableShare ? best.Key : null;
    }

    /// <summary>
    /// Longest common directory prefix of the folders covering a stable share of the activities.
    /// A bare drive or share root is too generic; the dominant exact folder is used instead.
    /// </summary>
    private static string? FolderPrefix(List<(ActivityEvent Activity, double Weight)> items, double total)
    {
        var withFolder = items.Where(i => !string.IsNullOrWhiteSpace(i.Activity.Folder)).ToList();
        if (withFolder.Sum(i => i.Weight) < total * StableShare)
            return null;

        var folders = withFolder.Select(i => i.Activity.Folder!.Trim().TrimEnd('\\', '/')).ToList();
        var prefix = folders[0];
        foreach (var folder in folders.Skip(1))
        {
            var n = 0;
            while (n < prefix.Length && n < folder.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(folder[n]))
                n++;
            // Only cut on a directory boundary.
            var boundary = n == prefix.Length && (n == folder.Length || IsSeparator(folder[n]))
                || n == folder.Length && n < prefix.Length && IsSeparator(prefix[n]);
            if (!boundary)
            {
                while (n > 0 && !IsSeparator(prefix[n - 1]))
                    n--;
            }
            prefix = prefix[..n].TrimEnd('\\', '/');
        }

        if (MeaningfulSegments(prefix) >= 2)
            return prefix;

        return Dominant(withFolder, a => a.Folder?.Trim().TrimEnd('\\', '/'), total);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>Path segments, not counting a drive letter or a URL scheme.</summary>
    private static int MeaningfulSegments(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Count(s => !s.EndsWith(':'));

    private string? FirstExternalDomain(string? participants) =>
        string.IsNullOrWhiteSpace(participants) ? null : groupKeys.ExternalDomains(participants) is [var first, ..] ? first : null;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
