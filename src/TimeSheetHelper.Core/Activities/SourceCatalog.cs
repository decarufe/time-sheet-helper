using System.Collections.Concurrent;

namespace TimeSheetHelper.Core.Activities;

/// <summary>How the timesheet engine uses the activities of a source.</summary>
public enum SourceBehavior
{
    /// <summary>Point events that only hint at what was being worked on (commit, sent email, ...).</summary>
    Evidence,

    /// <summary>Intervals proving the user was working (focused window, editor, AI session); points are evidence.</summary>
    Presence,

    /// <summary>Intervals that are not work time (idle, locked session): they mask the presence sources.</summary>
    NonWork,

    /// <summary>Scheduled meetings that take precedence over presence while they last.</summary>
    Meeting,
}

/// <param name="DisplayName">Label shown in the UI.</param>
/// <param name="Behavior">How the engine treats the activities.</param>
/// <param name="IsEditing">Active editing time: a meeting stops overriding focus when there is enough of it in a slot.</param>
/// <param name="DefaultWeight">Weight per second of overlap for <see cref="SourceBehavior.Presence"/> sources (1 when null).</param>
/// <param name="DefaultEvidenceWindow">Implicit duration before a point event (the engine default when null).</param>
public sealed record SourceTraits(
    string DisplayName,
    SourceBehavior Behavior = SourceBehavior.Evidence,
    bool IsEditing = false,
    double? DefaultWeight = null,
    TimeSpan? DefaultEvidenceWindow = null);

public interface ISourceCatalog
{
    /// <summary>Traits of a source; a source nobody declared is plain evidence labelled with its identifier.</summary>
    SourceTraits Get(ActivitySource source);

    IReadOnlyCollection<ActivitySource> Known { get; }
}

/// <summary>
/// Traits of the known sources. <see cref="Default"/> holds those of the sources shipped with the application, so
/// activities already recorded stay interpretable when their plugin is removed; plugin descriptors add to or override them.
/// </summary>
public sealed class SourceCatalog : ISourceCatalog
{
    private readonly ConcurrentDictionary<ActivitySource, SourceTraits> _traits = new();

    /// <summary>Shared read-only instance with the built-in sources; never register into it.</summary>
    public static SourceCatalog Default { get; } = CreateDefault();

    public static SourceCatalog CreateDefault()
    {
        var catalog = new SourceCatalog();
        catalog.Register(ActivitySource.Focus, new("Fenêtre active", SourceBehavior.Presence, DefaultWeight: 1.0));
        catalog.Register(ActivitySource.Idle, new("Inactif", SourceBehavior.NonWork));
        catalog.Register(ActivitySource.Locked, new("Verrouillé", SourceBehavior.NonWork));
        catalog.Register(ActivitySource.VsCode, new("VS Code", SourceBehavior.Presence, IsEditing: true, DefaultWeight: 1.5));
        catalog.Register(ActivitySource.ClaudeCode, new("Claude Code", SourceBehavior.Presence, DefaultWeight: 1.2));
        catalog.Register(ActivitySource.GitHub, new("GitHub"));
        catalog.Register(ActivitySource.AzureDevOps, new("Azure DevOps"));
        catalog.Register(ActivitySource.LocalGit, new("Git local"));
        catalog.Register(ActivitySource.Calendar, new("Calendrier", SourceBehavior.Meeting));
        catalog.Register(ActivitySource.Email, new("Courriel"));
        catalog.Register(ActivitySource.Teams, new("Teams"));
        return catalog;
    }

    public IReadOnlyCollection<ActivitySource> Known => _traits.Keys.OrderBy(k => k.Id, StringComparer.Ordinal).ToList();

    public void Register(ActivitySource source, SourceTraits traits) => _traits[source] = traits;

    public SourceTraits Get(ActivitySource source) =>
        _traits.TryGetValue(source, out var traits) ? traits : new SourceTraits(source.ToString());
}
