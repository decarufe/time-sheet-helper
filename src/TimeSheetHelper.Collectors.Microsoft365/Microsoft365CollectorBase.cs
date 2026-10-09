using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Collectors;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>Shared status, sign-in handling and error reporting for the Graph collectors.</summary>
internal abstract class Microsoft365CollectorBase(
    GraphClient graph,
    IGraphAccessTokenProvider tokens,
    IActivitySink sink,
    IOptionsMonitor<Microsoft365Options> options,
    ILogger logger) : ICollector
{
    protected GraphClient Graph { get; } = graph;
    protected IActivitySink Sink { get; } = sink;
    protected Microsoft365Options Options => options.CurrentValue;
    protected ILogger Logger { get; } = logger;

    public abstract string Name { get; }

    public async Task<CollectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!tokens.IsConfigured)
            return new CollectorStatus(false, GraphAuthenticator.NotConfiguredMessage);
        if (!await tokens.IsSignedInAsync(cancellationToken))
            return new CollectorStatus(false, GraphAuthenticator.SignInRequiredMessage);
        return new CollectorStatus(true);
    }

    public async Task<CollectorResult> SyncAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        if (!tokens.IsConfigured)
            return new CollectorResult(0, [GraphAuthenticator.NotConfiguredMessage]);

        var warnings = new List<string>();
        try
        {
            var count = await SyncCoreAsync(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), DateTime.SpecifyKind(toUtc, DateTimeKind.Utc), warnings, cancellationToken);
            return new CollectorResult(count, warnings);
        }
        catch (Microsoft365SignInRequiredException ex)
        {
            return new CollectorResult(0, [.. warnings, ex.Message]);
        }
        catch (GraphRequestException ex)
        {
            Logger.LogWarning(ex, "Microsoft 365 {Collector} sync failed", Name);
            return new CollectorResult(0, [.. warnings, $"Erreur Microsoft Graph ({(int)ex.StatusCode}) : {ex.ErrorCode ?? ex.Message}"]);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Microsoft 365 {Collector} sync failed", Name);
            return new CollectorResult(0, [.. warnings, $"Microsoft Graph injoignable : {ex.Message}"]);
        }
    }

    protected abstract Task<int> SyncCoreAsync(DateTime fromUtc, DateTime toUtc, List<string> warnings, CancellationToken cancellationToken);

    protected async Task<GraphMe> GetMeAsync(CancellationToken cancellationToken) =>
        GraphMapping.ParseMe(await Graph.GetAsync("me?$select=id,mail,userPrincipalName", cancellationToken));

    protected HashSet<string> InternalDomains(GraphMe me)
    {
        var domains = new HashSet<string>(Options.InternalDomains.Select(d => d.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        if (me.Domain is { } own)
            domains.Add(own);
        return domains;
    }

    protected static string ODataDate(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
