using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

// 0031_structured_requests.sql in memory: a member request may name an amenity; the coach proposes another time with a
// reason (valid 24 h or until it starts); the member accepts that exact version, asks again, or declines with a reason.
// Confirming with an amenity reserves it for the session (inside the coach's shift); everything is checked before writing.
public sealed partial class DemoGateway
{
    const string Proposed = "proposed";

    bool LiveProposal(TimeRequest r, DateTime now) => r.Status == Proposed && r.ProposalExpiresAt > now;
    bool CoachTurn(TimeRequest r, DateTime now) => r.Status == Status.Pending || (r.Status == Proposed && !LiveProposal(r, now));

    void CheckSessionTime(DateTime start, DateTime end, int? amenityId, DateTime now)
    {
        if (end <= start) throw Err("bad_input");
        if (start <= now) throw Err("past_slot");
        if (amenityId is null) return;
        if (!All<Amenity>().Any(a => a.Id == amenityId && a.IsActive)) throw Err("not_found");
        static bool OffGrid(DateTime t) => (t.ToUniversalTime() - DateTime.UnixEpoch).Ticks % Cooldown.Ticks != 0;
        if (OffGrid(start) || OffGrid(end)) throw Err("off_grid");
        if (end - start > TimeSpan.FromHours(2)) throw Err("too_long");
        var g = All<GymSettings>().First();
        var day = (start.ToUniversalTime() + Manila).Date;
        if (start.ToUniversalTime() < day - Manila + g.OpensAt || end.ToUniversalTime() > day - Manila + g.ClosesAt) throw Err("outside_gym_hours");
    }

    /// <summary>Throws if the session's amenity can't be held at that time: coach shift, closed amenity, cooldown, or someone busy.</summary>
    void CheckSessionAmenity(string coach, string member, int? sessionId, int amenityId, DateTime start, DateTime end)
    {
        if (!All<ShiftPost>().Any(s => s.CoachId == coach && s.Status == Status.Scheduled && s.Start <= start && s.End >= end)) throw Err("coach_unavailable");
        var am = All<Amenity>().FirstOrDefault(a => a.Id == amenityId && a.IsActive) ?? throw Err("not_found");
        if (am.Availability == "unavailable" && (am.BackAt is null || am.BackAt > start)) throw Err("amenity_unavailable");
        var others = Confirmed.Where(r => sessionId is null || r.TrainingSessionId != sessionId).ToList();
        if (others.Any(o => (o.MemberId == member || o.CoachId == coach) && o.StartsAt < end && start < o.EndsAt)) throw Err("member_busy");
        if (others.Any(o => o.AmenityId == amenityId && o.StartsAt < end + Cooldown && start < o.EndsAt + Cooldown)) throw Err("slot_taken");
    }

    void PlaceSessionAmenity(TrainingSession s, int amenityId, DateTime start, DateTime end)
    {
        var held = Confirmed.FirstOrDefault(r => r.TrainingSessionId == s.Id);
        if (held is not null) { held.StartsAt = start; held.EndsAt = end; held.AmenityId = amenityId; }
        else Add(new Reservation { MemberId = s.MemberId, AmenityId = amenityId, CoachId = s.CoachId, TrainingSessionId = s.Id, StartsAt = start, EndsAt = end });
    }

