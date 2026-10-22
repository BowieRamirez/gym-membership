using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

// 0029_reservations.sql in memory: one unit per amenity, a 30-minute cooldown after each use, touching slots = one booking,
// all or nothing, an active pass for solo bookings. Runs under the RPC lock, so checks and writes cannot interleave.
public sealed partial class DemoGateway
{
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30), Manila = TimeSpan.FromHours(8);
    IEnumerable<Reservation> Confirmed => All<Reservation>().Where(r => r.Status == "confirmed");

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
                var made = new List<(int Amenity, DateTime Start, DateTime End)>();
                foreach (var b in bookings)
                {
                    if (b.Count > 4) throw Err("too_long");
                    if (b.Start <= now) throw Err("past_slot");
                    var day = (b.Start + Manila).Date;
                    if (day > (now + Manila).Date.AddDays(14)) throw Err("too_far_ahead");
                    if (b.Start < day - Manila + g.OpensAt || b.End > day - Manila + g.ClosesAt) throw Err("outside_gym_hours");
                    var am = All<Amenity>().FirstOrDefault(x => x.Id == b.Amenity && x.IsActive) ?? throw Err("not_found");
                    if (am.Availability == "unavailable" && (am.BackAt is null || am.BackAt > b.Start)) throw Err("amenity_unavailable");
                    if (!All<UserAmenity>().Any(p => p.MemberId == Uid && p.AmenityId == b.Amenity && p.Status == Status.Active && (p.ExpiresAt is null || p.ExpiresAt >= b.End))) throw Err("no_pass");
                    var others = Confirmed.Select(r => (AmenityId: r.AmenityId, MemberId: r.MemberId, Start: r.StartsAt, End: r.EndsAt))
                        .Concat(made.Select(m => (AmenityId: m.Amenity, MemberId: Uid!, Start: m.Start, End: m.End))).ToList();
                    if (others.Any(o => o.MemberId == Uid && o.Start < b.End && b.Start < o.End)
                        || All<TrainingSession>().Any(t => t.MemberId == Uid && t.Status == Status.Scheduled && t.ScheduledStart < b.End && b.Start < t.ScheduledEnd)) throw Err("member_busy");
                    if (others.Any(o => o.AmenityId == b.Amenity && o.Start < b.End + Cooldown && b.Start < o.End + Cooldown)) throw Err("slot_taken");
                    made.Add((b.Amenity, b.Start, b.End));
                }
                result = made.Select(m =>
                {
                    var r = Add(new Reservation { MemberId = Uid!, AmenityId = m.Amenity, StartsAt = m.Start, EndsAt = m.End });
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
                if (r.Status != "confirmed") throw Err("already_processed");
                if (r.StartsAt <= now) throw Err("already_started");
                r.Status = "cancelled"; r.CancelledAt = now; r.CancelledBy = Uid; r.CancelReason = string.IsNullOrEmpty(reason) ? null : reason;
                if (r.MemberId != Uid) Notify(r.MemberId, "amenity", "Booking cancelled", reason);
                Audit("reservation.cancel", "reservations", r.Id.ToString());
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
                Notify(r.MemberId, "amenity", "Session extended", (end + Manila).ToString("HH:mm"));
                Audit("reservation.extend", "reservations", r.Id.ToString());
                return true;
            }
            default: return false;
        }
    }
}
