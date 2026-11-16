using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.Windows.Copilot;

/// <summary>A VS Code user folder whose <c>workspaceStorage</c> holds Copilot Chat sessions.</summary>
/// <param name="UserFolder">e.g. <c>%APPDATA%\Code\User</c>.</param>
/// <param name="Client">Recorded as the process name, e.g. "vscode".</param>
public sealed record VsCodeUserFolder(string UserFolder, string Client);

/// <summary>
/// Reads GitHub Copilot conversations from the local files of VS Code (stable and Insiders) and Copilot CLI, and
/// records one "ai-session" segment per burst of activity, tied to the folder (and repository) the conversation ran in.
/// There is no API exposing a user's Copilot history per project, so the local files are the only source.
/// Privacy: no prompt or response text is stored. Conversations of windows without a folder are ignored, and so are
/// VS Code's <c>GitHub.copilot-chat\transcripts</c>, which duplicate <c>chatSessions</c>.
/// A session id seen in several places (e.g. a VS Code agent-host session) is counted once.
/// </summary>
public sealed class CopilotCollector : ICollector
{
    public const string SessionKind = "ai-session";
    public const string CliClient = "copilot-cli";

    private readonly IActivitySink _sink;
    private readonly ILogger<CopilotCollector> _logger;
    private readonly IReadOnlyList<VsCodeUserFolder> _vsCodeFolders;
    private readonly string _cliSessionRoot;
    private readonly TimeProvider _time;

    public CopilotCollector(IActivitySink sink, ILogger<CopilotCollector> logger, TimeProvider time)
        : this(sink, logger, DefaultVsCodeFolders, DefaultCliSessionRoot, time)
    {
    }

    internal CopilotCollector(
        IActivitySink sink,
        ILogger<CopilotCollector> logger,
        IReadOnlyList<VsCodeUserFolder> vsCodeFolders,
        string cliSessionRoot,
        TimeProvider? time = null)
    {
        _sink = sink;
        _logger = logger;
        _vsCodeFolders = vsCodeFolders;
        _cliSessionRoot = cliSessionRoot;
        _time = time ?? TimeProvider.System;
    }

