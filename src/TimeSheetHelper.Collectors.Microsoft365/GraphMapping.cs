using System.Globalization;
using System.Text.Json;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>The signed-in user, as needed by the mappers.</summary>
internal sealed record GraphMe(string Id, string? Address)
{
    public string? Domain => GraphMapping.DomainOf(Address);
}

internal sealed record ChatInfo(string Id, string? Topic, string? ChatType, string? WebUrl, DateTime? LastMessageUtc);

/// <summary>A chat message sent by the user; the body is never read.</summary>
internal sealed record MyChatMessage(string Id, DateTime CreatedUtc);

/// <summary>Result of filtering one page of chat messages (newest first).</summary>
/// <param name="ReachedStart">True when the page contained a message older than the range: stop paging.</param>
internal sealed record ChatMessagesPage(IReadOnlyList<MyChatMessage> Messages, bool ReachedStart);

/// <summary>Pure Graph JSON to <see cref="ActivityEvent"/> mapping. Only metadata is read, never bodies.</summary>
internal static class GraphMapping
{
    public const string MeetingKind = "meeting";
    public const string SentKind = "sent";
    public const string ChatMessageKind = "chat-message";

    // ---------- Calendar ----------

    /// <summary>
    /// Maps a calendarView event. Returns null for cancelled, declined, all-day and "free" events.
    /// Title carries the response, availability, online flag and categories, e.g. "accepted | busy | online | Client A".
    /// </summary>
    public static ActivityEvent? MapCalendarEvent(JsonElement ev, ISet<string> internalDomains)
    {
        if (GetBool(ev, "isCancelled") || GetBool(ev, "isAllDay"))
            return null;

        var showAs = GetString(ev, "showAs");
        if (string.Equals(showAs, "free", StringComparison.OrdinalIgnoreCase))
            return null;

        var response = ev.TryGetProperty("responseStatus", out var rs) ? GetString(rs, "response") : null;
        if (string.Equals(response, "declined", StringComparison.OrdinalIgnoreCase))
            return null;

        var start = ParseDateTimeTimeZone(ev, "start");
        var end = ParseDateTimeTimeZone(ev, "end");
        if (start is null)
            return null;

        var participants = new List<string>();
        if (ev.TryGetProperty("organizer", out var organizer) && EmailAddressOf(organizer) is { } organizerAddress)
            participants.Add(organizerAddress);
        if (ev.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array)
        {
            foreach (var attendee in attendees.EnumerateArray())
            {
                // Rooms and equipment are not people.
                if (string.Equals(GetString(attendee, "type"), "resource", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (EmailAddressOf(attendee) is { } address)
                    participants.Add(address);
            }
        }
        participants = participants.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var isOnline = GetBool(ev, "isOnlineMeeting");
        var joinUrl = ev.TryGetProperty("onlineMeeting", out var online) && online.ValueKind == JsonValueKind.Object
            ? GetString(online, "joinUrl")
            : null;
        joinUrl ??= GetString(ev, "onlineMeetingUrl");

        var categories = ev.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
            ? cats.EnumerateArray().Select(c => c.GetString()).Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
            : [];

        var titleParts = new List<string>();
        if (!string.IsNullOrEmpty(response) && response != "none")
            titleParts.Add(response);
        if (!string.IsNullOrEmpty(showAs))
            titleParts.Add(showAs);
        if (isOnline || joinUrl is not null)
            titleParts.Add("online");
        if (categories.Count > 0)
            titleParts.Add(string.Join(", ", categories));

        var uid = GetString(ev, "iCalUId") ?? GetString(ev, "id");

        return new ActivityEvent
        {
            Source = ActivitySource.Calendar,
            Kind = MeetingKind,
            StartUtc = start.Value,
            EndUtc = end,
            Subject = GetString(ev, "subject"),
            Title = titleParts.Count > 0 ? string.Join(" | ", titleParts) : null,
            Participants = JoinParticipants(participants),
            Url = joinUrl,
            Project = DominantExternalDomain(participants, internalDomains),
            ExternalId = $"{uid}|{start.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}",
        };
    }

    // ---------- Email ----------

    /// <summary>Maps a sent message (metadata only). Project is the dominant external recipient domain.</summary>
    public static ActivityEvent? MapSentMessage(JsonElement message, ISet<string> internalDomains)
    {
        if (GetBool(message, "isDraft"))
            return null;
        var sent = ParseUtc(GetString(message, "sentDateTime"));
        if (sent is null)
            return null;

        var recipients = new List<string>();
        foreach (var property in (string[])["toRecipients", "ccRecipients"])
        {
            if (message.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var recipient in list.EnumerateArray())
                {
                    if (EmailAddressOf(recipient) is { } address)
                        recipients.Add(address);
                }
            }
        }
        recipients = recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ActivityEvent
        {
            Source = ActivitySource.Email,
            Kind = SentKind,
            StartUtc = sent.Value,
            Subject = GetString(message, "subject"),
            Participants = JoinParticipants(recipients),
            Project = DominantExternalDomain(recipients, internalDomains),
            Url = GetString(message, "webLink"),
            ExternalId = GetString(message, "internetMessageId") ?? GetString(message, "id"),
        };
    }

    // ---------- Teams ----------

    public static ChatInfo ParseChat(JsonElement chat)
    {
        DateTime? lastMessage = null;
        if (chat.TryGetProperty("lastMessagePreview", out var preview) && preview.ValueKind == JsonValueKind.Object)
            lastMessage = ParseUtc(GetString(preview, "createdDateTime"));

        return new ChatInfo(
            GetString(chat, "id") ?? "",
            GetString(chat, "topic"),
            GetString(chat, "chatType"),
            GetString(chat, "webUrl"),
            lastMessage);
    }

    /// <summary>Display names of the chat members other than the user, from a "members" array.</summary>
    public static IReadOnlyList<string> OtherMemberNames(IEnumerable<JsonElement> members, string meId) =>
        members
            .Where(m => !string.Equals(GetString(m, "userId"), meId, StringComparison.OrdinalIgnoreCase))
            .Select(m => GetString(m, "displayName") ?? GetString(m, "email"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Keeps the user's own, non-deleted, regular messages in [fromUtc, toUtc) from a page ordered newest first.
    /// <see cref="ChatMessagesPage.ReachedStart"/> tells the caller to stop paging.
    /// </summary>
    public static ChatMessagesPage FilterMyChatMessages(IEnumerable<JsonElement> messages, string meId, DateTime fromUtc, DateTime toUtc)
    {
        var mine = new List<MyChatMessage>();
        var reachedStart = false;
        foreach (var message in messages)
        {
            var created = ParseUtc(GetString(message, "createdDateTime"));
            if (created is null)
                continue;
            if (created.Value < fromUtc)
            {
                reachedStart = true;
                continue;
            }
            if (created.Value >= toUtc)
                continue;
            if (!string.Equals(GetString(message, "messageType"), "message", StringComparison.OrdinalIgnoreCase))
                continue;
            if (message.TryGetProperty("deletedDateTime", out var deleted) && deleted.ValueKind == JsonValueKind.String)
                continue;
            if (!IsFromUser(message, meId))
                continue;

            mine.Add(new MyChatMessage(GetString(message, "id") ?? "", created.Value));
        }
        return new ChatMessagesPage(mine, reachedStart);
    }

    public static ActivityEvent MapChatMessage(MyChatMessage message, ChatInfo chat, IReadOnlyList<string> otherMembers) => new()
    {
        Source = ActivitySource.Teams,
        Kind = ChatMessageKind,
        StartUtc = message.CreatedUtc,
        Subject = !string.IsNullOrWhiteSpace(chat.Topic) ? chat.Topic : (otherMembers.Count > 0 ? string.Join(", ", otherMembers) : null),
        Participants = JoinParticipants(otherMembers),
        Title = chat.ChatType,
        Url = chat.WebUrl,
        ExternalId = $"{chat.Id}/{message.Id}",
    };

    private static bool IsFromUser(JsonElement message, string meId) =>
        message.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
        && from.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
        && string.Equals(GetString(user, "id"), meId, StringComparison.OrdinalIgnoreCase);

    // ---------- Helpers ----------

    public static GraphMe ParseMe(JsonElement me) =>
        new(GetString(me, "id") ?? "", GetString(me, "mail") ?? GetString(me, "userPrincipalName"));

    /// <summary>Most frequent recipient domain that is not internal; ties go to the first seen. Null if none.</summary>
    public static string? DominantExternalDomain(IEnumerable<string> addresses, ISet<string> internalDomains) =>
        addresses
            .Select(DomainOf)
            .Where(d => d is not null && !internalDomains.Contains(d))
            .Select((d, index) => (Domain: d!, index))
            .GroupBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(x => x.index))
            .Select(g => g.Key)
            .FirstOrDefault();

    public static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..].Trim().ToLowerInvariant() : null;
    }

    public static DateTime? ParseUtc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    /// <summary>Parses a Graph dateTimeTimeZone ({ dateTime, timeZone }) into UTC.</summary>
    public static DateTime? ParseDateTimeTimeZone(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        var dateTime = GetString(value, "dateTime");
        if (!DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        var zone = GetString(value, "timeZone");
        if (string.IsNullOrEmpty(zone) || zone.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            || zone.Equals("Etc/GMT", StringComparison.OrdinalIgnoreCase))
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // We request UTC via the Prefer header, so this should not happen; assume UTC.
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        }
    }

    private static string? EmailAddressOf(JsonElement recipient) =>
        recipient.TryGetProperty("emailAddress", out var email) && email.ValueKind == JsonValueKind.Object
            ? NullIfEmpty(GetString(email, "address"))
            : null;

    private static string? JoinParticipants(IReadOnlyCollection<string> participants) =>
        participants.Count > 0 ? string.Join(";", participants) : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
