using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

// 0029_reservations.sql in memory: one unit per amenity, a 30-minute cooldown after each use, touching slots = one booking,
// all or nothing, an active pass for solo bookings. Runs under the RPC lock, so checks and writes cannot interleave.
public sealed partial class DemoGateway
{
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30), Manila = TimeSpan.FromHours(8);
    // 0052: an unpaid hold takes its slot and cooldown like a confirmed booking
    IEnumerable<Reservation> Confirmed => All<Reservation>().Where(r => r.Status is "confirmed" or AwaitingPayment);
    const string AwaitingPayment = "awaiting_payment";

    Reservation? HoldOf(Payment pay) => All<Reservation>().FirstOrDefault(r => r.PaymentId == pay.Id && r.Status == AwaitingPayment);

    // settle_amenity_hold (0052): a hold follows its payment, whichever way it goes
    void SettleHold(UserAmenity ua)
    {
        var r = All<Reservation>().FirstOrDefault(x => x.UserAmenityId == ua.Id && x.Status == AwaitingPayment);
        if (r is null) return;
        if (ua.Status == Status.Active)
        {
            r.Status = "confirmed";
            if (ua.IsSession) ua.Status = Status.Expired;   // the session is this booking
            Notify(r.MemberId, "amenity", "Booking confirmed", (r.StartsAt + Manila).ToString("ddd dd MMM, hh:mm tt"));
        }
        else if (ua.Status == Status.Cancelled) { r.Status = "cancelled"; r.CancelledAt = SubscriptionNow; r.CancelReason = "Not paid"; }
    }

    // expire_payments for holds: one that ran out frees its slot before anyone books
    void ExpireHolds()
    {
        foreach (var pay in All<Payment>().Where(p => p.Status == Status.Pending && p.ExpiresAt <= SubscriptionNow && HoldOf(p) is not null).ToList())
        { pay.Status = Status.Expired; CancelTarget(pay); Notify(pay.MemberId, "payment", "Payment expired", pay.PaymentCode); }
    }

    bool ReservationCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        var now = SubscriptionNow;
        int I(string k) => Convert.ToInt32(a[k]);
        Reservation Find(bool coachOnly = false)
        {
            var r = All<Reservation>().FirstOrDefault(x => x.Id == I("p_id"));
            if (r is null || (coachOnly ? r.CoachId != Uid : r.MemberId != Uid && r.CoachId != Uid && !IsStaff)) throw Err("not_found");
            return r;
        }
        switch (fn)
        {
            case "amenity_grid":
            {
                RequireActive();
                ExpireHolds();
                var day = DateTime.Parse((string)a["p_day"]).Date;
                var g = All<GymSettings>().First();
                DateTime from = DateTime.SpecifyKind(day - Manila, DateTimeKind.Utc), to = from.AddDays(1);
                result = new AmenityGrid
                {
                    Now = now, Opens = from + g.OpensAt, Closes = from + g.ClosesAt,
                    Amenities = All<Amenity>().Where(x => x.IsActive).OrderBy(x => x.Name)
                        .Select(x => new GridAmenity { Id = x.Id, Name = x.Name, Status = x.Availability, Back = x.BackAt }).ToList(),
                    Blocks = Confirmed.Where(r => r.StartsAt < to && from < r.EndsAt + Cooldown).OrderBy(r => r.StartsAt)
                        .Select(r => new GridBlock { Amenity = r.AmenityId, Starts = r.StartsAt, Ends = r.EndsAt, Cooldown = r.EndsAt + Cooldown, Mine = r.MemberId == Uid, Id = r.MemberId == Uid ? r.Id : null }).ToList()
                };
                return true;
            }
            case "book_slots":
            {
                RequireActive();
                if (!ActiveMember(Uid)) throw Err("no_active_membership");
                var raw = (System.Collections.IEnumerable)a["p_slots"];
                var slots = raw.Cast<Dictionary<string, object>>().Select(s => (Amenity: Convert.ToInt32(s["amenity"]),
                    Start: DateTime.Parse((string)s["start"], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal))).Distinct().ToList();
                if (slots.Count is 0 or > 48) throw Err("bad_input");
                if (slots.Any(s => (s.Start - DateTime.UnixEpoch).Ticks % Cooldown.Ticks != 0)) throw Err("off_grid");
                // group touching slots per amenity, then check every booking before writing any (all or nothing)
                var bookings = slots.GroupBy(s => s.Amenity).SelectMany(grp =>
                {
                    var list = new List<(int Amenity, DateTime Start, DateTime End, int Count)>();
                    foreach (var s in grp.OrderBy(x => x.Start))
                        if (list.Count > 0 && list[^1].End == s.Start) list[^1] = (s.Amenity, list[^1].Start, s.Start + Cooldown, list[^1].Count + 1);
                        else list.Add((s.Amenity, s.Start, s.Start + Cooldown, 1));
                    return list;
                }).OrderBy(b => b.Start).ToList();
                var g = All<GymSettings>().First();
                var made = new List<(int Amenity, DateTime Start, DateTime End, int? Session)>();
                var sessions = new HashSet<int>();
                foreach (var b in bookings)
                {
                    if (b.Count > 4) throw Err("too_long");
                    if (b.Start <= now) throw Err("past_slot");
                    var day = (b.Start + Manila).Date;
                    if (day > (now + Manila).Date.AddDays(14)) throw Err("too_far_ahead");
                    if (b.Start < day - Manila + g.OpensAt || b.End > day - Manila + g.ClosesAt) throw Err("outside_gym_hours");
                    var am = All<Amenity>().FirstOrDefault(x => x.Id == b.Amenity && x.IsActive) ?? throw Err("not_found");
                    if (am.Availability == "unavailable" && (am.BackAt is null || am.BackAt > b.Start)) throw Err("amenity_unavailable");
                    if (b.Start <= now.AddMinutes(30) && !CoachInGymFor(b.Amenity)) throw Err("no_coach_present");   // 0043, 0052
                    var pass = All<UserAmenity>().Where(p => p.MemberId == Uid && p.AmenityId == b.Amenity && p.Status == Status.Active && (p.ExpiresAt is null || p.ExpiresAt >= b.End) && !sessions.Contains(p.Id))
                        .OrderBy(p => p.IsSession).ThenBy(p => p.Id).FirstOrDefault() ?? throw Err("no_pass");
                    if (pass.IsSession) sessions.Add(pass.Id);
                    var others = Confirmed.Select(r => (AmenityId: r.AmenityId, MemberId: r.MemberId, Start: r.StartsAt, End: r.EndsAt))
                        .Concat(made.Select(m => (AmenityId: m.Amenity, MemberId: Uid!, Start: m.Start, End: m.End))).ToList();
                    if (others.Any(o => o.MemberId == Uid && o.Start < b.End && b.Start < o.End)
                        || All<TrainingSession>().Any(t => t.MemberId == Uid && t.Status == Status.Scheduled && t.ScheduledStart < b.End && b.Start < t.ScheduledEnd)) throw Err("member_busy");
                    if (others.Any(o => o.AmenityId == b.Amenity && o.Start < b.End + Cooldown && b.Start < o.End + Cooldown)) throw Err("slot_taken");
                    made.Add((b.Amenity, b.Start, b.End, pass.IsSession ? pass.Id : null));
                }
                result = made.Select(m =>
                {
                    var r = Add(new Reservation { MemberId = Uid!, AmenityId = m.Amenity, StartsAt = m.Start, EndsAt = m.End, UserAmenityId = m.Session });
                    if (m.Session is { } used) All<UserAmenity>().First(p => p.Id == used).Status = Status.Expired;
                    Audit("reservation.book", "reservations", r.Id.ToString());
                    return Clone(r);
                }).ToList();
                return true;
            }
            case "cancel_reservation":
            {
                RequireActive();
                var r = Find();
                var reason = a.TryGetValue("p_reason", out var v) ? (v as string)?.Trim() : null;
                if (r.MemberId != Uid && (reason is null || reason.Length is < 1 or > 500)) throw Err("bad_reason");
                if (r.Status is not ("confirmed" or AwaitingPayment)) throw Err("already_processed");
                if (r.StartsAt <= now) throw Err("already_started");
                var pay = All<Payment>().FirstOrDefault(p => p.Id == r.PaymentId);
                var paid = r.Status == "confirmed" && pay?.Status == Status.Verified;
                if (paid && r.MemberId == Uid && r.StartsAt.AddHours(-1) < now) throw Err("too_late_to_cancel");   // 0052
                var was = r.Status;
                r.Status = "cancelled"; r.CancelledAt = now; r.CancelledBy = Uid; r.CancelReason = string.IsNullOrEmpty(reason) ? null : reason;
                if (was == AwaitingPayment && pay is { Status: Status.Pending }) { pay.Status = Status.Rejected; pay.RejectedReason = "Booking cancelled"; CancelTarget(pay); }
                else if (paid)
                {
                    var f = Add(new RefundRequest { Kind = "cancel", ReservationId = r.Id, PaymentId = pay!.Id, MemberId = r.MemberId, Amount = pay.Amount,
                        Reason = string.IsNullOrEmpty(reason) ? "Booking cancelled" : reason, RequestedAt = now, Status = "approved", DecidedBy = Uid, DecidedAt = now,
                        DecisionReason = "Cancelled 1 hour or more before the start: full refund" });
                    NotifyStaff("payment", "Booking refund to repay");
                    Audit("refund.request", "refund_requests", f.Id.ToString());
                }
                else if (r.UserAmenityId is { } session) All<UserAmenity>().First(p => p.Id == session).Status = Status.Active;
                if (r.MemberId != Uid) Notify(r.MemberId, "amenity", "Booking cancelled", reason);
                Audit("reservation.cancel", "reservations", r.Id.ToString());
                return true;
            }
            case "book_amenity":   // F12 (0052)
            {
                RequireActive();
                if (!ActiveMember(Uid)) throw Err("no_active_membership");
                var amenityId = I("p_amenity"); var minutes = I("p_minutes");
                var method = a.TryGetValue("p_method", out var m) ? m as string ?? PayMethod.Qr : PayMethod.Qr;
                if (minutes is not (30 or 60 or 90 or 120) || method is not (PayMethod.Qr or PayMethod.Cash)) throw Err("bad_input");
                var start = DateTime.Parse((string)a["p_start"], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var end = start.AddMinutes(minutes);
                ExpireHolds();
                if (All<UserAmenity>().Any(p => p.MemberId == Uid && p.AmenityId == amenityId && p.Status == Status.Active && (p.ExpiresAt is null || p.ExpiresAt >= end)))
                {
                    var slots = Enumerable.Range(0, minutes / 30)
                        .Select(i => new Dictionary<string, object> { ["amenity"] = amenityId, ["start"] = start.AddMinutes(30 * i).ToString("yyyy-MM-ddTHH:mm:ssZ") }).ToList();
                    ReservationCall("book_slots", new() { ["p_slots"] = slots }, out var made);
                    result = ((IEnumerable<Reservation>)made!).First();
                    return true;
                }
                if ((start - DateTime.UnixEpoch).Ticks % Cooldown.Ticks != 0) throw Err("off_grid");
                if (start <= now) throw Err("past_slot");
                if (start.AddHours(-1) <= now) throw Err("too_soon");
                var day = (start + Manila).Date;
                if (day > (now + Manila).Date.AddDays(14)) throw Err("too_far_ahead");
                var g = All<GymSettings>().First();
                if (start < day - Manila + g.OpensAt || end > day - Manila + g.ClosesAt) throw Err("outside_gym_hours");
                var am = All<Amenity>().FirstOrDefault(x => x.Id == amenityId && x.IsActive) ?? throw Err("not_found");
                if (am.Availability == "unavailable" && (am.BackAt is null || am.BackAt > start)) throw Err("amenity_unavailable");
                if (Confirmed.Any(o => o.MemberId == Uid && o.StartsAt < end && start < o.EndsAt)
                    || All<TrainingSession>().Any(t => t.MemberId == Uid && t.Status == Status.Scheduled && t.ScheduledStart < end && start < t.ScheduledEnd)) throw Err("member_busy");
                if (Confirmed.Any(o => o.AmenityId == amenityId && o.StartsAt < end + Cooldown && start < o.EndsAt + Cooldown)) throw Err("slot_taken");
                var ua = Add(new UserAmenity { MemberId = Uid!, AmenityId = amenityId, Status = Status.Pending, AvailedAt = now, IsSession = am.DurationDays is null });
                var pay = Add(new Payment { PaymentCode = PaymentCode(), Kind = PayKind.Amenity, Method = method, Status = Status.Pending, Amount = am.Price,
                    MemberId = Uid, UserAmenityId = ua.Id, CreatedAt = now, ExpiresAt = new[] { now.AddHours(g.PaymentExpiryHours), start.AddHours(-1) }.Min() });
                var r = Add(new Reservation { MemberId = Uid!, AmenityId = amenityId, StartsAt = start, EndsAt = end, UserAmenityId = ua.Id, PaymentId = pay.Id, Status = AwaitingPayment });
                Audit("reservation.hold", "reservations", r.Id.ToString());
                result = Clone(r);
                return true;
            }
            case "claim_amenity_no_show":   // F13 (0052)
            {
                RequireActive();
                var reason = (a.GetValueOrDefault("p_reason") as string)?.Trim();
                if (string.IsNullOrEmpty(reason) || reason.Length > 1000) throw Err("bad_input");
                var r = All<Reservation>().FirstOrDefault(x => x.Id == I("p_id") && x.MemberId == Uid) ?? throw Err("not_found");
                var pay = All<Payment>().FirstOrDefault(p => p.Id == r.PaymentId);
                if (r.Status != "confirmed" || pay?.Status != Status.Verified) throw Err("not_refundable");
                if (now < r.EndsAt) throw Err("not_ended");
                if (now > r.EndsAt.AddHours(24)) throw Err("claim_window_closed");
                if (All<RefundRequest>().Any(f => f.ReservationId == r.Id)) throw Err("already_requested");
                var claim = Add(new RefundRequest { Kind = "no_show", ReservationId = r.Id, PaymentId = pay.Id, MemberId = r.MemberId, Amount = pay.Amount, Reason = reason, RequestedAt = now });
                NotifyStaff("payment", "Booking refund claim");
                Audit("refund.request", "refund_requests", claim.Id.ToString());
                result = Clone(claim);
                return true;
            }
            case "decide_amenity_refund":   // F13 (0052): the front desk decides, with a reason
            {
                Require("payments:verify");
                var reason = (a.GetValueOrDefault("p_reason") as string)?.Trim();
                if (string.IsNullOrEmpty(reason) || reason.Length > 1000 || a.GetValueOrDefault("p_approve") is null) throw Err("bad_input");
                var approve = Convert.ToBoolean(a["p_approve"]);
                var f = All<RefundRequest>().FirstOrDefault(x => x.Id == I("p_request_id") && x.Kind == "no_show") ?? throw Err("not_found");
                if (f.MemberId == Uid) throw Err("self_decision");
                if (f.Status != "pending") throw Err("already_processed");
                var pay = All<Payment>().First(p => p.Id == f.PaymentId);
                if (pay.Status != Status.Verified || f.Amount != pay.Amount) throw Err("not_refundable");
                f.Status = approve ? "approved" : "rejected"; f.DecidedBy = Uid; f.DecidedAt = now; f.DecisionReason = reason;
                Notify(f.MemberId, "payment", approve ? "Refund approved" : "Refund claim declined", reason);
                Audit("refund.decide", "refund_requests", f.Id.ToString());
                return true;
            }
            case "set_runs_amenity":   // F14 (0052)
            {
                RequireActive();
                if (!IsCoach(Uid!)) throw Err("forbidden");
                var amenityId = I("p_amenity");
                if (a.GetValueOrDefault("p_on") is null || !All<Amenity>().Any(x => x.Id == amenityId && x.IsActive)) throw Err("not_found");
                var mine = All<CoachAmenity>().FirstOrDefault(c => c.AmenityId == amenityId && c.CoachId == Uid);
                if (Convert.ToBoolean(a["p_on"])) { if (mine is null) Add(new CoachAmenity { AmenityId = amenityId, CoachId = Uid! }); }
                else if (mine is not null) Table(typeof(CoachAmenity)).Remove(mine);
                Audit("amenity.runs", "amenities", amenityId.ToString());
                return true;
            }
            case "end_reservation":
            {
                RequireActive();
                var r = Find();
                if (r.Status != "confirmed" || !(r.StartsAt < now && now < r.EndsAt)) throw Err("not_in_use");
                r.EndsAt = now;
                Audit("reservation.end", "reservations", r.Id.ToString());
                return true;
            }
            case "extend_reservation":
            {
                RequireActive();
                var r = Find(coachOnly: true);
                var minutes = I("p_minutes");
                if (minutes is not (30 or 60 or 90 or 120)) throw Err("bad_input");
                if (r.Status != "confirmed" || r.EndsAt <= now) throw Err("already_processed");
                var end = r.EndsAt.AddMinutes(minutes);
                var day = (r.StartsAt + Manila).Date;
                if (end > day - Manila + All<GymSettings>().First().ClosesAt) throw Err("outside_gym_hours");
                var rest = Confirmed.Where(o => o.Id != r.Id).ToList();
                if (rest.Any(o => (o.MemberId == r.MemberId || o.CoachId == r.CoachId) && o.StartsAt < end && r.EndsAt < o.EndsAt)) throw Err("member_busy");
                if (rest.Any(o => o.AmenityId == r.AmenityId && o.StartsAt < end + Cooldown && r.StartsAt < o.EndsAt + Cooldown)) throw Err("slot_taken");
                r.EndsAt = end;
                foreach (var s in All<TrainingSession>().Where(s => s.Id == r.TrainingSessionId && s.Status == Status.Scheduled)) s.ScheduledEnd = end;   // 0031: the session moves with it
                Notify(r.MemberId, "amenity", "Session extended", (end + Manila).ToString("HH:mm"));
                Audit("reservation.extend", "reservations", r.Id.ToString());
                return true;
            }
            default: return false;
        }
    }
}
