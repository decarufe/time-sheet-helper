using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Collectors.DevOps.AzureDevOps;
using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Collectors.DevOps.GitHub;
using TimeSheetHelper.Collectors.DevOps.LocalGit;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.Collectors.DevOps;

/// <summary>Commits, pull requests, reviews and workflow runs of the user on GitHub.</summary>
public sealed class GitHubPlugin : ISourcePlugin
{
    public const string PluginId = "github";
    public const string UseGhTokenAction = "use-gh-token";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "GitHub",
        Description = "Commits, pull requests (ouvertes, relues, commentées), commentaires d'issues et exécutions de workflows.",
        Emits = [new(ActivitySource.GitHub, new("GitHub"))],
        Config =
        [
            new(nameof(GitHubOptions.Organizations), "Organisations", ConfigFieldType.TextList, Help: "Une par ligne. Vide : tous les dépôts auxquels vous participez."),
            new(nameof(GitHubOptions.MaxPages), "Pages maximum par recherche", ConfigFieldType.Number, Help: "Garde-fou de pagination (10 par défaut)."),
        ],
        Secrets =
        [
            new(SecretNames.GitHubToken, "Jeton GitHub",
                "Jeton personnel (lecture seule : repo, read:org). Sans jeton, la session de GitHub CLI (gh) est utilisée si elle existe."),
        ],
        LegacyConfigSection = GitHubOptions.SectionName,
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.Configure<GitHubOptions>(context.Configuration);
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<GitHubTokenProvider>();
        services.AddHttpClient<GitHubApiClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<GitHubOptions>>().Value.ApiBaseUrl;
            http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TimeSheetHelper", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
        services.AddSingleton<GitHubCollector>();

        var provider = services.BuildServiceProvider();
        return new Instance(provider, provider.GetRequiredService<GitHubCollector>(), provider.GetRequiredService<IProcessRunner>(), context.Secrets);
    }

    private sealed class Instance(ServiceProvider services, GitHubCollector collector, IProcessRunner processes, ISecretStore secrets)
        : ServiceSourceInstance(services, [collector])
    {
        public override async Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            (await base.GetStatusAsync(cancellationToken)) with { Actions = [new(UseGhTokenAction, "Utiliser gh auth token")] };

        public override async Task<SourceActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
        {
            if (actionId != UseGhTokenAction)
                return await base.ExecuteActionAsync(actionId, cancellationToken);

            var result = await processes.RunAsync("gh", ["auth", "token"], cancellationToken: cancellationToken);
            if (result is null)
                return new SourceActionResult(false, "GitHub CLI (gh) introuvable : installez-le ou ajoutez-le au PATH.");

            var token = result.StandardOutput.Trim();
            if (!result.Succeeded || token.Length == 0)
                return new SourceActionResult(false, "Aucune session gh : exécutez « gh auth login » dans un terminal.");

            secrets.Set(SecretNames.GitHubToken, token);
            return new SourceActionResult(true, "Jeton de la session gh enregistré.");
        }
    }
}

/// <summary>Work items, pull requests, builds and commits of the user in Azure DevOps organizations.</summary>
public sealed class AzureDevOpsPlugin : ISourcePlugin
{
    public const string PluginId = "azure-devops";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "Azure DevOps",
        Description = "Éléments de travail modifiés, pull requests, builds demandés et commits, par organisation.",
        Emits = [new(ActivitySource.AzureDevOps, new("Azure DevOps"))],
        Config =
        [
            new(nameof(AzureDevOpsOptions.Organizations), "Organisations", ConfigFieldType.TextList, Required: true, Help: "Une par ligne (le nom dans https://dev.azure.com/<nom>)."),
            new(nameof(AzureDevOpsOptions.Projects), "Projets", ConfigFieldType.TextList, Help: "Une par ligne. Vide : seulement les projets où vous avez eu de l'activité."),
            new(nameof(AzureDevOpsOptions.MaxProjects), "Projets maximum", ConfigFieldType.Number, Help: "Limite quand les projets sont découverts par l'activité (10 par défaut)."),
            new(nameof(AzureDevOpsOptions.CollectCommits), "Collecter les commits", ConfigFieldType.Boolean),
        ],
        Secrets =
        [
            new(SecretNames.AzureDevOpsPat, "Jeton d'accès Azure DevOps (PAT)", "Portées en lecture : Code, Work Items, Build.", Required: true),
        ],
        LegacyConfigSection = AzureDevOpsOptions.SectionName,
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.Configure<AzureDevOpsOptions>(context.Configuration);
        services.AddHttpClient<AzureDevOpsApiClient>((sp, http) =>
        {
            http.Timeout = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.RequestTimeout;
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TimeSheetHelper", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddSingleton<AzureDevOpsCollector>();

        var provider = services.BuildServiceProvider();
        return new ServiceSourceInstance(provider, [provider.GetRequiredService<AzureDevOpsCollector>()]);
    }
}

/// <summary>Commits authored by the user in local git repositories.</summary>
public sealed class LocalGitPlugin : ISourcePlugin
{
    public const string PluginId = "local-git";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "Git local",
        Description = "Commits de vos dépôts locaux : ceux sous les dossiers racine, plus ceux que vous aviez ouverts pendant la période.",
        Emits = [new(ActivitySource.LocalGit, new("Git local"))],
        Config =
        [
            new(nameof(LocalGitOptions.Roots), "Dossiers racine", ConfigFieldType.TextList, Help: "Un par ligne ; * accepté en fin de chemin (ex. C:\\git*). Vide : C:\\git*.", Placeholder: "C:\\git*"),
            new(nameof(LocalGitOptions.ScanDepth), "Profondeur d'analyse", ConfigFieldType.Number, Help: "Niveaux de sous-dossiers parcourus (2 par défaut)."),
        ],
        LegacyConfigSection = LocalGitOptions.SectionName,
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.Configure<LocalGitOptions>(context.Configuration);
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<LocalGitCollector>();

        var provider = services.BuildServiceProvider();
        return new ServiceSourceInstance(provider, [provider.GetRequiredService<LocalGitCollector>()]);
    }
}
