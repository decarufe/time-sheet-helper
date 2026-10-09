using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.Collectors.Microsoft365;

namespace TimeSheetHelper.Tests.Microsoft365;

public class Microsoft365CollectorTests
{
    private static readonly DateTime From = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static string? Route(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var query = Uri.UnescapeDataString(uri.Query);
        return path switch
        {
            "/v1.0/me" => "me.json",
            "/v1.0/me/chats" when !query.Contains("skiptoken") => "chats.json",
            "/v1.0/chats/19:a@unq.gbl.spaces/messages" when query.Contains("skiptoken=page2") => "chatA-messages-page2.json",
            "/v1.0/chats/19:a@unq.gbl.spaces/messages" when !query.Contains("skiptoken") => "chatA-messages-page1.json",
            "/v1.0/chats/19:a@unq.gbl.spaces/members" => "chatA-members.json",
            "/v1.0/chats/19:b@thread.v2/messages" => "chatB-messages.json",
            "/v1.0/me/calendarView" => "calendarView.json",
            "/v1.0/me/mailFolders/sentitems/messages" => "sentitems.json",
            _ => null,
        };
    }

    private static TeamsCollector Teams(FakeGraphHandler handler, ListSink sink, int maxChats = 50) =>
        new(GraphTestClient.Client(handler), new FakeTokenProvider(), sink,
            new StaticOptionsMonitor<Microsoft365Options>(new() { TenantId = "t", ClientId = "c", MaxChats = maxChats }),
            NullLogger<TeamsCollector>.Instance);

    [Fact]
    public async Task Teams_stops_paging_at_messages_older_than_range()
    {
        var handler = new FakeGraphHandler(Route);
        var sink = new ListSink();

        var result = await Teams(handler, sink).SyncAsync(From, To);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.EventCount);
        Assert.Equal(["19:a@unq.gbl.spaces/1728290000000", "19:a@unq.gbl.spaces/1728100000000"], sink.Items.Select(i => i.ExternalId));
        Assert.All(sink.Items, i => Assert.Equal("Alice Martin", i.Subject));

        var urls = handler.Requests.Select(u => Uri.UnescapeDataString(u.ToString())).ToList();
        Assert.DoesNotContain(urls, u => u.Contains("skiptoken=page3"));       // message paging stopped
        Assert.DoesNotContain(urls, u => u.Contains("skiptoken=chats-page2")); // chat paging stopped
        Assert.DoesNotContain(urls, u => u.Contains("19:old@thread.v2"));      // quiet chat never scanned
        Assert.DoesNotContain(urls, u => u.Contains("19:empty@thread.v2"));
        Assert.DoesNotContain(urls, u => u.Contains("19:b@thread.v2/members")); // no message of mine, no member lookup
    }

    [Fact]
    public async Task Teams_respects_chat_cap()
    {
        var handler = new FakeGraphHandler(Route);
        var result = await Teams(handler, new ListSink(), maxChats: 1).SyncAsync(From, To);

        Assert.Equal(2, result.EventCount);
        Assert.Single(result.Warnings);
        Assert.DoesNotContain(handler.Requests, u => Uri.UnescapeDataString(u.ToString()).Contains("19:b@thread.v2"));
    }

    [Fact]
    public async Task Calendar_and_email_sync_write_filtered_events()
    {
        var handler = new FakeGraphHandler(Route);
        var sink = new ListSink();
        var options = new StaticOptionsMonitor<Microsoft365Options>(new() { TenantId = "t", ClientId = "c" });

        var calendar = await new CalendarCollector(GraphTestClient.Client(handler), new FakeTokenProvider(), sink, options,
            NullLogger<CalendarCollector>.Instance).SyncAsync(From, To);
        var email = await new EmailCollector(GraphTestClient.Client(handler), new FakeTokenProvider(), sink, options,
            NullLogger<EmailCollector>.Instance).SyncAsync(From, To);

        Assert.Equal(2, calendar.EventCount);
        Assert.Equal(2, email.EventCount);
        Assert.Equal(4, sink.Items.Count);
        Assert.Contains(handler.Requests, u => u.Query.Contains("startDateTime=2026-10-05T00:00:00Z"));
    }

    [Fact]
    public async Task Status_reports_missing_registration_then_sign_in()
    {
        var handler = new FakeGraphHandler(Route);
        var options = new StaticOptionsMonitor<Microsoft365Options>(new());

        var notConfigured = new EmailCollector(GraphTestClient.Client(handler), new FakeTokenProvider(configured: false), new ListSink(),
            options, NullLogger<EmailCollector>.Instance);
        var signedOut = new FakeTokenProvider(signedIn: false);
        var notSignedIn = new EmailCollector(GraphTestClient.Client(handler, signedOut), signedOut, new ListSink(),
            options, NullLogger<EmailCollector>.Instance);

        var status = await notConfigured.GetStatusAsync();
        Assert.False(status.IsConfigured);
        Assert.Equal("Inscription d'application Microsoft 365 manquante", status.Message);

        Assert.False((await notSignedIn.GetStatusAsync()).IsConfigured);
        var result = await notSignedIn.SyncAsync(From, To);
        Assert.Equal(0, result.EventCount);
        Assert.Equal(["Connexion Microsoft 365 requise"], result.Warnings);
        Assert.Empty(handler.Requests);
    }
}