    public static IReadOnlyList<VsCodeUserFolder> DefaultVsCodeFolders
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return
            [
                new(Path.Combine(appData, "Code", "User"), "vscode"),
                new(Path.Combine(appData, "Code - Insiders", "User"), "vscode-insiders"),
            ];
        }
    }

    public static string DefaultCliSessionRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    public string Name => "copilot";

    public TimeSpan SplitGap { get; init; } = CopilotSegmenter.DefaultSplitGap;

    public Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var found = _vsCodeFolders.Any(f => Directory.Exists(WorkspaceStorage(f))) || Directory.Exists(_cliSessionRoot);
        return Task.FromResult(found
            ? new CollectorStatus(true)
            : new CollectorStatus(false, $"Aucune conversation Copilot trouvée (VS Code ou {_cliSessionRoot})."));
    }

    public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var seenSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repoByFolder = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        // The CLI first: its log names the repository and branch, which VS Code's does not.
        foreach (var (session, client) in ReadCliSessions(fromUtc, warnings).Concat(ReadVsCodeSessions(fromUtc, warnings)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.TimestampsUtc.Count == 0 || !seenSessions.Add(session.SessionId))
                continue;

            foreach (var segment in CopilotSegmenter.Split(session, client, SplitGap))
            {
                if (segment.EndUtc <= segment.StartUtc || segment.StartUtc >= toUtc || segment.EndUtc < fromUtc)
                    continue;

                await _sink.WriteAsync(ToActivity(segment, repoByFolder), cancellationToken);
                count++;
            }
        }

        return new CollectorResult(count, warnings);
    }

    internal static ActivityEvent ToActivity(CopilotSegment segment, Dictionary<string, string?>? repoCache = null)
    {
        string? repo = null;
        if (segment.Folder is { } folder)
        {
            if (repoCache is null || !repoCache.TryGetValue(folder, out repo))
            {
                repo = GitRemote.Normalize(GitRemote.FindRemoteUrl(folder));
                repoCache?.TryAdd(folder, repo);
            }
        }

        return new ActivityEvent
        {
            Source = ActivitySource.Copilot,
            Kind = SessionKind,
            StartUtc = segment.StartUtc,
            EndUtc = segment.EndUtc,
            ProcessName = segment.Client,
            Folder = segment.Folder,
            Repo = repo ?? NormalizeRepository(segment.Repository),
            Title = segment.Branch,
            Subject = segment.Folder is { } f ? Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) : null,
            ExternalId = $"{segment.SessionId}@{segment.StartUtc.ToString("O", CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>
    /// The CLI names the repository "owner/repo" on GitHub and "org/project/repo" on Azure DevOps; the activity model
    /// stores "owner/repo" and the bare repository name respectively (see <see cref="GitRemote.Normalize"/>).
    /// </summary>
    internal static string? NormalizeRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return null;
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => null,
            1 or 2 => string.Join('/', parts),
            _ => parts[^1],
        };
    }

    private IEnumerable<(CopilotSession Session, string Client)> ReadCliSessions(DateTime fromUtc, List<string> warnings)
    {
        if (!Directory.Exists(_cliSessionRoot))
            yield break;

        // Current layout: <root>\<session>\events.jsonl (+ workspace.yaml).
        foreach (var dir in SafeEnumerate(() => Directory.EnumerateDirectories(_cliSessionRoot)))
        {
            var events = Path.Combine(dir, "events.jsonl");
            if (!File.Exists(events) || File.GetLastWriteTimeUtc(events) < fromUtc)
                continue;

            var session = TryRead(events, warnings, path => CopilotCliEventsParser.Parse(ReadLinesShared(path), Path.GetFileName(dir)));
            if (session is null)
                continue;

            if (session.Folder is null && File.Exists(Path.Combine(dir, "workspace.yaml")))
            {
                var yaml = TryRead(Path.Combine(dir, "workspace.yaml"), warnings,
                    path => CopilotCliEventsParser.ReadWorkspaceYaml(ReadLinesShared(path)));
                if (yaml is { } meta)
                    session = session with { Folder = meta.Folder, Repository = session.Repository ?? meta.Repository, Branch = session.Branch ?? meta.Branch };
            }

            yield return (session, CliClient);
        }

        // Layout before Copilot CLI 1.0.11: <root>\<session>.jsonl.
        foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(_cliSessionRoot, "*.jsonl")))
        {
            if (File.GetLastWriteTimeUtc(file) < fromUtc)
                continue;
            if (TryRead(file, warnings, path => CopilotCliEventsParser.Parse(ReadLinesShared(path), Path.GetFileNameWithoutExtension(path))) is { } session)
                yield return (session, CliClient);
        }
    }

    private IEnumerable<(CopilotSession Session, string Client)> ReadVsCodeSessions(DateTime fromUtc, List<string> warnings)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var userFolder in _vsCodeFolders)
        {
            var storage = WorkspaceStorage(userFolder);
            if (!Directory.Exists(storage))
                continue;

            foreach (var workspaceDir in SafeEnumerate(() => Directory.EnumerateDirectories(storage)))
            {
                var chatDir = Path.Combine(workspaceDir, "chatSessions");
                if (!Directory.Exists(chatDir))
                    continue;

                // .jsonl first: when VS Code migrated a session, both files may exist with the same id.
                var files = SafeEnumerate(() => Directory.EnumerateFiles(chatDir, "*.jsonl"))
                    .Concat(SafeEnumerate(() => Directory.EnumerateFiles(chatDir, "*.json")))
                    .Where(f => File.GetLastWriteTimeUtc(f) >= fromUtc)
                    .ToList();
                if (files.Count == 0)
                    continue;

                var workspaceJson = Path.Combine(workspaceDir, "workspace.json");
                var folder = File.Exists(workspaceJson)
                    ? TryRead(workspaceJson, warnings, path => VsCodeChatSessionParser.ReadWorkspaceFolder(ReadAllTextShared(path)))
                    : null;
                if (folder is null)
                    continue; // Remote or folder-less workspace: the conversation cannot be tied to a project.

                foreach (var file in files)
                {
                    var fallbackId = Path.GetFileNameWithoutExtension(file);
                    var session = file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                        ? TryRead(file, warnings, path => VsCodeChatSessionParser.ParseLines(ReadLinesShared(path), fallbackId, now))
                        : TryRead(file, warnings, path => VsCodeChatSessionParser.ParseDocument(ReadAllTextShared(path), fallbackId, now));
                    if (session is not null)
                        yield return (session with { Folder = folder }, userFolder.Client);
                }
            }
        }
    }

    private static string WorkspaceStorage(VsCodeUserFolder folder) => Path.Combine(folder.UserFolder, "workspaceStorage");

    private T? TryRead<T>(string path, List<string> warnings, Func<string, T> read)
    {
        try
        {
            return read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot read Copilot file {File}", path);
            warnings.Add($"Conversation Copilot illisible : {Path.GetFileName(path)}");
            return default;
        }
    }

    private IReadOnlyList<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot enumerate Copilot folder");
            return [];
        }
    }

    // VS Code and the CLI append while they run: open with FileShare.ReadWrite.
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
