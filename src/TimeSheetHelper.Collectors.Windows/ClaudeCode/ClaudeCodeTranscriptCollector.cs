using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.Windows.ClaudeCode;

/// <summary>
/// Reads Claude Code session transcripts (%USERPROFILE%\.claude\projects\*\*.jsonl) and records one
/// "ai-session" segment per burst of activity in a session. Privacy: no prompt or response text is stored;
/// the subject is the project folder name.
/// </summary>
public sealed class ClaudeCodeTranscriptCollector : ICollector
{
    public const string SessionKind = "ai-session";

    private readonly IActivitySink _sink;
    private readonly ILogger<ClaudeCodeTranscriptCollector> _logger;
    private readonly string _projectsRoot;

    public ClaudeCodeTranscriptCollector(IActivitySink sink, ILogger<ClaudeCodeTranscriptCollector> logger)
        : this(sink, logger, DefaultProjectsRoot)
    {
    }

    internal ClaudeCodeTranscriptCollector(IActivitySink sink, ILogger<ClaudeCodeTranscriptCollector> logger, string projectsRoot)
    {
        _sink = sink;
        _logger = logger;
        _projectsRoot = projectsRoot;
    }

    public static string DefaultProjectsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public string Name => "claudecode";

    public TimeSpan SplitGap { get; init; } = ClaudeCodeTranscriptParser.DefaultSplitGap;

    public Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Directory.Exists(_projectsRoot)
            ? new CollectorStatus(true)
            : new CollectorStatus(false, $"Aucune session Claude Code trouvée ({_projectsRoot})."));

    public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_projectsRoot))
            return CollectorResult.Of(0);

        var warnings = new List<string>();
        var repoByFolder = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var file in EnumerateTranscripts(fromUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ClaudeCodeSegment> segments;
            try
            {
                segments = ClaudeCodeTranscriptParser.Parse(ReadLinesShared(file), SplitGap, Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot read Claude Code transcript {File}", file);
                warnings.Add($"Transcription illisible : {Path.GetFileName(file)}");
                continue;
            }

            foreach (var segment in segments)
            {
                if (segment.EndUtc <= segment.StartUtc || segment.StartUtc >= toUtc || segment.EndUtc < fromUtc)
                    continue;

                var activity = ToActivity(segment, repoByFolder);
                await _sink.WriteAsync(activity, cancellationToken);
                count++;
            }
        }

        return new CollectorResult(count, warnings);
    }

    internal static ActivityEvent ToActivity(ClaudeCodeSegment segment, Dictionary<string, string?>? repoCache = null)
    {
        string? repo = null;
        if (segment.Cwd is { } cwd)
        {
            if (repoCache is null || !repoCache.TryGetValue(cwd, out repo))
            {
                repo = GitRemote.Normalize(GitRemote.FindRemoteUrl(cwd));
                repoCache?.TryAdd(cwd, repo);
            }
        }

        return new ActivityEvent
        {
            Source = ActivitySource.ClaudeCode,
            Kind = SessionKind,
            StartUtc = segment.StartUtc,
            EndUtc = segment.EndUtc,
            ProcessName = "claude",
            Folder = segment.Cwd,
            Repo = repo,
            Title = segment.GitBranch,
            Subject = segment.Cwd is { } folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : null,
            ExternalId = $"{segment.SessionId}@{segment.StartUtc.ToString("O", CultureInfo.InvariantCulture)}",
        };
    }

    private IEnumerable<string> EnumerateTranscripts(DateTime fromUtc)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_projectsRoot, "*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 1, // <project>\<session>.jsonl only; subagent transcripts overlap their parent session.
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot enumerate {Root}", _projectsRoot);
            yield break;
        }

        foreach (var file in files)
        {
            // A transcript last written before the range cannot contain messages inside it.
            if (Path.GetDirectoryName(Path.GetDirectoryName(file)) is { } parent
                && string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(_projectsRoot), StringComparison.OrdinalIgnoreCase)
                && File.GetLastWriteTimeUtc(file) >= fromUtc)
            {
                yield return file;
            }
        }
    }

    // Claude Code appends to the transcript while it runs: open with FileShare.ReadWrite.
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
