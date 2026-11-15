using System.Globalization;
using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

// 0065_gym_closures.sql in memory (G16-G18): closed days, the one check every set-up time goes through, and close, reopen, preview.
public sealed partial class DemoGateway
{
    static DateOnly ManilaDay(DateTime t) => DateOnly.FromDateTime(t.ToUniversalTime().AddHours(8));

    /// <summary>refuse_closed_day (0065): nothing is set up on a closed day.</summary>
    void RefuseClosed(DateTime? t)
    {
        if (t is { } at && All<GymClosure>().Any(c => c.Day == ManilaDay(at))) throw Err("day_closed");
    }

    void RefuseClosedRow(object row)
    {
        switch (row)
        {
            case Reservation r: RefuseClosed(r.StartsAt); break;
            case ShiftPost s: RefuseClosed(s.Start); break;
            case TrainingSession t: RefuseClosed(t.ScheduledStart); break;
            case TimeRequest q: RefuseClosed(q.RequestedStart); RefuseClosed(q.ProposedStart); break;
            case GymCheckIn c: RefuseClosed(c.CheckedInAt); break;
        }
    }

    bool ClosureCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        if (fn is not ("close_gym_day" or "reopen_gym_day" or "closure_preview")) return false;
        RequireActive();
        if (!RolesOf(Uid).Any(r => r is "admin" or "subadmin")) throw Err("forbidden");
        var day = DateOnly.Parse((string)a["p_date"], CultureInfo.InvariantCulture);
        if (day < ManilaDay(SubscriptionNow)) throw Err("day_past");
        var now = SubscriptionNow;
        var bookings = All<Reservation>().Where(r => r.Status is "confirmed" or "awaiting_payment" && ManilaDay(r.StartsAt) == day && r.EndsAt > now).ToList();
        var sessions = All<TrainingSession>().Where(t => t.Status == Status.Scheduled && ManilaDay(t.ScheduledStart) == day && t.ScheduledEnd > now).ToList();
        var shifts = All<ShiftPost>().Where(s => s.Status == Status.Scheduled && ManilaDay(s.Start) == day && s.End > now).ToList();
        var requests = All<TimeRequest>().Where(q => q.Status is "pending" or "proposed" && ManilaDay(q.Status == "proposed" ? q.ProposedStart!.Value : q.RequestedStart) == day).ToList();
        Payment? Pay(int? id) => id is null ? null : All<Payment>().FirstOrDefault(p => p.Id == id);
        bool Paid(Reservation r) => r.Status == "confirmed" && (Pay(r.PaymentId)?.Status == Status.Verified || r.BalancePaymentId is not null);
        string When(string text) => string.Format(CultureInfo.InvariantCulture, text, day.ToString("dddd, d MMM", CultureInfo.InvariantCulture));
        void Announce(string body) => MessengerCall("post_announcement", new() { ["p_body"] = body, ["p_audience"] = "everyone" }, out _);

        switch (fn)
        {
            case "closure_preview":
                result = new Services.ClosurePreview(bookings.Count, bookings.Count(Paid), sessions.Count, shifts.Count, requests.Count);
                return true;
            case "reopen_gym_day":
            {
                var c = All<GymClosure>().FirstOrDefault(x => x.Day == day) ?? throw Err("not_found");
                Table(typeof(GymClosure)).Remove(c);
                Announce(When("The gym is open again on {0}."));
                Audit("gym.reopen", "gym_closures", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;
            }
        }

        var reason = (a.GetValueOrDefault("p_reason") as string)?.Trim() ?? "";
        if (reason.Length is < 1 or > 200) throw Err("bad_reason");
        if (All<GymClosure>().Any(x => x.Day == day)) throw Err("already_processed");
        Add(new GymClosure { ClosedOn = day.ToDateTime(TimeOnly.MinValue), Reason = reason, ClosedBy = Uid!, ClosedAt = now });
        var why = "Gym closed: " + reason;
        foreach (var r in bookings)
        {
            var pay = Pay(r.PaymentId); var balance = Pay(r.BalancePaymentId)?.Amount ?? 0;
            var was = r.Status;
            r.Status = "cancelled"; r.CancelledAt = now; r.CancelledBy = Uid; r.CancelReason = why;
            if (was == "awaiting_payment" && pay is { Status: Status.Pending }) { pay.Status = Status.Rejected; pay.RejectedReason = "Booking cancelled"; CancelTarget(pay); }
            else if (pay?.Status == Status.Verified || r.BalancePaymentId is not null)
            {
                Add(new RefundRequest { Kind = "cancel", ReservationId = r.Id, PaymentId = pay?.Status == Status.Verified ? pay.Id : r.BalancePaymentId!.Value, MemberId = r.MemberId,
                    Amount = (pay?.Status == Status.Verified ? pay.Amount : 0) + balance, Reason = why, RequestedAt = now, Status = "approved", DecidedBy = Uid, DecidedAt = now,
                    DecisionReason = "Cancelled by the gym: refunded" });
                NotifyStaff("payment", "Booking refund to repay");
            }
            else if (r.UserAmenityId is { } session) foreach (var p in All<UserAmenity>().Where(p => p.Id == session && p.IsSession)) p.Status = Status.Active;
            Notify(r.MemberId, "amenity", "Booking cancelled", why);
        }
        foreach (var t in sessions)
        {
            t.Status = Status.Cancelled;
            foreach (var q in All<TimeRequest>().Where(q => q.SessionId == t.Id && q.Status == Status.Pending)) q.Status = Status.Cancelled;
            Notify(t.MemberId, "system", "A training session was cancelled", why);
            Notify(t.CoachId, "system", "A training session was cancelled", why);
        }
        foreach (var s in shifts) { s.Status = Status.Cancelled; Notify(s.CoachId, "system", "Shift cancelled", why); }
        foreach (var q in requests) { q.Status = "rejected"; q.DeclineReason = why; if (q.MemberId is { } m) Notify(m, "time_request", "Session request declined", why); }
        Announce(When("The gym is closed on {0}: " + reason.TrimEnd('.') + "."));
        Audit("gym.close", "gym_closures", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return true;
    }
}
