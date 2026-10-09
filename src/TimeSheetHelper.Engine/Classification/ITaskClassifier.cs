using System.Text.Json.Serialization;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;

namespace TimeSheetHelper.Engine.Classification;

/// <summary>
/// Metadata of an unclassified activity group: the only thing ever sent to a classifier.
/// No bodies, no participants list, only titles / paths / subjects samples.
/// </summary>
public sealed record ActivityGroup(
    string Key,
    IReadOnlyList<ActivitySource> Sources,
    IReadOnlyList<string> SampleTitles,
    IReadOnlyList<string> SamplePaths,
    IReadOnlyList<string> SampleSubjects,
    TimeSpan TotalDuration);

public sealed record SuggestedRule(
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuleField>))] RuleField Field,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuleOperator>))] RuleOperator Operator,
    string Pattern);

/// <summary>Classifier verdict for a group. <see cref="TaskId"/> is null when no task fits.</summary>
public sealed record ClassificationResult(int? TaskId, double Confidence, string? Reason, SuggestedRule? SuggestedRule);

/// <summary>Proposes a Forecast task for an unclassified activity group (Claude in production, a fake in tests).</summary>
public interface ITaskClassifier
{
    /// <summary>False when the feature is disabled or not configured: callers must not call <see cref="ClassifyAsync"/>.</summary>
    bool IsAvailable { get; }

    /// <returns>The verdict, or null when the classifier could not answer (error, refusal).</returns>
    Task<ClassificationResult?> ClassifyAsync(
        ActivityGroup group,
        IReadOnlyList<ForecastTask> tasks,
        IReadOnlyList<Rule> rules,
        CancellationToken cancellationToken = default);
}
