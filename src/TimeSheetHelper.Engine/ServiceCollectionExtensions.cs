using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Learning;
using TimeSheetHelper.Engine.Proposals;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the timesheet engine. Requires <c>AddTimeSheetStorage</c>; uses an <c>ISecretStore</c> when registered.</summary>
    public static IServiceCollection AddTimesheetEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EngineOptions>(configuration.GetSection(EngineOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISourceCatalog>(SourceCatalog.Default);
        services.AddLogging();

        services.AddSingleton<RuleEvaluator>();
        services.AddSingleton<GroupKeyBuilder>();
        services.AddSingleton<ActivityResolver>();
        services.AddSingleton<SlotAllocator>();
        services.TryAddSingleton<ITaskClassifier, ClaudeClassifier>();
        services.AddSingleton<ClassificationService>();
        services.AddSingleton<CorrectionLearner>();
        services.AddSingleton<PredictionFeedback>();
        services.AddSingleton<WeekProposalBuilder>();
        services.AddSingleton<WeekService>();
        services.AddSingleton<GroupAssignmentService>();
        return services;
    }
}
