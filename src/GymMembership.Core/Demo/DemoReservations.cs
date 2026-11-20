using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

// 0029_reservations.sql in memory: one unit per amenity, a 30-minute cooldown after each use, touching slots = one booking,
// all or nothing, an active pass for solo bookings. Runs under the RPC lock, so checks and writes cannot interleave.
public sealed partial class DemoGateway
{
    static readonly TimeSpan SlotStep = TimeSpan.FromMinutes(30), Cooldown = TimeSpan.Zero, Manila = TimeSpan.FromHours(8);
    // G13 (0064): only confirmed bookings take a slot, the member's time and the coach; a request waiting for its down payment holds nothing
    IEnumerable<Reservation> Confirmed => All<Reservation>().Where(r => r.Status == "confirmed");
    const string AwaitingPayment = "awaiting_payment";

    Reservation? HoldOf(Payment pay) => All<Reservation>().FirstOrDefault(r => r.PaymentId == pay.Id && r.Status == AwaitingPayment);

    // settle_amenity_hold (0052): a hold follows its payment, whichever way it goes
    /// <summary>0092: a walk-in's online booking once its payment is verified (confirmed, or cancelled into a refund if the slot went first) or cancelled.</summary>
    void SettleWalkIn(Payment pay)
    {
        var r = All<Reservation>().FirstOrDefault(x => x.PaymentId == pay.Id && x.WalkInName is not null && x.Status == AwaitingPayment);
        if (r is null) return;
        if (pay.Status != Status.Verified) { r.Status = "cancelled"; r.CancelledAt = SubscriptionNow; r.CancelReason = "Not paid"; return; }
        if (Confirmed.Any(o => o.AmenityId == r.AmenityId && o.StartsAt < r.EndsAt + Cooldown && r.StartsAt < o.EndsAt + Cooldown))
        {
            r.Status = "cancelled"; r.CancelledAt = SubscriptionNow; r.CancelReason = "Slot taken";
            Add(new RefundRequest { Kind = "cancel", ReservationId = r.Id, PaymentId = pay.Id, MemberId = null!, Amount = pay.Amount, Reason = "Slot taken", RequestedAt = SubscriptionNow,
                Status = "approved", DecidedBy = pay.RecordedBy, DecidedAt = SubscriptionNow, DecisionReason = $"Slot taken: another booking was paid first. Refund the walk-in {r.WalkInName}." });
            NotifyStaff("payment", "Booking refund to repay");
            return;
        }
        r.Status = "confirmed";
    }

    /// <summary>0094: a confirmed paid member booking's receipt in their Front desk chat, from whoever confirmed it, or the oldest admin.</summary>
    void SendBookingReceipt(Reservation r)
    {
        var pay = All<Payment>().FirstOrDefault(p => p.Id == r.PaymentId);
        if (r.WalkInName is not null || pay?.ReceiptNo is null) return;
        var from = Uid is { } u && u != r.MemberId ? u
            : All<UserRole>().Where(x => RolesOf(x.UserId).Contains("admin")).Select(x => x.UserId).FirstOrDefault(id => All<Profile>().Any(p => p.Id == id && p.IsActive));
        if (from is null) return;
        var now = SubscriptionNow;
        var how = pay.Method switch { PayMethod.Cash => "in cash", PayMethod.Gcash => "by GCash", _ => "online" };
        var name = All<Amenity>().FirstOrDefault(a => a.Id == r.AmenityId)?.Name ?? "Amenity";
        var body = $"Receipt (automatic). Your booking is confirmed: {r.StartsAt + Manila:ddd d MMM, h:mm tt} to {r.EndsAt + Manila:h:mm tt}, at {name}. Paid PHP {pay.Amount:N2} {how}. Receipt {pay.ReceiptNo}"
            + (r.TotalPrice > pay.Amount ? $". Balance PHP {r.TotalPrice - pay.Amount:N2} at the front desk" : "") + ". Show this message at the front desk.";
        var thread = All<SupportThread>().FirstOrDefault(t => t.MemberId == r.MemberId && t.Queue == ChatQueue.FrontDesk) ?? Add(new SupportThread { MemberId = r.MemberId, Queue = ChatQueue.FrontDesk });
        Add(new SupportMessage { MemberId = r.MemberId, Queue = ChatQueue.FrontDesk, SenderId = from, Body = body, SentAt = now });
        thread.LastMessageAt = now;
        NotifyChat(r.MemberId, "Message from the front desk", body[..80], $"s|{r.MemberId}|{ChatQueue.FrontDesk}");
    }

