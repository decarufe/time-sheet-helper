using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>Calendar, sent e-mail and Teams messages (metadata only) through Microsoft Graph.</summary>
public sealed class Microsoft365Plugin : ISourcePlugin
{
    public const string PluginId = "microsoft-365";
    public const string SignInAction = "sign-in";
    public const string SignOutAction = "sign-out";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "Microsoft 365",
        Description = "Calendrier, courriels envoyés et conversations Teams (métadonnées seulement) via Microsoft Graph.",
        Platform = "windows",
        Emits =
        [
            new(ActivitySource.Calendar, new("Calendrier", SourceBehavior.Meeting)),
            new(ActivitySource.Email, new("Courriel")),
            new(ActivitySource.Teams, new("Teams")),
        ],
        Config =
        [
            new(nameof(Microsoft365Options.TenantId), "Locataire (TenantId)", ConfigFieldType.Text, Required: true, Help: "GUID ou domaine du locataire Entra ID."),
            new(nameof(Microsoft365Options.ClientId), "Application (ClientId)", ConfigFieldType.Text, Required: true, Help: "Identifiant de l'inscription d'application (client public)."),
            new(nameof(Microsoft365Options.UseBroker), "Connexion unique Windows (WAM)", ConfigFieldType.Boolean, Help: "Sinon, la connexion passe par le navigateur."),
            new(nameof(Microsoft365Options.MaxChats), "Conversations Teams analysées", ConfigFieldType.Number, Help: "Les plus récemment actives d'abord (50 par défaut)."),
            new(nameof(Microsoft365Options.InternalDomains), "Domaines internes", ConfigFieldType.TextList, Help: "Un par ligne : ignorés pour déterminer le client d'un courriel ou d'une réunion."),
        ],
        LegacyConfigSection = Microsoft365Options.SectionName,
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.Configure<Microsoft365Options>(context.Configuration);
        services.AddHttpClient(GraphClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(100));
        services.AddSingleton<GraphAuthenticator>();
        services.AddSingleton<IGraphAccessTokenProvider>(sp => sp.GetRequiredService<GraphAuthenticator>());
        services.AddSingleton<GraphClient>();
        services.AddSingleton<CalendarCollector>();
        services.AddSingleton<EmailCollector>();
        services.AddSingleton<TeamsCollector>();

        var provider = services.BuildServiceProvider();
        ICollector[] collectors =
        [
            provider.GetRequiredService<CalendarCollector>(),
            provider.GetRequiredService<EmailCollector>(),
            provider.GetRequiredService<TeamsCollector>(),
        ];
        return new Instance(provider, collectors, provider.GetRequiredService<GraphAuthenticator>());
    }

    private sealed class Instance(ServiceProvider services, IReadOnlyList<ICollector> collectors, GraphAuthenticator auth)
        : ServiceSourceInstance(services, collectors)
    {
        public override async Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (!auth.IsConfigured)
                return new SourceStatus(false, "Inscription d'application manquante : renseignez le locataire et l'application dans les réglages.");

            bool signedIn;
            try
            {
                signedIn = await auth.IsSignedInAsync(cancellationToken);
            }
            catch (Exception)
            {
                signedIn = false;
            }

            return signedIn
                ? new SourceStatus(true, "Connecté", Actions: [new(SignOutAction, "Se déconnecter")])
                : new SourceStatus(false, GraphAuthenticator.SignInRequiredMessage, Actions: [new(SignInAction, "Se connecter", Primary: true)]);
        }

        public override async Task<SourceActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
        {
            switch (actionId)
            {
                case SignInAction:
                    var user = await auth.SignInAsync(cancellationToken);
                    return new SourceActionResult(true, user is null ? "Connecté." : $"Connecté en tant que {user}.");
                case SignOutAction:
                    await auth.SignOutAsync(cancellationToken);
                    return new SourceActionResult(true, "Déconnecté.");
                default:
                    return await base.ExecuteActionAsync(actionId, cancellationToken);
            }
        }
    }
}
