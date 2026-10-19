using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>
/// Teams chat messages sent by the user (metadata only, never the body). Chats are scanned most recently active
/// first, up to <see cref="Microsoft365Options.MaxChats"/>; messages are paged newest first until older than the range.
/// </summary>
internal sealed class TeamsCollector(
    GraphClient graph,
    IGraphAccessTokenProvider tokens,
    IActivitySink sink,
    IOptionsMonitor<Microsoft365Options> options,
    ILogger<TeamsCollector> logger) : Microsoft365CollectorBase(graph, tokens, sink, options, logger)
{
    // Sorted by last message so scanning can stop at the first chat that has been quiet since the range start.
    internal const string SortedChatsUrl = "me/chats?$expand=lastMessagePreview&$orderby=lastMessagePreview/createdDateTime desc&$top=50";

    // Fallback when the sorted query is refused: unsorted, members inline, capped by MaxChats.
    internal const string ChatsWithMembersUrl = "me/chats?$expand=members&$top=50";

    public override string Name => "teams";

    protected override async Task<int> SyncCoreAsync(DateTime fromUtc, DateTime toUtc, List<string> warnings, CancellationToken cancellationToken)
    {
        var me = await GetMeAsync(cancellationToken);
        var maxChats = Math.Max(1, Options.MaxChats);

        List<(ChatInfo Chat, IReadOnlyList<JsonElement>? Members)> chats;
        try
        {
            chats = await ListChatsAsync(SortedChatsUrl, fromUtc, maxChats, sorted: true, cancellationToken);
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            Logger.LogInformation(ex, "Sorted chat listing refused, falling back to unsorted listing");
            chats = await ListChatsAsync(ChatsWithMembersUrl, fromUtc, maxChats, sorted: false, cancellationToken);
        }

        var count = 0;
        var forbidden = 0;
        string? lastForbiddenCode = null;
        foreach (var (chat, inlineMembers) in chats)
        {
            try
            {
                count += await SyncChatAsync(chat, inlineMembers, me, fromUtc, toUtc, cancellationToken);
            }
            catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // Only the status and Graph's error code are logged: never a token nor the content of a message.
                Logger.LogWarning("Teams chat messages refused: HTTP {Status}, Graph error code {ErrorCode}", (int)ex.StatusCode, ex.ErrorCode ?? "(none)");
                if (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    forbidden++;
                    lastForbiddenCode = ex.ErrorCode;
                }
                warnings.Add(ex.StatusCode == HttpStatusCode.Forbidden
                    ? $"Conversation Teams ignorée ({chat.Topic ?? chat.Id}) : accès refusé ({ex.ErrorCode ?? "Forbidden"})"
                    : $"Conversation Teams ignorée ({chat.Topic ?? chat.Id}) : introuvable");
            }
        }

        if (forbidden > 0 && forbidden == chats.Count)
        {
            // Every chat refused: one actionable message instead of one line per chat.
            warnings.Clear();
            warnings.Add(PermissionMessage(lastForbiddenCode));
        }

        if (chats.Count >= maxChats)
            warnings.Add($"Limite de {maxChats} conversations Teams atteinte; les plus anciennes n'ont pas été analysées.");

        return count;
    }

    internal static string PermissionMessage(string? errorCode) =>
        $"Teams : accès refusé à toutes les conversations (code Graph : {errorCode ?? "Forbidden"}). "
        + "La permission déléguée Chat.Read est manquante ou n'a pas reçu le consentement : ajoutez-la dans Entra (consentement administrateur si exigé), "
        + "puis reconnectez-vous à Microsoft 365. Si l'erreur persiste, une politique Teams du locataire peut bloquer l'accès.";

    private async Task<List<(ChatInfo, IReadOnlyList<JsonElement>?)>> ListChatsAsync(
        string url, DateTime fromUtc, int maxChats, bool sorted, CancellationToken cancellationToken)
    {
        var chats = new List<(ChatInfo, IReadOnlyList<JsonElement>?)>();
        await foreach (var element in Graph.GetAllAsync(url, cancellationToken))
        {
            var chat = GraphMapping.ParseChat(element);
            if (sorted)
            {
                if (chat.LastMessageUtc is null)
                    continue;
                if (chat.LastMessageUtc < fromUtc)
                    break; // Every following chat is quieter still.
            }

            IReadOnlyList<JsonElement>? members = element.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array
                ? [.. m.EnumerateArray()]
                : null;
            chats.Add((chat, members));
            if (chats.Count >= maxChats)
                break;
        }
        return chats;
    }

    private async Task<int> SyncChatAsync(
        ChatInfo chat, IReadOnlyList<JsonElement>? inlineMembers, GraphMe me, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var mine = new List<MyChatMessage>();
        var url = $"chats/{chat.Id}/messages?$top=50&$orderby=createdDateTime desc";
        await foreach (var page in Graph.GetPagesAsync(url, cancellationToken))
        {
            var result = GraphMapping.FilterMyChatMessages(page, me.Id, fromUtc, toUtc);
            mine.AddRange(result.Messages);
            if (result.ReachedStart)
                break;
        }

        if (mine.Count == 0)
            return 0;

        var members = inlineMembers ?? await ListMembersAsync(chat.Id, cancellationToken);
        var others = GraphMapping.OtherMemberNames(members, me.Id);
        foreach (var message in mine)
            await Sink.WriteAsync(GraphMapping.MapChatMessage(message, chat, others), cancellationToken);
        return mine.Count;
    }

    private async Task<IReadOnlyList<JsonElement>> ListMembersAsync(string chatId, CancellationToken cancellationToken)
    {
        var members = new List<JsonElement>();
        await foreach (var member in Graph.GetAllAsync($"chats/{chatId}/members", cancellationToken))
            members.Add(member);
        return members;
    }
}
