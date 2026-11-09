using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

// 0015-0017 and 0051 in memory: coach chats; gym threads per person and queue (Front desk, Admin, System admin) for members and
// coaches, answered by the roles of that queue; read-only Announcements; and (F11) a chat message for every step of a time request.
public sealed partial class DemoGateway
{
    sealed class DemoAnnouncement { public long Id; public string? Author; public string Audience = "everyone"; public string? Recipient; public string Body = ""; public DateTime Posted; }
    readonly List<DemoAnnouncement> _announcements = new();
    readonly Dictionary<string, long> _announcementReads = new();

    bool CanAnswer(string? queue) => IsActiveUser && queue switch
    {
        ChatQueue.FrontDesk => Has("support:reply"),
        ChatQueue.Admin => RolesOf(CurrentUserId).Any(r => r is "admin" or "subadmin"),
        ChatQueue.SystemAdmin => RolesOf(CurrentUserId).Contains("admin"),
        _ => false
    };
    bool Writer(string? user) => All<Member>().Any(m => m.UserId == user) || All<Coach>().Any(c => c.UserId == user);
    static string QueueOf(Dictionary<string, object> a) => a.TryGetValue("p_queue", out var q) && q is string s ? s : ChatQueue.FrontDesk;
    static string Folder(string queue, string person) => queue == ChatQueue.FrontDesk ? $"support/{person}" : $"support/{queue}/{person}";

    IEnumerable<DemoAnnouncement> MyAnnouncements(string uid) => _announcements.Where(x => x.Recipient == uid || (x.Recipient is null &&
        ((x.Audience is "everyone" or "members" && All<Member>().Any(m => m.UserId == uid)) || (x.Audience is "everyone" or "coaches" && All<Coach>().Any(c => c.UserId == uid)))));

    // F11: what each change of a time request says in the chat, in the name of whoever made it (0051 post_request_message)
    void PostRequestMessages(Dictionary<int, TimeRequest> before)
    {
        if (CurrentUserId is not { } actor) return;
        string Words(DateTime s, DateTime e, int? amenity) =>
            $"{s.AddHours(8):ddd d MMM, h:mm tt} to {e.AddHours(8):h:mm tt}" + (All<Amenity>().FirstOrDefault(x => x.Id == amenity) is { } am ? $", at {am.Name}" : "");
        foreach (var r in All<TimeRequest>().Where(r => r.MemberId is not null && (actor == r.CoachId || actor == r.MemberId)).ToList())
        {
            before.TryGetValue(r.Id, out var old);
            if (old is not null && old.Status == r.Status && old.Version == r.Version) continue;
            var note = string.IsNullOrWhiteSpace(r.Message) ? "" : $" Note: {r.Message.Trim()}";
            var body = old is null
                ? (r.RequestedBy == "member" ? "I would like to request a training session: " : "I would like to invite you to a training session: ") + Words(r.RequestedStart, r.RequestedEnd, r.AmenityId) + "." + note
                : r.Status switch
                {
                    "approved" when old.Status == "proposed" => $"I accepted the new time: {Words(old.ProposedStart!.Value, old.ProposedEnd!.Value, old.ProposedAmenityId)}.",
                    "approved" => $"I accepted the training session: {Words(r.RequestedStart, r.RequestedEnd, r.AmenityId)}.",
                    "rejected" => $"I declined the training session on {Words(r.RequestedStart, r.RequestedEnd, r.AmenityId)}." + (string.IsNullOrWhiteSpace(r.DeclineReason) ? "" : $" Reason: {r.DeclineReason.Trim()}"),
                    "proposed" => $"Could we do another time? {Words(r.ProposedStart!.Value, r.ProposedEnd!.Value, r.ProposedAmenityId)}." + (string.IsNullOrWhiteSpace(r.ProposalReason) ? "" : $" Reason: {r.ProposalReason.Trim()}"),
                    "pending" => $"Could we do this time instead? {Words(r.RequestedStart, r.RequestedEnd, r.AmenityId)}.",
                    "cancelled" => $"I cancelled the training session request for {Words(r.RequestedStart, r.RequestedEnd, r.AmenityId)}.",
                    _ => null
                };
            if (body is not null)
                Add(new ChatMessage { CoachId = r.CoachId, MemberId = r.MemberId!, SenderId = actor, Body = body, SentAt = SubscriptionNow, Kind = "request", TimeRequestId = r.Id });
        }
    }

