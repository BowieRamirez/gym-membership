using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

static class Fmt
{
    static DateTime Local(DateTime d) => (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d).ToLocalTime();

    /// <summary>"Mon 12 Oct, 6:00 AM to 2:00 PM"</summary>
    public static string Range(DateTime start, DateTime end) => $"{Local(start):ddd d MMM, h:mm tt} to {Local(end):h:mm tt}";
}

// ===================== Messenger =====================
public sealed record ChatBubble(ChatLine Line, bool Mine)
{
    /// <summary>Shown under what you sent: Sent, Delivered or Read. Empty for what you received.</summary>
    public string Status => !Mine ? "" : Line.ReadAt is not null ? "Read" : Line.DeliveredAt is not null ? "Delivered" : "Sent";
}

/// <summary>One page of a conversation, oldest first. HasMore says an earlier page exists.</summary>
public sealed record ChatPage(IReadOnlyList<ChatBubble> Bubbles, bool HasMore);

public interface IChatService
{
    /// <summary>Set before navigating to Messages so that conversation opens first. MemberId null means "me, as the member".</summary>
    (string CoachId, string? MemberId)? Pending { get; set; }
    Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync();
    /// <summary>A page of at most 99 messages. beforeId is the id of the oldest message already on screen.</summary>
    Task<AppResult<ChatPage>> MessagesAsync(ChatThread thread, long? beforeId = null, int limit = 50);
    Task<AppResult<bool>> SendAsync(ChatThread thread, string body);
    Task<AppResult<bool>> MarkReadAsync(ChatThread thread);
    Task<AppResult<bool>> MarkDeliveredAsync();
    Task<AppResult<bool>> ClaimAsync(ChatThread thread, bool claim);
    Task<AppResult<bool>> SetOpenAsync(ChatThread thread, bool open);
    Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query);
}

public sealed class ChatService(ISupabaseGateway gw, INotificationService notifications) : IChatService
{
    public (string CoachId, string? MemberId)? Pending { get; set; }

    public Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync() => Safe.RunAsync<IReadOnlyList<ChatThread>>(async () =>
        await gw.RpcAsync<List<ChatThread>>("chat_threads") ?? []);

    public Task<AppResult<ChatPage>> MessagesAsync(ChatThread thread, long? beforeId = null, int limit = 50) => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var take = Math.Clamp(limit, 1, 99);
        var args = new Dictionary<string, object> { ["p_kind"] = thread.Kind, ["p_member"] = thread.MemberId, ["p_limit"] = take + 1 };   // one extra row tells whether an earlier page exists
        if (thread.CoachId is not null) args["p_coach"] = thread.CoachId;
        if (beforeId is { } before) args["p_before"] = before;
        var rows = await gw.RpcAsync<List<ChatLine>>("chat_messages", args) ?? [];
        var bubbles = rows.Take(take).Reverse().Select(l => new ChatBubble(l, l.SenderId == uid)).ToList();
        return new ChatPage(bubbles, rows.Count > take);
    });

    public async Task<AppResult<bool>> SendAsync(ChatThread thread, string body)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0) return AppResult<bool>.Fail(AppErrorKind.Validation, "empty_message");
        if (text.Length > 2000) return AppResult<bool>.Fail(AppErrorKind.Validation, "message_too_long");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            if (thread.IsSupport) await gw.RpcAsync("send_support_message", new() { ["p_member"] = thread.MemberId, ["p_body"] = text });
            else await gw.InsertAsync(new ChatMessage { CoachId = thread.CoachId!, MemberId = thread.MemberId, SenderId = uid, Body = text });
            return true;
        });
    }

    public Task<AppResult<bool>> MarkReadAsync(ChatThread thread) => Safe.RunAsync(async () =>
    {
        var args = new Dictionary<string, object> { ["p_kind"] = thread.Kind, ["p_member"] = thread.MemberId };
        if (thread.CoachId is not null) args["p_coach"] = thread.CoachId;
        await gw.RpcAsync("mark_chat_read", args);
        notifications.Refresh();   // the alert for this chat is cleared, so the bell can update
        return true;
    });

    public Task<AppResult<bool>> MarkDeliveredAsync() => Safe.RunAsync(async () => { await gw.RpcAsync("mark_chats_delivered"); return true; });

    public Task<AppResult<bool>> ClaimAsync(ChatThread thread, bool claim) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("claim_support_thread", new() { ["p_member"] = thread.MemberId, ["p_claim"] = claim }); return true; });

    public Task<AppResult<bool>> SetOpenAsync(ChatThread thread, bool open) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("set_support_status", new() { ["p_member"] = thread.MemberId, ["p_open"] = open }); return true; });

    public Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query) => Safe.RunAsync<IReadOnlyList<ChatSearchHit>>(async () =>
        string.IsNullOrWhiteSpace(query) ? [] : await gw.RpcAsync<List<ChatSearchHit>>("chat_search", new() { ["p_query"] = query.Trim() }) ?? []);
}
