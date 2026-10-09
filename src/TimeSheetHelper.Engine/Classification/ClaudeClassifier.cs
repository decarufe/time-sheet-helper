using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Engine.Classification;

/// <summary>
/// Asks Claude which Forecast task an activity group belongs to, using structured outputs.
/// Only group metadata, the task list and existing rules are sent.
/// </summary>
public sealed class ClaudeClassifier(
    IOptions<EngineOptions> options,
    RuleEvaluator ruleEvaluator,
    ILogger<ClaudeClassifier> logger,
    ISecretStore? secretStore = null) : ITaskClassifier
{
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";

    private const string SystemPrompt =
        """
        You map a group of a consultant's work activities to one of their Forecast timesheet tasks.
        You receive only metadata: a group key (repository, folder, meeting subject, e-mail domains or application),
        sample window titles, file paths, subjects, the sources it came from and the total time spent.
        You also receive the list of tasks (id, client, project, task) and the user's existing rules,
        which show how similar activities were classified before.

        Pick the task the group most likely belongs to, or null when nothing fits or the activity is not billable work
        (personal browsing, generic tooling with no project hint). Confidence is between 0 and 1; stay below 0.5 when guessing.
        Write the reason in French, in one short sentence the user will read.
        When a stable field would classify similar activities in the future, suggest a rule:
        prefer Repo (Equals), then Folder (StartsWith), then Project (Equals), then Subject (Regex), then Participants (Contains).
        Never suggest a rule matching generic values (a whole drive, a browser, "Microsoft Teams"). Use null otherwise.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsAvailable =>
        options.Value.Claude.Enabled && !string.IsNullOrWhiteSpace(secretStore?.Get(SecretNames.AnthropicApiKey));

    public async Task<ClassificationResult?> ClassifyAsync(
        ActivityGroup group,
        IReadOnlyList<ForecastTask> tasks,
        IReadOnlyList<Rule> rules,
        CancellationToken cancellationToken = default)
    {
        var claude = options.Value.Claude;
        var apiKey = secretStore?.Get(SecretNames.AnthropicApiKey);
        if (!claude.Enabled || string.IsNullOrWhiteSpace(apiKey) || tasks.Count == 0)
            return null;

        var client = new AnthropicClient { ApiKey = apiKey };
        var parameters = new MessageCreateParams
        {
            Model = claude.Model,
            MaxTokens = claude.MaxTokens,
            System = SystemPrompt,
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = ResponseSchema },
            },
            Messages = [new BetaMessageParam { Role = Role.User, Content = BuildPayload(group, tasks, rules) }],
        };
        if (claude.UseServerSideFallback)
        {
            parameters = parameters with
            {
                Betas = [ServerSideFallbackBeta],
                Fallbacks = new Default(),
            };
        }

        try
        {
            var response = await client.Beta.Messages.Create(parameters, cancellationToken);
            if (response.StopReason == "refusal")
            {
                logger.LogWarning("Claude declined to classify group {GroupKey}.", group.Key);
                return null;
            }

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
            return Parse(text, tasks);
        }
        catch (AnthropicRateLimitException ex)
        {
            logger.LogWarning(ex, "Claude rate limit reached while classifying {GroupKey}.", group.Key);
        }
        catch (AnthropicApiException ex)
        {
            logger.LogWarning(ex, "Claude API error while classifying {GroupKey}.", group.Key);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Claude unreachable while classifying {GroupKey}.", group.Key);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unreadable Claude answer for {GroupKey}.", group.Key);
        }
        return null;
    }

    /// <summary>User message: group metadata, tasks and rules as JSON.</summary>
    internal static string BuildPayload(ActivityGroup group, IReadOnlyList<ForecastTask> tasks, IReadOnlyList<Rule> rules)
    {
        var payload = new
        {
            group = new
            {
                key = group.Key,
                sources = group.Sources.Select(s => s.ToString()),
                sampleTitles = group.SampleTitles,
                samplePaths = group.SamplePaths,
                sampleSubjects = group.SampleSubjects,
                totalMinutes = (int)Math.Round(group.TotalDuration.TotalMinutes),
            },
            tasks = tasks.Select(t => new { id = t.Id, client = t.ClientName, project = t.ProjectName, task = t.TaskName }),
            rules = rules.Where(r => r.Enabled).Select(r => new
            {
                field = r.Field.ToString(),
                @operator = r.Operator.ToString(),
                pattern = r.Pattern,
                taskId = r.ForecastTaskId,
            }),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Validates Claude's JSON: unknown task ids, out-of-range confidence and invalid rules are dropped.</summary>
    internal ClassificationResult Parse(string json, IReadOnlyList<ForecastTask> tasks)
    {
        var answer = JsonSerializer.Deserialize<Answer>(json, JsonOptions)
            ?? throw new JsonException("Empty classification answer.");

        int? taskId = answer.TaskId is { } id && tasks.Any(t => t.Id == id) ? id : null;
        var confidence = taskId is null ? 0 : Math.Clamp(answer.Confidence, 0, 1);

        SuggestedRule? rule = null;
        if (taskId is not null
            && answer.SuggestedRule is { } s
            && Enum.TryParse<RuleField>(s.Field, ignoreCase: true, out var field)
            && Enum.TryParse<RuleOperator>(s.Operator, ignoreCase: true, out var op)
            && !string.IsNullOrWhiteSpace(s.Pattern)
            && (op != RuleOperator.Regex || ruleEvaluator.IsValidRegex(s.Pattern)))
        {
            rule = new SuggestedRule(field, op, s.Pattern.Trim());
        }

        return new ClassificationResult(taskId, confidence, answer.Reason, rule);
    }

    private sealed record Answer(int? TaskId, double Confidence, string? Reason, AnswerRule? SuggestedRule);

    private sealed record AnswerRule(string? Field, string? Operator, string? Pattern);

    private static readonly Dictionary<string, JsonElement> ResponseSchema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "taskId", "confidence", "reason", "suggestedRule" }),
        ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["taskId"] = new { type = new[] { "integer", "null" }, description = "Id of the chosen task, or null." },
            ["confidence"] = new { type = "number", description = "Between 0 and 1." },
            ["reason"] = new { type = "string", description = "One short sentence, in French." },
            ["suggestedRule"] = new
            {
                anyOf = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new[] { "field", "operator", "pattern" },
                        ["properties"] = new
                        {
                            field = new { type = "string", @enum = Enum.GetNames<RuleField>() },
                            @operator = new { type = "string", @enum = Enum.GetNames<RuleOperator>() },
                            pattern = new { type = "string" },
                        },
                    },
                    new { type = "null" },
                },
            },
        }),
    };
}