    void SettleHold(UserAmenity ua)
    {
        var r = All<Reservation>().FirstOrDefault(x => x.UserAmenityId == ua.Id && x.Status == AwaitingPayment);
        if (r is null) return;
        if (ua.Status == Status.Active)
        {
            // G13, G14 (0064): someone else's down payment verified first took the slot, the coach or the member's own time
            var others = Confirmed.Where(o => o.Id != r.Id).ToList();
            var why = others.Any(o => o.CoachId is not null && o.CoachId == r.CoachId && o.StartsAt < r.EndsAt && r.StartsAt < o.EndsAt) ? "Coach not free"
                : others.Any(o => o.MemberId == r.MemberId && o.StartsAt < r.EndsAt && r.StartsAt < o.EndsAt) ? "You have another booking then"
                : others.Any(o => o.AmenityId == r.AmenityId && o.StartsAt < r.EndsAt + Cooldown && r.StartsAt < o.EndsAt + Cooldown) ? "Slot taken" : null;
            if (why is not null)
            {
                var pay = All<Payment>().First(p => p.Id == r.PaymentId);
                r.Status = "cancelled"; r.CancelledAt = SubscriptionNow; r.CancelReason = why; ua.Status = Status.Cancelled;
                Add(new RefundRequest { Kind = "cancel", ReservationId = r.Id, PaymentId = pay.Id, MemberId = r.MemberId, Amount = pay.Amount, Reason = why, RequestedAt = SubscriptionNow,
                    Status = "approved", DecidedBy = Uid, DecidedAt = SubscriptionNow, DecisionReason = why + ": another booking's down payment was verified first. Down payment refunded." });
                Notify(r.MemberId, "amenity", "Your slot was taken: down payment refunded", why);
                NotifyStaff("payment", "Booking refund to repay");
                return;
            }
            r.Status = "confirmed";
            if (ua.IsSession) ua.Status = Status.Expired;   // the session is this booking
            SendBookingReceipt(r);   // 0094
            Notify(r.MemberId, "amenity", "Booking confirmed", (r.StartsAt + Manila).ToString("ddd dd MMM, hh:mm tt"));
        }
        else if (ua.Status == Status.Cancelled) { r.Status = "cancelled"; r.CancelledAt = SubscriptionNow; r.CancelReason = "Not paid"; }
    }

