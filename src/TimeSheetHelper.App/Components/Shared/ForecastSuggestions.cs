using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.App.Components.Shared;

/// <summary>Turns Forecast matches into the lines of the "add a task" suggestion lists.</summary>
public static class ForecastSuggestions
{
    /// <summary>Known (already saved) projects listed above the Forecast ones.</summary>
    public const int MaxKnownProjects = 3;

    public static string StatusLabel(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "RUNNING" => "En cours",
        "PLANNING" => "Planification",
        "HALTED" => "En pause",
        "DONE" => "Terminé",
        "OPPORTUNITY" => "Opportunité",
        null or "" => "",
        _ => status.Trim(),
    };

    /// <summary>
    /// A project line. Under a name field the name leads and the number follows; under a number field it is the other way round.
    /// </summary>
    public static SuggestionOption ProjectOption(ForecastProjectMatch project, bool numberFirst = false)
    {
        var status = StatusLabel(project.Status);
        return numberFirst
            ? new SuggestionOption(Join(" · ", project.Code, project.Name), Join(" · ", project.ClientName, status), project)
            : new SuggestionOption(project.Name, Join(" · ", project.Code, project.ClientName, status), project);
    }

    /// <summary>A task line: the task, then the project it belongs to.</summary>
    public static SuggestionOption TaskOption(ForecastTaskMatch task, bool numberFirst = false)
    {
        var project = Join(" ", task.ProjectCode, task.ProjectName);
        return numberFirst
            ? new SuggestionOption(Join(" · ", task.Code, task.Name), Join(" · ", project, task.ClientName), task)
            : new SuggestionOption(task.Name, Join(" · ", task.Code, project, task.ClientName), task);
    }

    public static SuggestionOption KnownProjectOption(string name) => new(name, "Projet déjà enregistré", name);

    /// <summary>
    /// The lines for a project search: the known projects that match the text (at most <see cref="MaxKnownProjects"/>, those starting with it
    /// first, none when the same project came back from Forecast), then the Forecast ones. A search on a number lists Forecast projects only.
    /// </summary>
    public static SuggestionBatch ProjectBatch(
        ForecastLookupResult<ForecastProjectMatch> result, IEnumerable<string> knownProjects, string text, bool numberFirst)
    {
        var options = new List<SuggestionOption>();
        if (!numberFirst)
        {
            var wanted = ForecastCodes.Normalize(text);
            var fromForecast = result.Items.Select(p => ForecastCodes.Normalize(p.Name)).ToHashSet();
            options.AddRange(knownProjects
                .Select(name => (Name: name, Normalized: ForecastCodes.Normalize(name)))
                .Where(known => wanted.Length > 0 && known.Normalized.Contains(wanted, StringComparison.Ordinal) && !fromForecast.Contains(known.Normalized))
                .OrderBy(known => known.Normalized.StartsWith(wanted, StringComparison.Ordinal) ? 0 : 1)
                .Take(MaxKnownProjects)
                .Select(known => KnownProjectOption(known.Name)));
        }

        options.AddRange(result.Items.Select(p => ProjectOption(p, numberFirst)));
        return new SuggestionBatch(options, result.HasMore, result.Error);
    }

    public static SuggestionBatch TaskBatch(ForecastLookupResult<ForecastTaskMatch> result, bool numberFirst) =>
        new(result.Items.Select(t => TaskOption(t, numberFirst)).ToList(), result.HasMore, result.Error);

    /// <summary>The line read out under a list: how many were found, or why nothing is listed.</summary>
    public static string StatusText(SuggestionSearch search) => search.Status switch
    {
        SuggestionStatus.Loading => "Recherche dans Forecast…",
        SuggestionStatus.Empty => "Aucun résultat dans Forecast.",
        SuggestionStatus.Error => search.Error ?? "La recherche dans Forecast a échoué.",
        SuggestionStatus.Results => $"{search.Options.Count} {(search.Options.Count > 1 ? "suggestions" : "suggestion")}"
            + (search.HasMore ? " — plus de résultats : précisez votre saisie." : ".")
            + (search.Error is { } error ? $" {error}" : ""),
        _ => "",
    };

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
