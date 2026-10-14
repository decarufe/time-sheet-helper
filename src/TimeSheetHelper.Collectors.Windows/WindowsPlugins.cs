using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TimeSheetHelper.Collectors.Windows.ClaudeCode;
using TimeSheetHelper.Collectors.Windows.VsCode;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;
using TimeSheetHelper.Plugins;

namespace TimeSheetHelper.Collectors.Windows;

/// <summary>Records the focused window, idle and locked periods.</summary>
public sealed class FocusTrackerPlugin : ISourcePlugin
{
    public const string PluginId = "focus-tracker";
    public const string TogglePauseAction = "toggle-pause";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "Fenêtre active (Windows)",
        Description = "Enregistre la fenêtre au premier plan, le fichier affiché, et les périodes d'inactivité ou de session verrouillée.",
        Mode = SourceExecutionMode.Continuous,
        Platform = "windows",
        Emits =
        [
            new(ActivitySource.Focus, new("Fenêtre active", SourceBehavior.Presence, DefaultWeight: 1.0)),
            new(ActivitySource.Idle, new("Inactif", SourceBehavior.NonWork)),
            new(ActivitySource.Locked, new("Verrouillé", SourceBehavior.NonWork)),
        ],
        Config =
        [
            new("IdleThreshold", "Délai d'inactivité", ConfigFieldType.Text, Help: "Au-delà (hh:mm:ss), l'absence de saisie est comptée comme inactivité.", Placeholder: "00:05:00"),
            new("ExcludedProcesses", "Processus exclus", ConfigFieldType.TextList, Help: "Un par ligne. Leur titre et leurs fichiers ne sont jamais enregistrés (ex. KeePass)."),
            new("ExcludedTitleKeywords", "Mots-clés de titre exclus", ConfigFieldType.TextList, Help: "Un par ligne. Un titre qui en contient un est masqué (ex. InPrivate)."),
        ],
        LegacyConfigSection = FocusTrackerOptions.SectionName,
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.Configure<FocusTrackerOptions>(context.Configuration);
        services.AddSingleton<FileContextResolver>();
        services.AddSingleton<FocusTracker>();
        var provider = services.BuildServiceProvider();
        return new Instance(provider, provider.GetRequiredService<FocusTracker>());
    }

    private sealed class Instance(ServiceProvider services, FocusTracker tracker) : ServiceSourceInstance(services)
    {
        public override Task StartAsync(CancellationToken cancellationToken) => tracker.StartAsync(cancellationToken);

        public override Task StopAsync(CancellationToken cancellationToken) => tracker.StopAsync(cancellationToken);

        public override Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var details = new List<StatusDetail>();
            if (tracker.IsPaused)
            {
                details.Add(new("État", "En pause : rien n'est enregistré."));
            }
            else if (tracker.Current is { } current)
            {
                details.Add(new("Maintenant", current.State switch
                {
                    FocusState.Idle => "Inactif (aucune saisie clavier ou souris)",
                    FocusState.Locked => "Session verrouillée",
                    _ => string.IsNullOrWhiteSpace(current.Title) ? current.ProcessName ?? "" : $"{current.ProcessName} — {current.Title}",
                }));
                if ((current.FilePath ?? current.Folder) is { } where)
                    details.Add(new("Fichier / dossier", where));
                if (tracker.CurrentSinceUtc is { } since)
                    details.Add(new("Depuis", since.ToLocalTime().ToString("HH:mm:ss")));
            }
            else
            {
                details.Add(new("État", "En attente du premier changement de fenêtre…"));
            }

            var action = new SourceAction(TogglePauseAction, tracker.IsPaused ? "Reprendre la surveillance" : "Mettre en pause");
            return Task.FromResult(new SourceStatus(true, tracker.IsPaused ? "En pause" : null, details, [action]));
        }

        public override Task<SourceActionResult> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
        {
            if (actionId != TogglePauseAction)
                return base.ExecuteActionAsync(actionId, cancellationToken);

            tracker.SetPaused(!tracker.IsPaused);
            return Task.FromResult(new SourceActionResult(true, tracker.IsPaused ? "Surveillance en pause." : "Surveillance reprise."));
        }
    }
}

/// <summary>Receives the heartbeats of the VS Code extension.</summary>
public sealed class VsCodePlugin : ISourcePlugin
{
    public const string PluginId = "vscode";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "Extension VS Code",
        Description = "Reçoit un signal toutes les 30 secondes environ de l'extension quand VS Code est ouvert : fichier, dépôt, branche, débogage, assistant IA.",
        Mode = SourceExecutionMode.Push,
        PushRoute = "vscode",
        Emits = [new(ActivitySource.VsCode, new("VS Code", SourceBehavior.Presence, IsEditing: true, DefaultWeight: 1.5))],
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.AddSingleton<VsCodeIngestService>();
        var provider = services.BuildServiceProvider();
        return new Instance(provider, provider.GetRequiredService<VsCodeIngestService>(), context.Time);
    }

    private sealed class Instance(ServiceProvider services, VsCodeIngestService ingest, TimeProvider time)
        : ServiceSourceInstance(services), IPushReceiver
    {
        public async Task<bool> ReceiveAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            VsCodeHeartbeat? heartbeat;
            try
            {
                heartbeat = JsonSerializer.Deserialize<VsCodeHeartbeat>(body.Span, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return false;
            }

            if (heartbeat is null)
                return false;

            await ingest.IngestAsync(heartbeat, cancellationToken);
            return true;
        }

        public override Task<SourceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (ingest.LastHeartbeatUtc is not { } last)
                return Task.FromResult(new SourceStatus(true, "Aucun signal reçu depuis le démarrage de l'application."));

            var recent = now - last < TimeSpan.FromMinutes(5);
            return Task.FromResult(new SourceStatus(true, recent ? "Connectée" : "Aucun signal récent",
                [new("Dernier signal", last.ToLocalTime().ToString("HH:mm:ss"))]));
        }
    }
}

/// <summary>Reads Claude Code session transcripts.</summary>
public sealed class ClaudeCodePlugin : ISourcePlugin
{
    public const string PluginId = "claude-code";

    public SourceDescriptor Describe() => new()
    {
        Id = PluginId,
        DisplayName = "Claude Code",
        Description = "Lit les sessions Claude Code (%USERPROFILE%\\.claude\\projects) : seul le dossier du projet est conservé, jamais le texte des échanges.",
        Mode = SourceExecutionMode.Pull,
        Platform = "windows",
        Emits = [new(ActivitySource.ClaudeCode, new("Claude Code", SourceBehavior.Presence, DefaultWeight: 1.2))],
    };

    public ISourceInstance Create(IPluginContext context)
    {
        var services = new ServiceCollection().AddPluginContext(context);
        services.AddSingleton<ClaudeCodeTranscriptCollector>();
        var provider = services.BuildServiceProvider();
        return new ServiceSourceInstance(provider, [provider.GetRequiredService<ClaudeCodeTranscriptCollector>()]);
    }
}