    // G14 (0064): runs the amenity, a scheduled shift over the whole time, no confirmed booking or scheduled session then
    bool CoachIsFree(string coach, int amenityId, DateTime start, DateTime end) =>
        All<CoachAmenity>().Any(c => c.CoachId == coach && c.AmenityId == amenityId) && All<Profile>().Any(p => p.Id == coach && p.IsActive)
        && All<ShiftPost>().Any(s => s.CoachId == coach && s.Status == Status.Scheduled && s.Start <= start && s.End >= end)
        && !Confirmed.Any(r => r.CoachId == coach && r.StartsAt < end && start < r.EndsAt)
        && !All<TrainingSession>().Any(t => t.CoachId == coach && t.Status == Status.Scheduled && t.ScheduledStart < end && start < t.ScheduledEnd);

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
                    Amenities = All<Amenity>().Where(x => x.State == CatalogState.Published).OrderBy(x => x.Name)
                        .Select(x => new GridAmenity { Id = x.Id, Name = x.Name, Status = x.Availability, Back = x.BackAt }).ToList(),
                    Blocks = All<Reservation>().Where(r => r.Status == "confirmed" || (r.Status == AwaitingPayment && r.MemberId == Uid))
                        .Where(r => r.StartsAt < to && from < r.EndsAt + Cooldown).OrderBy(r => r.StartsAt)
                        .Select(r => new GridBlock { Amenity = r.AmenityId, Starts = r.StartsAt, Ends = r.EndsAt, Cooldown = r.EndsAt + Cooldown, Mine = r.MemberId == Uid,
                            Id = r.MemberId == Uid ? r.Id : null, Pending = r.Status == AwaitingPayment }).ToList()
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
                if (slots.Any(s => (s.Start - DateTime.UnixEpoch).Ticks % SlotStep.Ticks != 0)) throw Err("off_grid");
                // group touching slots per amenity, then check every booking before writing any (all or nothing)
                var bookings = slots.GroupBy(s => s.Amenity).SelectMany(grp =>
                {
                    var list = new List<(int Amenity, DateTime Start, DateTime End, int Count)>();
                    foreach (var s in grp.OrderBy(x => x.Start))
                        if (list.Count > 0 && list[^1].End == s.Start) list[^1] = (s.Amenity, list[^1].Start, s.Start + SlotStep, list[^1].Count + 1);
                        else list.Add((s.Amenity, s.Start, s.Start + SlotStep, 1));
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
                    var am = AmenityForSale(b.Amenity);
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
                if (r.BalancePaymentId is not null) throw Err("balance_paid");   // G13 (0064): the desk voids the balance first
                var pay = All<Payment>().FirstOrDefault(p => p.Id == r.PaymentId);
                var paid = r.Status == "confirmed" && pay?.Status == Status.Verified;
                var mine = r.MemberId == Uid;
                var was = r.Status;
                r.Status = "cancelled"; r.CancelledAt = now; r.CancelledBy = Uid; r.CancelReason = string.IsNullOrEmpty(reason) ? null : reason;
                if (was == AwaitingPayment && pay is { Status: Status.Pending }) { pay.Status = Status.Rejected; pay.RejectedReason = "Booking cancelled"; CancelTarget(pay); }
                else if (paid)
                {
                    if (pay!.Amount > 0 && (!mine || r.StartsAt.AddHours(-24) >= now))   // G13 & B5: 24 hours or more before, or the gym cancelling, and only when paid > 0
                    {
                        var f = Add(new RefundRequest { Kind = "cancel", ReservationId = r.Id, PaymentId = pay.Id, MemberId = r.MemberId, Amount = pay.Amount,
                            Reason = string.IsNullOrEmpty(reason) ? "Booking cancelled" : reason, RequestedAt = now, Status = "approved", DecidedBy = Uid, DecidedAt = now,
                            DecisionReason = mine ? "Cancelled 24 hours or more before the start: down payment refunded" : "Cancelled by the gym: down payment refunded" });
                        NotifyStaff("payment", "Booking refund to repay");
                        Audit("refund.request", "refund_requests", f.Id.ToString());
                    }
                }
                else if (!paid && r.UserAmenityId is { } session) foreach (var p in All<UserAmenity>().Where(p => p.Id == session && p.IsSession)) p.Status = Status.Active;
                if (r.MemberId != Uid) Notify(r.MemberId, "amenity", "Booking cancelled", reason);
                Audit("reservation.cancel", "reservations", r.Id.ToString());
                return true;
            }
            case "book_amenity":   // F12 (0052)
            case "desk_book_amenity":   // plan 2026-11-17 Z1, Z2 (0087): the desk books for a customer, full price in cash at once
            {
                RequireActive();
                var desk = fn == "desk_book_amenity";
                var who = desk ? a.GetValueOrDefault("p_member") as string ?? throw Err("bad_input") : Uid!;
                if (desk) { Require("payments:record"); if (who == Uid || All<Member>().All(x => x.UserId != who)) throw Err(who == Uid ? "bad_input" : "not_found"); }
                if (!ActiveMember(who)) throw Err("no_active_membership");   // 0097: the desk too
                var amenityId = I("p_amenity"); var minutes = I("p_minutes");
                var method = desk ? PayMethod.Cash : a.TryGetValue("p_method", out var m) ? m as string ?? PayMethod.Qr : PayMethod.Qr;
                if (!desk && method == PayMethod.Cash) throw Err("online_only");   // 0099: members pay online; the desk records cash
                if (minutes < 60 || minutes % 60 != 0 || method is not (PayMethod.Qr or PayMethod.Cash)) throw Err("bad_input");   // 0090: whole hours
                var start = DateTime.Parse((string)a["p_start"], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var end = start.AddMinutes(minutes);
                if (!All<MembershipTerm>().Any(t => t.MemberId == who && t.CancelledAt is null && t.StartsAt <= start && t.EndsAt > start
                    && (!(All<MembershipPackage>().FirstOrDefault(p => p.Id == t.PackageId)?.IsDayPass ?? false) || t.EndsAt >= end))) throw Err("outside_membership");   // 0098, 0106
                var coach = a.GetValueOrDefault("p_coach") as string;
                if ((start - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerHour != 0) throw Err("not_on_the_hour");   // 0093: pass bookings too
                ExpireHolds();
                var amenity = AmenityForSale(amenityId);
                if (coach is not null && !CoachIsFree(coach, amenityId, start, end)) throw Err("coach_not_free");   // G14 (0064)
                if (!desk && All<UserAmenity>().Where(p => p.MemberId == Uid && p.AmenityId == amenityId && p.Status == Status.Active && (p.ExpiresAt is null || p.ExpiresAt >= end))
                        .OrderBy(p => p.IsSession).ThenBy(p => p.Id).FirstOrDefault() is { } heldPass)
                {
                    var slots = Enumerable.Range(0, minutes / 30)
                        .Select(i => new Dictionary<string, object> { ["amenity"] = amenityId, ["start"] = start.AddMinutes(30 * i).ToString("yyyy-MM-ddTHH:mm:ssZ") }).ToList();
                    ReservationCall("book_slots", new() { ["p_slots"] = slots }, out var made);
                    var booked = All<Reservation>().First(x => x.Id == ((IEnumerable<Reservation>)made!).First().Id);
                    if (coach is not null) { booked.CoachId = coach; booked.TotalPrice = amenity.CoachFee * (minutes / 60); booked.DownPayment = 0; booked.UserAmenityId ??= heldPass.Id; }   // the fee at the desk
                    result = Clone(booked);
                    return true;
                }
                if ((start - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerHour != 0) throw Err("not_on_the_hour");   // 0093
                if (start <= now) throw Err("past_slot");
                if (!desk && start.AddHours(-1) <= now) throw Err("too_soon");
                var day = (start + Manila).Date;
                if (day > (now + Manila).Date.AddDays(14)) throw Err("too_far_ahead");
                var g = All<GymSettings>().First();
                if (start < day - Manila + g.OpensAt || end > day - Manila + g.ClosesAt) throw Err("outside_gym_hours");
                var am = AmenityForSale(amenityId);
                if (am.Availability == "unavailable" && (am.BackAt is null || am.BackAt > start)) throw Err("amenity_unavailable");
                if (All<TrainingSession>().Any(t => t.MemberId == who && t.Status == Status.Scheduled && t.ScheduledStart < end && start < t.ScheduledEnd)) throw Err("member_busy");
                if (Confirmed.Any(x => x.AmenityId == amenityId && x.StartsAt < end + Cooldown && start < x.EndsAt + Cooldown)) throw Err("slot_taken");
                if (Confirmed.Any(x => x.MemberId == who && x.StartsAt < end && start < x.EndsAt)) throw Err("member_busy");
                // G13 (0064): no slot is held; the down payment is the percent of price + coach fee - code (G4)
                var total = (am.Price + (coach is null ? 0 : am.CoachFee)) * (minutes / 60);   // 0090: per hour
                var d = Discounted(who, new() { ["p_discount_code"] = a.GetValueOrDefault("p_code")! }, total, "amenity", amenityId);
                var down = desk ? d.Amount : Math.Round(d.Amount * g.DownPaymentPercent / 100m, 2);
                if (desk) OpenShiftOf(Uid);   // 0050: no open shift, nothing is booked
                var ua = Add(new UserAmenity { MemberId = who, AmenityId = amenityId, Status = Status.Pending, AvailedAt = now, IsSession = SessionOnly(am) });
                var pay = Add(new Payment { PaymentCode = PaymentCode(), Kind = PayKind.Amenity, Method = method, Status = Status.Pending, Amount = down, DiscountId = d.Id, DiscountAmount = d.Cut,
                    MemberId = who, RecordedBy = desk ? Uid : null, UserAmenityId = ua.Id, CreatedAt = now, ExpiresAt = new[] { now.AddHours(g.PaymentExpiryHours), start.AddHours(-1) }.Min() });
                var r = Add(new Reservation { MemberId = who, AmenityId = amenityId, CoachId = coach, StartsAt = start, EndsAt = end, UserAmenityId = ua.Id, PaymentId = pay.Id,
                    Status = AwaitingPayment, TotalPrice = d.Amount, DownPayment = down });
                Audit("reservation.request", "reservations", r.Id.ToString());
                if (desk) Call("verify_payment", new() { ["p_payment_id"] = pay.Id, ["p_approve"] = true });   // confirms it and sends the receipt (0094)
                else if (down == 0)
                {
                    pay.Status = Status.Verified; pay.VerifiedAt = now; pay.ReceiptNo = ReceiptNo();
                    r.Status = "confirmed"; ua.Status = Status.Active; ua.AvailedAt = now; ua.ExpiresAt = r.EndsAt;
                    Notify(who, "payment", "Payment received", pay.PaymentCode);
                }
                result = Clone(r);
                return true;
            }
            case "desk_book_walk_in":   // 0091: the desk books someone with no account, by name; full price in cash, confirmed at once
            {
                RequireActive(); Require("payments:record");
                var name = (a.GetValueOrDefault("p_name") as string)?.Trim();
                var amenityId = I("p_amenity"); var minutes = I("p_minutes");
                if (string.IsNullOrEmpty(name) || name.Length > 100 || minutes < 60 || minutes % 60 != 0) throw Err("bad_input");
                var start = DateTime.Parse((string)a["p_start"], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var end = start.AddMinutes(minutes);
                var am = AmenityForSale(amenityId);
                if ((start - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerHour != 0) throw Err("not_on_the_hour");   // 0093
                if (start <= now) throw Err("past_slot");
                var day = (start + Manila).Date;
                if (day > (now + Manila).Date.AddDays(14)) throw Err("too_far_ahead");
                var g = All<GymSettings>().First();
                if (start < day - Manila + g.OpensAt || end > day - Manila + g.ClosesAt) throw Err("outside_gym_hours");
                if (am.Availability == "unavailable" && (am.BackAt is null || am.BackAt > start)) throw Err("amenity_unavailable");
                var online = a.GetValueOrDefault("p_online") is true;   // 0092: paid online through Xendit, confirmed when it is paid
                int? shift = online ? null : OpenShiftOf(Uid!);   // 0050: no open shift, nothing is booked
                if (Confirmed.Any(x => x.AmenityId == amenityId && x.StartsAt < end + Cooldown && start < x.EndsAt + Cooldown)) throw Err("slot_taken");
                var total = am.Price * (minutes / 60);
                var pay = Add(online
                    ? new Payment { PaymentCode = PaymentCode(), Kind = PayKind.Amenity, Method = PayMethod.Qr, Status = Status.Pending, Amount = total, WalkInName = name,
                        RecordedBy = Uid, CreatedAt = now, ExpiresAt = new[] { now.AddMinutes(30), start }.Min() }
                    : new Payment { PaymentCode = PaymentCode(), Kind = PayKind.Amenity, Method = PayMethod.Cash, Status = Status.Verified, Amount = total, WalkInName = name,
                        RecordedBy = Uid, VerifiedBy = Uid, VerifiedAt = now, ReceiptNo = ReceiptNo(), CashShiftId = shift, CreatedAt = now });
                var r = Add(new Reservation { MemberId = null!, WalkInName = name, AmenityId = amenityId, StartsAt = start, EndsAt = end, PaymentId = pay.Id,
                    Status = online ? AwaitingPayment : "confirmed", TotalPrice = total, DownPayment = total });
                Audit(online ? "reservation.desk_walk_in_online" : "reservation.desk_walk_in", "reservations", r.Id.ToString());
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
            case "request_walk_in_refund":   // 0096
            {
                Require("payments:record");
                var reason = (a.GetValueOrDefault("p_reason") as string)?.Trim();
                if (string.IsNullOrEmpty(reason) || reason.Length > 900) throw Err("bad_input");
                var gym = a.GetValueOrDefault("p_gym_cancels") is true;
                var pay = All<Payment>().FirstOrDefault(p => p.Id == I("p_payment_id")) ?? throw Err("not_found");
                if (pay.Status != Status.Verified || pay.Amount <= 0 || pay.MemberId is not null) throw Err("not_refundable");
                Reservation? booking = null;
                if (pay.Kind == PayKind.WalkIn) { if ((pay.VerifiedAt!.Value + Manila).Date != (now + Manila).Date) throw Err("refund_day_passed"); }
                else
                {
                    booking = All<Reservation>().FirstOrDefault(r => r.PaymentId == pay.Id && r.WalkInName is not null && r.Status == "confirmed") ?? throw Err("not_refundable");
                    if (booking.StartsAt <= now) throw Err("already_started");
                    if (!gym && booking.StartsAt.AddHours(-24) < now) throw Err("refund_too_late");
                }
                if (All<RefundRequest>().Any(f => f.PaymentId == pay.Id && f.Kind != "online")) throw Err("already_requested");
                var asked = Add(new RefundRequest { Kind = booking is null ? "day_pass" : "walk_in_booking", ReservationId = booking?.Id, PaymentId = pay.Id, MemberId = null!, Amount = pay.Amount,
                    Reason = (gym ? "Gym is cancelling: " : "") + reason, RequestedAt = now });
                NotifyStaff("payment", "Walk-in refund to review");
                Audit("refund.request", "refund_requests", asked.Id.ToString());
                result = Clone(asked);
                return true;
            }
            case "decide_walk_in_refund":   // 0096
            {
                Require("payments:void");
                var reason = (a.GetValueOrDefault("p_reason") as string)?.Trim();
                if (string.IsNullOrEmpty(reason) || reason.Length > 1000 || a.GetValueOrDefault("p_approve") is null) throw Err("bad_input");
                var approve = Convert.ToBoolean(a["p_approve"]);
                var f = All<RefundRequest>().FirstOrDefault(x => x.Id == I("p_request_id") && x.IsWalkInRequest) ?? throw Err("not_found");
                if (f.Status != "pending") throw Err("already_processed");
                if (All<Payment>().First(p => p.Id == f.PaymentId).Status != Status.Verified) throw Err("not_refundable");
                if (approve && All<Reservation>().FirstOrDefault(r => r.Id == f.ReservationId && r.Status == "confirmed") is { } booking)
                { booking.Status = "cancelled"; booking.CancelledAt = now; booking.CancelledBy = Uid; booking.CancelReason = "Refund approved"; }
                f.Status = approve ? "approved" : "rejected"; f.DecidedBy = Uid; f.DecidedAt = now; f.DecisionReason = reason;
                Audit("refund.decide", "refund_requests", f.Id.ToString());
                return true;
            }
            case "free_coaches":   // G14 (0064)
            {
                RequireActive();
                var amenityId = I("p_amenity");
                DateTime T(string k) => DateTime.Parse((string)a[k], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                var (start, end) = (T("p_start"), T("p_end"));
                if (end <= start) throw Err("bad_input");
                result = All<CoachAmenity>().Where(c => c.AmenityId == amenityId && CoachIsFree(c.CoachId, amenityId, start, end))
                    .Select(c => All<Profile>().First(p => p.Id == c.CoachId))
                    .Select(p => new FreeCoach { CoachId = p.Id, Name = string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, AvatarPath = p.AvatarPath })
                    .OrderBy(c => c.Name).ToList();
                return true;
            }
            case "record_booking_balance":   // G13 (0064): the rest, at the desk
            {
                Require("payments:record");
                var method = a.GetValueOrDefault("p_method") as string;
                var reference = (a.GetValueOrDefault("p_reference") as string)?.Trim();
                if (method is not (PayMethod.Cash or PayMethod.Gcash) || (method == PayMethod.Gcash && string.IsNullOrEmpty(reference))) throw Err("bad_input");
                var r = All<Reservation>().FirstOrDefault(x => x.Id == I("p_reservation_id")) ?? throw Err("not_found");
                if (r.Status != "confirmed") throw Err("not_confirmed");
                if (r.BalancePaymentId is not null || r.Balance <= 0 || r.UserAmenityId is null) throw Err("already_processed");
                int? shift = method == PayMethod.Cash ? OpenShiftOf(Uid!) : null;
                var pay = Add(new Payment { PaymentCode = PaymentCode(), Kind = PayKind.Amenity, Method = method, Status = Status.Verified, Amount = r.Balance, MemberId = r.MemberId,
                    UserAmenityId = r.UserAmenityId, ReferenceNo = string.IsNullOrEmpty(reference) ? null : reference, RecordedBy = Uid, VerifiedBy = Uid, VerifiedAt = now,
                    ReceiptNo = ReceiptNo(), CreatedAt = now, CashShiftId = shift });
                r.BalancePaymentId = pay.Id;
                Audit("reservation.balance", "reservations", r.Id.ToString());
                result = Clone(pay);
                return true;
            }
            case "set_runs_amenity":   // F14 (0052)
            {
                RequireActive();
                if (!IsCoach(Uid!)) throw Err("forbidden");
                var amenityId = I("p_amenity");
                if (a.GetValueOrDefault("p_on") is null || !All<Amenity>().Any(x => x.Id == amenityId && x.State == CatalogState.Published)) throw Err("not_found");
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
