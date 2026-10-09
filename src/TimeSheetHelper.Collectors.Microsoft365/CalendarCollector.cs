using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>Meetings from /me/calendarView (no body). Skips cancelled, declined, all-day and "free" events.</summary>
internal sealed class CalendarCollector(
    GraphClient graph,
    IGraphAccessTokenProvider tokens,
    IActivitySink sink,
    IOptionsMonitor<Microsoft365Options> options,
    ILogger<CalendarCollector> logger) : Microsoft365CollectorBase(graph, tokens, sink, options, logger)
{
    private const string Select =
        "id,iCalUId,subject,start,end,isAllDay,isCancelled,showAs,responseStatus,organizer,attendees,isOnlineMeeting,onlineMeeting,onlineMeetingUrl,categories";

    // Ask Graph for UTC start/end so no time zone conversion is needed.
    private static readonly IReadOnlyDictionary<string, string> PreferUtc = new Dictionary<string, string>
    {
        ["Prefer"] = "outlook.timezone=\"UTC\"",
    };

    public override string Name => "calendar";

    protected override async Task<int> SyncCoreAsync(DateTime fromUtc, DateTime toUtc, List<string> warnings, CancellationToken cancellationToken)
    {
        var me = await GetMeAsync(cancellationToken);
        var internalDomains = InternalDomains(me);

        var url = $"me/calendarView?startDateTime={ODataDate(fromUtc)}&endDateTime={ODataDate(toUtc)}&$select={Select}&$top=100";
        var count = 0;
        await foreach (var ev in Graph.GetAllAsync(url, cancellationToken, PreferUtc))
        {
            if (GraphMapping.MapCalendarEvent(ev, internalDomains) is not { } activity)
                continue;
            await Sink.WriteAsync(activity, cancellationToken);
            count++;
        }
        return count;
    }
}
