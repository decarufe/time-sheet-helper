using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests.App;

/// <summary>Hand-made <see cref="IForecastLookup"/>: answers from lists, records the calls, and can hold an answer back until the test lets it go.</summary>
internal sealed class FakeForecastLookup : IForecastLookup
{
    public List<ForecastProjectMatch> Projects { get; } = [];

    public List<ForecastTaskMatch> Tasks { get; } = [];

    public ForecastAutoFillResult AutoFill { get; set; } = ForecastAutoFillResult.Empty;

    public string? Error { get; set; }

    public List<string?> ProjectQueries { get; } = [];

    public List<(string? Query, int? ProjectId)> TaskQueries { get; } = [];

    public List<(string? Project, string? Task)> AutoFillCalls { get; } = [];

    public Task<ForecastLookupResult<ForecastProjectMatch>> SearchProjectsAsync(string? query, CancellationToken cancellationToken = default)
    {
        ProjectQueries.Add(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (Error is not null)
            return Task.FromResult(ForecastLookupResult<ForecastProjectMatch>.Failure(Error));
        var found = Projects.Where(p => Matches(query, p.Name, p.Code)).ToList();
        return Task.FromResult(new ForecastLookupResult<ForecastProjectMatch>(found, found.Count));
    }

    public Task<ForecastLookupResult<ForecastTaskMatch>> SearchTasksAsync(string? query, int? projectId = null, CancellationToken cancellationToken = default)
    {
        TaskQueries.Add((query, projectId));
        cancellationToken.ThrowIfCancellationRequested();
        if (Error is not null)
            return Task.FromResult(ForecastLookupResult<ForecastTaskMatch>.Failure(Error));
        var found = Tasks.Where(t => (projectId is null || t.ProjectId == projectId) && Matches(query, t.Name, t.Code)).ToList();
        return Task.FromResult(new ForecastLookupResult<ForecastTaskMatch>(found, found.Count));
    }

    public Task<ForecastGetResult<ForecastProjectMatch>> GetProjectAsync(string? projectNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ForecastGetResult<ForecastProjectMatch>(Projects.FirstOrDefault(p => p.Code == projectNumber)));

    public Task<ForecastGetResult<ForecastTaskMatch>> GetTaskAsync(string? taskNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ForecastGetResult<ForecastTaskMatch>(Tasks.FirstOrDefault(t => t.Code == taskNumber)));

    public Task<ForecastAutoFillResult> AutoFillAsync(string? projectNumber, string? taskNumber, CancellationToken cancellationToken = default)
    {
        AutoFillCalls.Add((projectNumber, taskNumber));
        return Task.FromResult(AutoFill);
    }

    private static bool Matches(string? query, params string[] fields) =>
        fields.Any(field => field.Contains((query ?? "").Trim(), StringComparison.CurrentCultureIgnoreCase));
}
