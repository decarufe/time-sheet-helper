namespace TimeSheetHelper.Core.Collectors;

/// <summary>A source pulled on demand for a time range (GitHub, Azure DevOps, Graph, ...). Writes to an IActivitySink.</summary>
public interface ICollector
{
    /// <summary>Short identifier shown in the UI and used on the command line, e.g. "github".</summary>
    string Name { get; }

    /// <summary>Reports missing credentials or configuration; the UI shows <see cref="CollectorStatus.Message"/>.</summary>
    Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
}

public sealed record CollectorStatus(bool IsConfigured, string? Message = null);

public sealed record CollectorResult(int EventCount, IReadOnlyList<string> Warnings)
{
    public static CollectorResult Of(int count) => new(count, []);
}