    bool RequestCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        var now = SubscriptionNow;
        var uid = Uid;
        int I(string k) => Convert.ToInt32(a[k]);
        int? OptI(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToInt32(v) : null;
        DateTime T(string k) => DateTime.Parse((string)a[k], null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        string? Text(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        TimeRequest Find() => All<TimeRequest>().FirstOrDefault(x => x.Id == I("p_request_id")) ?? throw Err("not_found");
        switch (fn)
        {
            case "request_session":
            {
                RequireActive();
                if (uid == (string)a["p_coach_id"]) throw Err("forbidden");
                var amenity = OptI("p_amenity_id");
                CheckSessionTime(T("p_start"), T("p_end"), amenity, now);
                var id = (int)CallLocked("request_time", a)!;
                All<TimeRequest>().First(r => r.Id == id).AmenityId = amenity;
                result = id;
                return true;
            }
            case "propose_time":
            {
                RequireActive();
                var r = Find();
                if (r.CoachId != uid) throw Err("not_found");
                if (r.RequestedBy != "member" || r.SessionId is not null || !CoachTurn(r, now)) throw Err("already_processed");
                var reason = Text("p_reason");
                if (reason is null || reason.Length > 500) throw Err("bad_reason");
                var (start, end, amenity) = (T("p_start"), T("p_end"), OptI("p_amenity_id"));
                CheckSessionTime(start, end, amenity, now);
                r.Status = Proposed; r.ProposedStart = start; r.ProposedEnd = end; r.ProposedAmenityId = amenity; r.ProposalReason = reason;
                r.ProposalExpiresAt = now.AddHours(24) < start ? now.AddHours(24) : start; r.Version++;
                Notify(r.MemberId, "time_request", "Your coach proposed another time", reason);
                return true;
            }
            case "accept_proposal":
            {
                RequireActive();
                var r = Find();
                if (r.MemberId != uid || r.RequestedBy != "member") throw Err("not_found");
                if (r.Status != Proposed) throw Err("already_processed");
                if (All<TrainingSession>().Any(s => s.CoachId == r.CoachId && s.MemberId == uid && s.Status == Status.Scheduled && s.ScheduledEnd > now)) throw Err("active_coaching_session");
                if (r.Version != I("p_version")) throw Err("stale_proposal");
                if (r.ProposalExpiresAt <= now) { r.Status = Status.Pending; result = "expired"; return true; }
                if (!ActiveMember(uid)) throw Err("no_active_membership");
                var (start, end) = (r.ProposedStart!.Value, r.ProposedEnd!.Value);
                if (Overlap(r.CoachId, true, start, end, 0) || Overlap(uid, false, start, end, 0)) throw Err("overlap");
                if (r.ProposedAmenityId is { } am) CheckSessionAmenity(r.CoachId, uid, null, am, start, end);
                var session = Add(new TrainingSession { CoachId = r.CoachId, MemberId = uid, Title = "Training session", ScheduledStart = start, ScheduledEnd = end });
                if (r.ProposedAmenityId is { } amenity) PlaceSessionAmenity(session, amenity, start, end);
                r.Status = Status.Approved; r.RequestedStart = start; r.RequestedEnd = end; r.AmenityId = r.ProposedAmenityId;
                Notify(r.CoachId, "time_request", "Proposal accepted");
                result = "confirmed";
                return true;
            }
            case "request_another_time":
            {
                RequireActive();
                var r = Find();
                if (r.MemberId != uid || r.RequestedBy != "member") throw Err("not_found");
                if (r.Status != Proposed) throw Err("already_processed");
                var (start, end, amenity) = (T("p_start"), T("p_end"), OptI("p_amenity_id"));
                CheckSessionTime(start, end, amenity, now);
                r.Status = Status.Pending; r.RequestedStart = start; r.RequestedEnd = end; r.AmenityId = amenity; r.Message = Text("p_message") ?? r.Message;
                r.ProposedStart = r.ProposedEnd = null; r.ProposedAmenityId = null; r.ProposalReason = null; r.ProposalExpiresAt = null; r.Version++;
                Notify(r.CoachId, "time_request", "Another time requested");
                return true;
            }
            case "decline_time_request":
            {
                RequireActive();
                var r = Find();
                if (uid != r.CoachId && uid != r.MemberId) throw Err("not_found");
                var reason = Text("p_reason");
                if (reason is null || reason.Length > 500) throw Err("bad_reason");
                var allowed = (uid == r.CoachId && r.RequestedBy == "member" && CoachTurn(r, now))
                    || (uid == r.MemberId && r.Status == Proposed)
                    || (uid == r.MemberId && r.RequestedBy == "coach" && r.Status == Status.Pending);
                if (!allowed) throw Err("already_processed");
                r.Status = Status.Rejected; r.DeclineReason = reason;
                Notify(uid == r.CoachId ? r.MemberId : r.CoachId, "time_request", "Request declined", reason);
                return true;
            }
            default: return false;
        }
    }
}
