using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>Sent mail metadata (no body) from the Sent Items folder.</summary>
internal sealed class EmailCollector(
    GraphClient graph,
    IGraphAccessTokenProvider tokens,
    IActivitySink sink,
    IOptionsMonitor<Microsoft365Options> options,
    ILogger<EmailCollector> logger) : Microsoft365CollectorBase(graph, tokens, sink, options, logger)
{
    private const string Select = "id,internetMessageId,subject,sentDateTime,toRecipients,ccRecipients,isDraft,webLink";

    public override string Name => "email";

    protected override async Task<int> SyncCoreAsync(DateTime fromUtc, DateTime toUtc, List<string> warnings, CancellationToken cancellationToken)
    {
        var me = await GetMeAsync(cancellationToken);
        var internalDomains = InternalDomains(me);

        var filter = Uri.EscapeDataString($"sentDateTime ge {ODataDate(fromUtc)} and sentDateTime lt {ODataDate(toUtc)}");
        var url = $"me/mailFolders/sentitems/messages?$filter={filter}&$orderby=sentDateTime desc&$select={Select}&$top=100";

        var count = 0;
        await foreach (var message in Graph.GetAllAsync(url, cancellationToken))
        {
            if (GraphMapping.MapSentMessage(message, internalDomains) is not { } activity)
                continue;
            await Sink.WriteAsync(activity, cancellationToken);
            count++;
        }
        return count;
    }
}
