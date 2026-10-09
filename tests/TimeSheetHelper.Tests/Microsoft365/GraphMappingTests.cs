using TimeSheetHelper.Collectors.Microsoft365;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests.Microsoft365;

public class GraphMappingTests
{
    private static readonly HashSet<string> Internal = new(StringComparer.OrdinalIgnoreCase) { "contoso.com" };

    private static readonly DateTime From = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static List<ActivityEvent> CalendarEvents() =>
        GraphFixtures.Values("calendarView.json")
            .Select(e => GraphMapping.MapCalendarEvent(e, Internal))
            .OfType<ActivityEvent>()
            .ToList();

    [Fact]
    public void Calendar_skips_declined_free_all_day_and_cancelled()
    {
        var subjects = CalendarEvents().Select(e => e.Subject).ToList();
        Assert.Equal(["Revue sprint Fabrikam", "Planification interne"], subjects);
    }

    [Fact]
    public void Calendar_maps_times_participants_and_metadata()
    {
        var meeting = CalendarEvents()[0];

        Assert.Equal(ActivitySource.Calendar, meeting.Source);
        Assert.Equal("meeting", meeting.Kind);
        Assert.Equal(new DateTime(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc), meeting.StartUtc);
        Assert.Equal(DateTimeKind.Utc, meeting.StartUtc.Kind);
        Assert.Equal(TimeSpan.FromHours(1), meeting.Duration);
        // Organizer first, case-insensitive dedupe, rooms excluded.
        Assert.Equal("alice@fabrikam.com;eric@contoso.com;bob@fabrikam.com", meeting.Participants);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/abc", meeting.Url);
        Assert.Equal("accepted | busy | online | Client Fabrikam, Facturable", meeting.Title);
        Assert.Equal("fabrikam.com", meeting.Project);
        Assert.Equal("040000008200E00074C5B7101A82E00800000000ACCEPTED|2026-10-06T14:00:00Z", meeting.ExternalId);
    }

    [Fact]
    public void Calendar_internal_meeting_has_no_project_or_url()
    {
        var meeting = CalendarEvents()[1];
        Assert.Null(meeting.Project);
        Assert.Null(meeting.Url);
        Assert.Equal("organizer | tentative", meeting.Title);
    }

    [Fact]
    public void Calendar_converts_non_utc_time_zone()
    {
        var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            """{"start":{"dateTime":"2026-10-06T10:00:00.0000000","timeZone":"Eastern Standard Time"}}""");
        Assert.Equal(new DateTime(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc), GraphMapping.ParseDateTimeTimeZone(json, "start"));
    }

    [Fact]
    public void Email_maps_recipients_and_dominant_external_domain()
    {
        var mails = GraphFixtures.Values("sentitems.json")
            .Select(m => GraphMapping.MapSentMessage(m, Internal))
            .OfType<ActivityEvent>()
            .ToList();

        Assert.Equal(2, mails.Count);
        var first = mails[0];
        Assert.Equal(ActivitySource.Email, first.Source);
        Assert.Equal("sent", first.Kind);
        Assert.Equal(new DateTime(2026, 10, 6, 18, 42, 10, DateTimeKind.Utc), first.StartUtc);
        Assert.Null(first.EndUtc);
        Assert.Equal("RE: Estimation phase 2", first.Subject);
        Assert.Equal("alice@fabrikam.com;bob@fabrikam.com;dan@northwind.com;carl@contoso.com", first.Participants);
        Assert.Equal("fabrikam.com", first.Project);
        Assert.Equal("<MSG1@contoso.com>", first.ExternalId);

        // A sub-domain of the user's domain is still external unless listed in InternalDomains.
        Assert.Equal("contoso-labs.com", mails[1].Project);
    }

    [Fact]
    public void Dominant_domain_ignores_internal_and_breaks_ties_by_order()
    {
        Assert.Null(GraphMapping.DominantExternalDomain(["a@contoso.com", "b@CONTOSO.com"], Internal));
        Assert.Equal("northwind.com", GraphMapping.DominantExternalDomain(["a@northwind.com", "b@fabrikam.com"], Internal));
    }

    [Fact]
    public void Teams_keeps_only_my_regular_messages_in_range()
    {
        var page1 = GraphMapping.FilterMyChatMessages(GraphFixtures.Values("chatA-messages-page1.json"), "me-123", From, To);
        Assert.False(page1.ReachedStart);
        Assert.Equal(["1728290000000"], page1.Messages.Select(m => m.Id));

        var page2 = GraphMapping.FilterMyChatMessages(GraphFixtures.Values("chatA-messages-page2.json"), "me-123", From, To);
        Assert.True(page2.ReachedStart);
        Assert.Equal(["1728100000000"], page2.Messages.Select(m => m.Id)); // deleted and older ones dropped
    }

    [Fact]
    public void Teams_messages_from_others_or_apps_are_excluded()
    {
        var page = GraphMapping.FilterMyChatMessages(GraphFixtures.Values("chatB-messages.json"), "me-123", From, To);
        Assert.Empty(page.Messages);
        Assert.False(page.ReachedStart);
    }

    [Fact]
    public void Teams_message_uses_member_names_when_chat_has_no_topic()
    {
        var chat = GraphMapping.ParseChat(GraphFixtures.Values("chats.json")[0]);
        var others = GraphMapping.OtherMemberNames(GraphFixtures.Values("chatA-members.json"), "me-123");
        var activity = GraphMapping.MapChatMessage(new MyChatMessage("42", From.AddHours(9)), chat, others);

        Assert.Equal(ActivitySource.Teams, activity.Source);
        Assert.Equal("chat-message", activity.Kind);
        Assert.Equal("Alice Martin", activity.Subject);
        Assert.Equal("Alice Martin", activity.Participants);
        Assert.Equal("19:a@unq.gbl.spaces/42", activity.ExternalId);
        Assert.Equal(new DateTime(2026, 10, 8, 15, 0, 0, DateTimeKind.Utc), chat.LastMessageUtc);
    }
}
