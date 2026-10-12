using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

static class Fmt
{
    static DateTime Local(DateTime d) => (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d).ToLocalTime();

    /// <summary>"Mon 12 Oct, 6:00 AM to 2:00 PM"</summary>
    public static string Range(DateTime start, DateTime end) => $"{Local(start):ddd d MMM, h:mm tt} to {Local(end):h:mm tt}";
}

// ===================== Messages (member and coach) =====================
// A conversation is one coach and one member, so it is named by their two Auth ids.
public sealed record ThreadRow(string CoachId, string MemberId, string With, bool CanSend);
public sealed record MessageRow(ChatMessage Message, bool Mine);

public interface IMessageService
{
    /// <summary>Set before navigating to Messages so that conversation opens first. MemberId null means "me, as the member".</summary>
    (string CoachId, string? MemberId)? Pending { get; set; }
    Task<AppResult<IReadOnlyList<ThreadRow>>> ThreadsAsync();
    Task<AppResult<IReadOnlyList<MessageRow>>> MessagesAsync(string coachId, string memberId);
    Task<AppResult<bool>> SendAsync(string coachId, string memberId, string body);
}

public sealed class MessageService(ISupabaseGateway gw) : IMessageService
{
    public (string CoachId, string? MemberId)? Pending { get; set; }

    public Task<AppResult<IReadOnlyList<ThreadRow>>> ThreadsAsync() => Safe.RunAsync<IReadOnlyList<ThreadRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var names = await Directory_.UserNamesAsync(gw);
        var hires = await gw.ListAsync<CoachHire>();
        var past = await gw.ListAsync<ChatMessage>();
        var active = hires.Where(h => h.Status == Status.Active).Select(h => (h.CoachId, h.MemberId)).ToHashSet();

        // a conversation exists for every hire (current or ended) and for anyone already written to
        var pairs = hires.Select(h => (h.CoachId, h.MemberId)).Union(past.Select(m => (m.CoachId, m.MemberId)));
        return pairs.Select(p => new ThreadRow(p.CoachId, p.MemberId, names.GetValueOrDefault(p.CoachId == uid ? p.MemberId : p.CoachId, "Unknown"), active.Contains(p)))
            .OrderBy(t => t.With).ToList();
    });

    public Task<AppResult<IReadOnlyList<MessageRow>>> MessagesAsync(string coachId, string memberId) => Safe.RunAsync<IReadOnlyList<MessageRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        return (await gw.ListAsync<ChatMessage>(m => m.CoachId == coachId && m.MemberId == memberId))
            .OrderBy(m => m.SentAt).Select(m => new MessageRow(m, m.SenderId == uid)).ToList();
    });

    public async Task<AppResult<bool>> SendAsync(string coachId, string memberId, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return AppResult<bool>.Fail(AppErrorKind.Validation, "empty_message");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            await gw.InsertAsync(new ChatMessage { CoachId = coachId, MemberId = memberId, SenderId = uid, Body = body.Trim() });
            return true;
        });
    }
}
