using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Rules;

namespace TimeSheetHelper.Engine.Rules;

/// <summary>Matches activities against user rules. Rules are tried by ascending <see cref="Rule.Order"/>; the first match wins.</summary>
public sealed class RuleEvaluator(IOptions<EngineOptions> options)
{
    private readonly ConcurrentDictionary<string, Regex?> _regexes = new();

    /// <summary>Enabled rules in evaluation order (Order, then Id for stability).</summary>
    public static IReadOnlyList<Rule> Prepare(IEnumerable<Rule> rules) =>
        rules.Where(r => r.Enabled).OrderBy(r => r.Order).ThenBy(r => r.Id).ToList();

    /// <summary>First matching rule, or null. <paramref name="orderedRules"/> should come from <see cref="Prepare"/>.</summary>
    public Rule? FirstMatch(ActivityEvent activity, IReadOnlyList<Rule> orderedRules)
    {
        foreach (var rule in orderedRules)
        {
            if (rule.Enabled && Matches(rule, activity))
                return rule;
        }
        return null;
    }

    public bool Matches(Rule rule, ActivityEvent activity) =>
        Matches(rule.Field, rule.Operator, rule.Pattern, activity);

    public bool Matches(RuleField field, RuleOperator op, string pattern, ActivityEvent activity)
    {
        var value = GetField(activity, field);
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(pattern))
            return false;

        return op switch
        {
            RuleOperator.Equals => string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            RuleOperator.Contains => value.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            RuleOperator.Regex => RegexMatches(pattern, value),
            _ => false,
        };
    }

    public static string? GetField(ActivityEvent activity, RuleField field) => field switch
    {
        RuleField.Source => activity.Source.ToString(),
        RuleField.ProcessName => activity.ProcessName,
        RuleField.Title => activity.Title,
        RuleField.FilePath => activity.FilePath,
        RuleField.Folder => activity.Folder,
        RuleField.Url => activity.Url,
        RuleField.Repo => activity.Repo,
        RuleField.Project => activity.Project,
        RuleField.Subject => activity.Subject,
        RuleField.Participants => activity.Participants,
        _ => null,
    };

    /// <summary>True when <paramref name="pattern"/> is a valid regex (used to validate suggested rules).</summary>
    public bool IsValidRegex(string pattern) => GetRegex(pattern) is not null;

    private bool RegexMatches(string pattern, string value)
    {
        var regex = GetRegex(pattern);
        if (regex is null)
            return false;
        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private Regex? GetRegex(string pattern) => _regexes.GetOrAdd(pattern, p =>
    {
        try
        {
            return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, options.Value.RegexTimeout);
        }
        catch (ArgumentException)
        {
            // An invalid user pattern never matches rather than breaking the whole week.
            return null;
        }
    });
}