    bool MessengerCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        var now = SubscriptionNow;
        string S(string k) => (string)a[k];
        bool B(string k) => Convert.ToBoolean(a[k]);
        string? Opt(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        string NameOf(string id) => All<Profile>().FirstOrDefault(p => p.Id == id)?.FullName ?? "Unknown";
        switch (fn)
        {
            case "chat_threads":
            {
                RequireActive();
                var uid = Uid;
                var rows = new List<ChatThread>();
                var pairs = All<CoachHire>().Select(h => (h.CoachId, h.MemberId)).Union(All<ChatMessage>().Select(m => (m.CoachId, m.MemberId)))
                    .Where(p => p.CoachId == uid || p.MemberId == uid).ToList();
                foreach (var (coachId, memberId) in pairs)
                {
                    var withId = coachId == uid ? memberId : coachId;
                    var msgs = All<ChatMessage>().Where(m => m.CoachId == coachId && m.MemberId == memberId).OrderBy(m => m.Id).ToList();
                    var last = msgs.LastOrDefault();
                    rows.Add(new ChatThread
                    {
                        Kind = "coach", CoachId = coachId, MemberId = memberId, WithId = withId, WithName = NameOf(withId),
                        WithAvatar = All<Profile>().FirstOrDefault(p => p.Id == withId)?.AvatarPath,
                        LastBody = last is null ? null : Preview(last.Body, last.AttachmentPath), LastAt = last?.SentAt, LastFromMe = last?.SenderId == uid,
                        Unread = msgs.Count(m => m.SenderId != uid && m.ReadAt is null),
                        CanSend = CoachesMember(coachId, memberId) && ActiveMember(memberId)
                    });
                }
                if (Writer(uid))
                {
                    // your own gym threads: Front desk always, Admin and System admin once started
                    foreach (var q in ChatQueue.All.Where(q => q == ChatQueue.FrontDesk || All<SupportThread>().Any(t => t.MemberId == uid && t.Queue == q)))
                    {
                        var msgs = All<SupportMessage>().Where(m => m.MemberId == uid && m.Queue == q).OrderBy(m => m.Id).ToList();
                        var last = msgs.LastOrDefault();
                        rows.Add(new ChatThread
                        {
                            Kind = "support", MemberId = uid, Queue = q, WithName = ChatQueue.Name(q),
                            LastBody = last is null ? null : Preview(last.Body, last.AttachmentPath), LastAt = last?.SentAt, LastFromMe = last?.SenderId == uid,
                            Unread = msgs.Count(m => m.SenderId != uid && m.ReadAt is null), CanSend = true,
                            Status = All<SupportThread>().FirstOrDefault(t => t.MemberId == uid && t.Queue == q)?.Status ?? "open"
                        });
                    }
                    var mine = MyAnnouncements(uid).ToList();
                    var newest = mine.MaxBy(x => x.Id);
                    rows.Add(new ChatThread
                    {
                        Kind = "announcements", MemberId = uid, WithName = "Announcements", LastBody = newest?.Body, LastAt = newest?.Posted,
                        Unread = mine.Count(x => x.Id > _announcementReads.GetValueOrDefault(uid)), CanSend = false
                    });
                }
                // the inbox: threads in the queues you answer
                foreach (var t in All<SupportThread>().Where(t => t.MemberId != uid && CanAnswer(t.Queue)).OrderByDescending(t => t.LastMessageAt).Take(500))
                {
                    var msgs = All<SupportMessage>().Where(m => m.MemberId == t.MemberId && m.Queue == t.Queue).OrderBy(m => m.Id).ToList();
                    var last = msgs.LastOrDefault();
                    rows.Add(new ChatThread
                    {
                        Kind = "support", MemberId = t.MemberId, Queue = t.Queue, WithId = t.MemberId, WithName = NameOf(t.MemberId),
                        WithAvatar = All<Profile>().FirstOrDefault(p => p.Id == t.MemberId)?.AvatarPath,
                        LastBody = last is null ? null : Preview(last.Body, last.AttachmentPath), LastAt = last?.SentAt, LastFromMe = last is not null && last.SenderId != t.MemberId,
                        Unread = msgs.Count(m => m.SenderId == t.MemberId && m.ReadAt is null), CanSend = true,
                        Status = t.Status, ClaimedBy = t.ClaimedBy, ClaimedByName = t.ClaimedBy is null ? null : NameOf(t.ClaimedBy)
                    });
                }
                result = rows.OrderByDescending(r => r.LastAt ?? DateTime.MinValue).ToList();
                return true;
            }
            case "chat_messages":
            {
                RequireActive();
                var uid = Uid; var kind = S("p_kind"); var member = Opt("p_member"); var coach = Opt("p_coach"); var queue = QueueOf(a);
                long? before = a.TryGetValue("p_before", out var bv) && bv is not null ? Convert.ToInt64(bv) : null;
                var limit = Math.Clamp(a.TryGetValue("p_limit", out var lv) && lv is not null ? Convert.ToInt32(lv) : 50, 1, 100);
                if (kind == "coach")
                {
                    if (uid != coach && uid != member) throw Err("forbidden");
                    result = All<ChatMessage>().Where(m => m.CoachId == coach && m.MemberId == member && (before is null || m.Id < before)).OrderByDescending(m => m.Id).Take(limit)
                        .Select(m => new ChatLine { MessageId = m.Id, SenderId = m.SenderId, Body = m.Body, SentAt = m.SentAt, DeliveredAt = m.DeliveredAt, ReadAt = m.ReadAt,
                            AttachmentPath = m.AttachmentPath, Kind = m.Kind, TimeRequestId = m.TimeRequestId }).ToList();
                }
                else if (kind == "support")
                {
                    if (uid != member && !CanAnswer(queue)) throw Err("forbidden");
                    result = All<SupportMessage>().Where(m => m.MemberId == member && m.Queue == queue && (before is null || m.Id < before)).OrderByDescending(m => m.Id).Take(limit)
                        .Select(m => new ChatLine
                        {
                            MessageId = m.Id, SenderId = m.SenderId, Body = m.Body, SentAt = m.SentAt, DeliveredAt = m.DeliveredAt, ReadAt = m.ReadAt, AttachmentPath = m.AttachmentPath,
                            // the person who wrote in never learns which staff member answered; staff do
                            SenderName = uid == member || m.SenderId == member ? null : All<Profile>().FirstOrDefault(p => p.Id == m.SenderId)?.FullName
                        }).ToList();
                }
                else if (kind == "announcements")
                {
                    if (uid != member) throw Err("forbidden");
                    result = MyAnnouncements(uid).Where(x => before is null || x.Id < before).OrderByDescending(x => x.Id).Take(limit)
                        .Select(x => new ChatLine { MessageId = x.Id, SenderId = x.Author ?? "", Body = x.Body, SentAt = x.Posted, DeliveredAt = x.Posted,
                            SenderName = x.Recipient is null ? "Announcement" : "For you" }).ToList();
                }
                else throw Err("bad_input");
                return true;
            }
            case "mark_chat_read":
            {
                RequireActive();
                var uid = Uid; var kind = S("p_kind"); var member = Opt("p_member"); var coach = Opt("p_coach"); var queue = QueueOf(a);
                if (kind == "coach")
                {
                    if (uid != coach && uid != member) throw Err("forbidden");
                    foreach (var m in All<ChatMessage>().Where(m => m.CoachId == coach && m.MemberId == member && m.SenderId != uid && m.ReadAt is null)) { m.DeliveredAt ??= now; m.ReadAt = now; }
                    ClearChatAlerts(uid, $"c|{coach}|{member}");
                }
                else if (kind == "support")
                {
                    if (uid != member && !CanAnswer(queue)) throw Err("forbidden");
                    foreach (var m in All<SupportMessage>().Where(m => m.MemberId == member && m.Queue == queue && m.ReadAt is null && (uid == member ? m.SenderId != member : m.SenderId == member))) { m.DeliveredAt ??= now; m.ReadAt = now; }
                    ClearChatAlerts(uid, $"s|{member}|{queue}");
                }
                else if (kind == "announcements")
                {
                    if (uid != member) throw Err("forbidden");
                    _announcementReads[uid] = Math.Max(_announcementReads.GetValueOrDefault(uid), MyAnnouncements(uid).Select(x => x.Id).DefaultIfEmpty(0).Max());
                    ClearChatAlerts(uid, "announcements");
                }
                else throw Err("bad_input");
                return true;
            }
            case "mark_chats_delivered":
            {
                RequireActive();
                var uid = Uid;
                foreach (var m in All<ChatMessage>().Where(m => (m.CoachId == uid || m.MemberId == uid) && m.SenderId != uid && m.DeliveredAt is null)) m.DeliveredAt = now;
                foreach (var m in All<SupportMessage>().Where(m => m.DeliveredAt is null && m.SenderId != uid && (m.MemberId == uid || (m.SenderId == m.MemberId && CanAnswer(m.Queue))))) m.DeliveredAt = now;
                return true;
            }
            case "send_support_message":
            {
                RequireActive();
                var uid = Uid; var member = S("p_member"); var queue = QueueOf(a);
                var body = (a["p_body"] as string)?.Trim() ?? "";
                var att = Opt("p_attachment");
                if (!ChatQueue.All.Contains(queue)) throw Err("bad_input");
                if (body == "" && att is null) throw Err("empty_message");
                if (body.Length > 2000) throw Err("message_too_long");
                if (uid == member) { if (!Writer(uid)) throw Err("forbidden"); }
                else if (CanAnswer(queue)) { if (!Writer(member)) throw Err("not_found"); }
                else throw Err("forbidden");
                // the photo has to be in this thread's folder
                if (att is not null && !System.Text.RegularExpressions.Regex.IsMatch(att, $"^{Folder(queue, member)}/{FilePart}$")) throw Err("forbidden");

                var thread = All<SupportThread>().FirstOrDefault(t => t.MemberId == member && t.Queue == queue) ?? Add(new SupportThread { MemberId = member, Queue = queue });
                var msg = Add(new SupportMessage { MemberId = member, Queue = queue, SenderId = uid, Body = body, SentAt = now, AttachmentPath = att });
                thread.LastMessageAt = now;
                var shown = Preview(body, att) ?? "";
                var key = $"s|{member}|{queue}"; var preview = shown.Length > 80 ? shown[..80] : shown;
                if (uid == member)
                {
                    thread.Status = "open";   // writing again reopens it
                    var title = $"New message from {NameOf(uid)}" + (queue == ChatQueue.FrontDesk ? "" : $" to {ChatQueue.Name(queue)}");
                    string[] roles = queue switch { ChatQueue.Admin => ["admin", "subadmin"], ChatQueue.SystemAdmin => ["admin"], _ => ["admin", "subadmin", "employee"] };
                    if (thread.ClaimedBy is { } taker && All<Profile>().Any(p => p.Id == taker && p.IsActive)) NotifyChat(taker, title, preview, key);
                    else foreach (var id in All<UserRole>().Where(u => RolesOf(u.UserId).Any(roles.Contains)).Select(u => u.UserId).Distinct().ToList())
                        if (All<Profile>().Any(p => p.Id == id && p.IsActive)) NotifyChat(id, title, preview, key);
                }
                else NotifyChat(member, queue == ChatQueue.FrontDesk ? "Message from the front desk" : $"Message from {ChatQueue.Name(queue)}", preview, key);
                result = (long)msg.Id;
                return true;
            }
            case "claim_support_thread":
            {
                RequireActive();
                var uid = Uid; var queue = QueueOf(a);
                if (!CanAnswer(queue)) throw Err("forbidden");
                var thread = All<SupportThread>().FirstOrDefault(t => t.MemberId == S("p_member") && t.Queue == queue) ?? throw Err("not_found");
                if (B("p_claim"))
                {
                    if (thread.ClaimedBy is not null && thread.ClaimedBy != uid && All<Profile>().Any(p => p.Id == thread.ClaimedBy && p.IsActive)) throw Err("already_processed");
                    thread.ClaimedBy = uid;
                }
                else
                {
                    if (thread.ClaimedBy is not null && thread.ClaimedBy != uid && !RolesOf(uid).Contains("admin") && All<Profile>().Any(p => p.Id == thread.ClaimedBy && p.IsActive)) throw Err("forbidden");
                    thread.ClaimedBy = null;
                }
                return true;
            }
            case "set_support_status":
            {
                RequireActive();
                var queue = QueueOf(a);
                if (!CanAnswer(queue)) throw Err("forbidden");
                (All<SupportThread>().FirstOrDefault(t => t.MemberId == S("p_member") && t.Queue == queue) ?? throw Err("not_found")).Status = B("p_open") ? "open" : "closed";
                return true;
            }
            case "chat_search":
            {
                RequireActive();
                var q = ((a.TryGetValue("p_query", out var qv) ? qv as string : null) ?? "").Trim();
                var hits = new List<ChatSearchHit>();
                result = hits;
                if (q.Length < 2) return true;
                bool Has_(string? s) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;   // % and _ are plain characters here, like in the database
                MessengerCall("chat_threads", new(), out var listed);
                foreach (var t in ((List<ChatThread>)listed!).Where(t => !t.IsAnnouncements))
                {
                    var found = t.IsSupport
                        ? All<SupportMessage>().Where(m => m.MemberId == t.MemberId && m.Queue == t.Queue && Has_(m.Body)).OrderByDescending(m => m.SentAt).Select(m => (m.Body, m.SentAt)).FirstOrDefault()
                        : All<ChatMessage>().Where(m => m.CoachId == t.CoachId && m.MemberId == t.MemberId && Has_(m.Body)).OrderByDescending(m => m.SentAt).Select(m => (m.Body, m.SentAt)).FirstOrDefault();
                    if (found.Body is null && !Has_(t.WithName)) continue;
                    hits.Add(new ChatSearchHit
                    {
                        Kind = t.Kind, CoachId = t.CoachId, MemberId = t.MemberId, WithId = t.WithId, WithName = t.WithName, Queue = t.Queue,
                        Snippet = found.Body ?? t.LastBody, SentAt = found.Body is null ? t.LastAt : found.SentAt
                    });
                }
                result = hits.OrderByDescending(h => h.SentAt ?? DateTime.MinValue).Take(30).ToList();
                return true;
            }
            case "post_announcement":
            {
                RequireActive();
                var uid = Uid;
                if (!RolesOf(uid).Any(r => r is "admin" or "subadmin")) throw Err("forbidden");
                var body = (a.TryGetValue("p_body", out var b) ? b as string : null)?.Trim() ?? "";
                var audience = Opt("p_audience") ?? "everyone";
                if (body.Length is 0 or > 2000 || audience is not ("everyone" or "members" or "coaches")) throw Err("bad_input");
                var x = new DemoAnnouncement { Id = _announcements.Count + 1, Author = uid, Audience = audience, Body = body, Posted = now };
                _announcements.Add(x);
                foreach (var p in All<Profile>().Where(p => p.IsActive && p.DeletedAt is null).ToList())
                    if ((audience is "everyone" or "members" && All<Member>().Any(m => m.UserId == p.Id)) || (audience is "everyone" or "coaches" && All<Coach>().Any(c => c.UserId == p.Id)))
                        NotifyChat(p.Id, "Announcement", body.Length > 80 ? body[..80] : body, "announcements");
                Audit("announcement.post", "announcements", x.Id.ToString());
                return true;
            }
            case "announcements_list":
            {
                RequireActive();
                if (!RolesOf(Uid).Any(r => r is "admin" or "subadmin")) throw Err("forbidden");
                result = _announcements.Where(x => x.Recipient is null).OrderByDescending(x => x.Id)
                    .Select(x => new AnnouncementRow { Id = x.Id, AuthorName = x.Author is null ? null : NameOf(x.Author), Audience = x.Audience, Body = x.Body, PostedAt = x.Posted }).ToList();
                return true;
            }
            default: return false;
        }
    }
}
