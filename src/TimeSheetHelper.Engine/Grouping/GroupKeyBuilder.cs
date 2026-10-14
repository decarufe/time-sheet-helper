using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Engine.Grouping;

/// <summary>Kind of evidence a group key was built from, most specific first.</summary>
public enum GroupKind
{
    Repo,
    Folder,
    Meeting,
    Domain,
    People,
    App,
    Source,
}

/// <summary>Parsed group key, e.g. "repo:contoso/api" or "meeting:daily standup".</summary>
public readonly record struct GroupKey(GroupKind Kind, string Value)
{
    public override string ToString() => $"{Prefix(Kind)}:{Value}";

    public static string Prefix(GroupKind kind) => kind switch
    {
        GroupKind.Repo => "repo",
        GroupKind.Folder => "folder",
        GroupKind.Meeting => "meeting",
        GroupKind.Domain => "domain",
        GroupKind.People => "people",
        GroupKind.App => "app",
        _ => "source",
    };

    public static bool TryParse(string? text, out GroupKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text))
            return false;
        var colon = text.IndexOf(':');
        if (colon <= 0)
            return false;

        var prefix = text[..colon];
        foreach (var kind in Enum.GetValues<GroupKind>())
        {
            if (Prefix(kind) == prefix)
            {
                key = new GroupKey(kind, text[(colon + 1)..]);
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Puts activities that no rule classifies into groups sharing the most specific stable key:
/// repository, else folder, else meeting subject, else e-mail domains / participants, else process + title stem.
/// </summary>
public sealed partial class GroupKeyBuilder(IOptions<EngineOptions> options, ISourceCatalog? catalog = null)
{
    private readonly ISourceCatalog _catalog = catalog ?? SourceCatalog.Default;

    private const int MaxStemLength = 60;

    public string Build(ActivityEvent activity) => BuildKey(activity).ToString();

    public GroupKey BuildKey(ActivityEvent activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.Repo))
            return new(GroupKind.Repo, activity.Repo.Trim().ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(activity.Folder))
            return new(GroupKind.Folder, NormalizeFolder(activity.Folder));

        if (_catalog.Get(activity.Source).Behavior == SourceBehavior.Meeting && NormalizeSubject(activity.Subject) is { Length: > 0 } subject)
            return new(GroupKind.Meeting, subject);

        if (!string.IsNullOrWhiteSpace(activity.Participants))
        {
            var domains = ExternalDomains(activity.Participants);
            if (domains.Count > 0)
                return new(GroupKind.Domain, string.Join(',', domains.Take(3)));

            var people = SplitParticipants(activity.Participants)
                .Select(p => p.ToLowerInvariant())
                .Distinct()
                .Order(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (people.Count > 0)
                return new(GroupKind.People, string.Join(',', people));
        }

        if (!string.IsNullOrWhiteSpace(activity.ProcessName))
        {
            var stem = TitleStem(activity.Title);
            var process = activity.ProcessName.Trim().ToLowerInvariant();
            return new(GroupKind.App, stem.Length > 0 ? $"{process}:{stem}" : process);
        }

        // Last resort: sources without process (e.g. a Teams message to nobody known).
        var fallback = NormalizeSubject(activity.Subject ?? activity.Title);
        return new(GroupKind.Source, fallback.Length > 0 ? $"{activity.Source.ToString().ToLowerInvariant()}:{fallback}" : activity.Source.ToString().ToLowerInvariant());
    }

    public static string NormalizeFolder(string folder) =>
        folder.Trim().TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>Lower-cased subject without reply/forward prefixes, numbers and punctuation.</summary>
    public static string NormalizeSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "";

        var s = subject.Trim().ToLowerInvariant();
        s = ReplyPrefix().Replace(s, "");
        s = StandaloneNumbers().Replace(s, " ");
        s = NonWord().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>Stable part of a window title: first segment, without unsaved markers or numbers.</summary>
    public static string TitleStem(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";

        var s = title.Trim().TrimStart('●', '*', ' ');
        var cut = TitleSeparator().Match(s);
        if (cut.Success && cut.Index > 0)
            s = s[..cut.Index];

        s = Digits().Replace(s.ToLowerInvariant(), " ");
        s = Spaces().Replace(s, " ").Trim();
        return s.Length > MaxStemLength ? s[..MaxStemLength].TrimEnd() : s;
    }

    public static IEnumerable<string> SplitParticipants(string participants) =>
        participants.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? DomainOf(string participant)
    {
        var at = participant.LastIndexOf('@');
        if (at < 0 || at == participant.Length - 1)
            return null;
        return participant[(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
    }

    public IReadOnlyList<string> ExternalDomains(string participants)
    {
        var own = options.Value.OwnDomains;
        return SplitParticipants(participants)
            .Select(DomainOf)
            .OfType<string>()
            .Where(d => !own.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [GeneratedRegex(@"^((re|tr|fw|fwd|réf|ref|rép|aw|wg)\s*:\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    // Free-standing numbers only: digits glued to a word ("o365", "3d") or hyphenated to one ("1-on-1") are part of the name.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<!\p{L}-)\d+(?![\p{L}\p{N}])(?!-\p{L})")]
    private static partial Regex StandaloneNumbers();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+[-–—|]\s+")]
    private static partial Regex TitleSeparator();
}
