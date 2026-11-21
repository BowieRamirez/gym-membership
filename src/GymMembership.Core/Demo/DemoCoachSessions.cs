using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

// 0120_book_coach_session.sql in memory: free_coaches, book_coach_session, desk_book_coach_session, 15-min held add-on sessions, and add-on refunds.
public sealed partial class DemoGateway
{
    bool CoachSessionCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        var now = SubscriptionNow;
        var uid = Uid;
        int I(string k) => Convert.ToInt32(a[k]);
        string S(string k) => (string)a[k];
        string? Opt(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        DateTime T(string k) => DateTime.Parse(S(k), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

        switch (fn)
        {
            case "free_coaches" when !a.ContainsKey("p_amenity"):
            {
                RequireActive();
                var day = DateOnly.Parse(S("p_day"), System.Globalization.CultureInfo.InvariantCulture);
                if (All<GymClosure>().Any(c => c.Day == day)) { result = new List<FreeCoachSlot>(); return true; }
                var g = All<GymSettings>().First();
                var dayStart = day.ToDateTime(TimeOnly.MinValue) - Manila + g.OpensAt;
                var dayEnd = day.ToDateTime(TimeOnly.MinValue) - Manila + g.ClosesAt;
                var maxH = g.MaxCoachHoursPerDay;
                var profiles = All<Profile>().ToDictionary(p => p.Id);
                var list = new List<FreeCoachSlot>();
                foreach (var c in All<Coach>())
                {
                    if (!profiles.TryGetValue(c.UserId, out var pr) || !pr.IsActive) continue;
                    var bookedHrs = All<TrainingSession>()
                        .Where(ts => ts.CoachId == c.UserId && ts.Status is Status.Scheduled or Status.Completed
                            && (ts.HeldUntil is null || ts.HeldUntil > now)
                            && DateOnly.FromDateTime((ts.ScheduledStart.ToUniversalTime() + Manila).Date) == day)
                        .Sum(ts => (ts.ScheduledEnd - ts.ScheduledStart).TotalHours);
                    if (maxH is { } mh && bookedHrs >= mh) continue;
                    var free = new List<int>();
                    for (var h = 0; h < 24; h++)
                    {
                        var slotStart = day.ToDateTime(TimeOnly.MinValue).AddHours(h) - Manila;
                        var slotEnd = slotStart.AddHours(1);
                        if (slotStart <= now || slotStart < dayStart || slotEnd > dayEnd) continue;
                        if (!All<ShiftPost>().Any(sp => sp.CoachId == c.UserId && sp.Status == Status.Scheduled && sp.Start <= slotStart && sp.End >= slotEnd)) continue;
                        var busy = All<TrainingSession>().Any(ts => ts.CoachId == c.UserId && ts.Status == Status.Scheduled
                            && (ts.HeldUntil is null || ts.HeldUntil > now) && ts.ScheduledStart < slotEnd && slotStart < ts.ScheduledEnd);
                        if (!busy) free.Add(h);
                    }
                    if (free.Count > 0)
                        list.Add(new FreeCoachSlot(c.UserId, string.IsNullOrWhiteSpace(pr.FullName) ? pr.Username : pr.FullName, c.Specialty, c.Bio, pr.AvatarPath, [.. free]));
                }
                result = list.OrderBy(x => x.FullName).ToList();
                return true;
            }
            case "book_coach_session":
            {
                RequireActive();
                result = BookCoachCore(uid, S("p_coach"), T("p_start"), I("p_hours"), PayMethod.Qr, Opt("p_code"), skipE6: false, recordedBy: null, now);
                return true;
            }
            case "desk_book_coach_session":
            {
                Require("payments:record");
                var memberId = S("p_member");
                if (All<Member>().All(m => m.UserId != memberId)) throw Err("not_found");
                var method = S("p_method");
                if (method is not ("cash" or "qr" or "online")) throw Err("bad_input");
                result = BookCoachCore(memberId, S("p_coach"), T("p_start"), I("p_hours"), method == "online" ? PayMethod.Qr : method, Opt("p_code"), skipE6: true, recordedBy: uid, now);
                return true;
            }
            case "desk_book_walkin_coach_session":
            {
                Require("payments:record");
                var coachId = S("p_coach");
                var start = T("p_start");
                var hours = I("p_hours");
                if (hours is not (1 or 2) || start <= now) throw Err("bad_input");
                var end = start.AddHours(hours);
                if (!All<ShiftPost>().Any(sp => sp.CoachId == coachId && sp.Status == Status.Scheduled && sp.Start <= start && sp.End >= end)) throw Err("coach_not_free");
                if (All<TrainingSession>().Any(ts => ts.CoachId == coachId && ts.Status == Status.Scheduled && (ts.HeldUntil is null || ts.HeldUntil > now) && ts.ScheduledStart < end && start < ts.ScheduledEnd)) throw Err("coach_taken");
                var s = Add(new TrainingSession { CoachId = coachId, MemberId = "", WalkInName = Opt("p_walk_in_name"), ScheduledStart = start, ScheduledEnd = end, Status = Status.Scheduled });
                result = s.Id;
                return true;
            }
        }
        return false;
    }

    BookCoachResult BookCoachCore(string memberId, string coachId, DateTime start, int hours, string method, string? code, bool skipE6, string? recordedBy, DateTime now)
    {
        ExpireCoachHolds(now);
        if (string.IsNullOrWhiteSpace(coachId) || hours is not (1 or 2)) throw Err("bad_input");
        if ((start.ToUniversalTime() - DateTime.UnixEpoch).Ticks % TimeSpan.TicksPerHour != 0) throw Err("off_grid");
        if (start <= now) throw Err("past_slot");
        var end = start.AddHours(hours);
        var day = DateOnly.FromDateTime((start.ToUniversalTime() + Manila).Date);
        if (All<GymClosure>().Any(c => c.Day == day)) throw Err("gym_closed");
        var g = All<GymSettings>().First();
        var dayStart = day.ToDateTime(TimeOnly.MinValue) - Manila + g.OpensAt;
        var dayEnd = day.ToDateTime(TimeOnly.MinValue) - Manila + g.ClosesAt;
        if (start < dayStart || end > dayEnd) throw Err("outside_gym_hours");

        if (!All<Coach>().Any(c => c.UserId == coachId) || All<Profile>().FirstOrDefault(p => p.Id == coachId)?.IsActive != true) throw Err("not_found");
        if (!All<ShiftPost>().Any(sp => sp.CoachId == coachId && sp.Status == Status.Scheduled && sp.Start <= start && sp.End >= end)) throw Err("coach_not_free");

        var term = All<MembershipTerm>()
            .Where(t => t.MemberId == memberId && t.CancelledAt is null && t.StartsAt <= start && t.EndsAt >= end)
            .OrderByDescending(t => t.EndsAt).FirstOrDefault();
        // Fallback for demo seeded UserMembershipPackage without MembershipTerm
        int? pkgId = term?.PackageId ?? All<UserMembershipPackage>()
            .Where(u => u.MemberId == memberId && u.Status == Status.Active && u.StartsAt <= start && u.EndsAt >= end)
            .Select(u => (int?)u.MembershipPackageId).FirstOrDefault();
        if (pkgId is null) throw Err("outside_membership");

        var pkg = All<MembershipPackage>().First(p => p.Id == pkgId.Value);
        if (!pkg.IncludesCoach && pkg.CoachAddonPrice is null) throw Err("coach_not_offered");

        var bookedHrs = All<TrainingSession>()
            .Where(ts => ts.CoachId == coachId && ts.Status is Status.Scheduled or Status.Completed
                && DateOnly.FromDateTime((ts.ScheduledStart.ToUniversalTime() + Manila).Date) == day)
            .Sum(ts => (ts.ScheduledEnd - ts.ScheduledStart).TotalHours);
        if (g.MaxCoachHoursPerDay is { } maxH && bookedHrs + hours > maxH) throw Err("coach_day_full");

        if (pkg.IncludesCoach)
        {
            if (!skipE6)
            {
                var recentNoShows = All<TrainingSession>().Where(ts => ts.MemberId == memberId && ts.Status == Status.NoShow && !ts.IsPaidAddon && ts.ScheduledStart >= now.AddDays(-30)).ToList();
                if (recentNoShows.Count >= 2 && recentNoShows.Max(ts => ts.ScheduledStart).AddDays(7) > now)
                    throw Err("coach_booking_blocked");
                var cntDay = All<TrainingSession>().Count(ts => ts.MemberId == memberId && !ts.IsPaidAddon && ts.Status is Status.Scheduled or Status.Completed
                    && DateOnly.FromDateTime((ts.ScheduledStart.ToUniversalTime() + Manila).Date) == day);
                if (cntDay >= g.MaxCoachSessionsPerDay) throw Err("too_many_coach_sessions");
                var cntUp = All<TrainingSession>().Count(ts => ts.MemberId == memberId && !ts.IsPaidAddon && ts.Status == Status.Scheduled && (ts.HeldUntil is null || ts.HeldUntil > now));
                if (cntUp >= g.MaxUpcomingCoachSessions) throw Err("too_many_coach_sessions");
            }
            if (All<TrainingSession>().Any(ts => ts.MemberId == memberId && ts.Status == Status.Scheduled && ts.ScheduledStart < end && start < ts.ScheduledEnd)) throw Err("member_busy");
            if (All<TrainingSession>().Any(ts => ts.CoachId == coachId && ts.Status == Status.Scheduled && ts.ScheduledStart < end && start < ts.ScheduledEnd)) throw Err("coach_taken");

            var ses = Add(new TrainingSession { CoachId = coachId, MemberId = memberId, Title = "Coach session", ScheduledStart = start, ScheduledEnd = end, Status = Status.Scheduled, IsPaidAddon = false });
            Audit("coach_session.book", "training_sessions", ses.Id.ToString());
            return new BookCoachResult(ses.Id, null, null, 0m, null, true);
        }

        // Paid add-on
        var basePrice = pkg.CoachAddonPrice!.Value * hours;
        var disc = code is null ? (Id: (int?)null, Cut: 0m, Amount: basePrice) : Discounted(memberId, new() { ["p_discount_code"] = code }, basePrice, "coach", 0);
        var verified = method == PayMethod.Cash || disc.Amount == 0m;
        DateTime? held = verified ? null : now.AddMinutes(15);

        if (All<TrainingSession>().Any(ts => ts.MemberId == memberId && ts.Status == Status.Scheduled && ts.ScheduledStart < end && start < ts.ScheduledEnd)) throw Err("member_busy");
        if (All<TrainingSession>().Any(ts => ts.CoachId == coachId && ts.Status == Status.Scheduled && ts.ScheduledStart < end && start < ts.ScheduledEnd)) throw Err("coach_taken");

        var pay = Add(new Payment
        {
            PaymentCode = PaymentCode(),
            Kind = PayKind.CoachAddon,
            Method = method,
            Status = verified ? Status.Verified : Status.Pending,
            Amount = disc.Amount,
            DiscountId = disc.Id,
            DiscountAmount = disc.Cut,
            MemberId = memberId,
            ExpiresAt = held,
            RecordedBy = recordedBy,
            VerifiedBy = verified ? (recordedBy ?? memberId) : null,
            VerifiedAt = verified ? now : null,
            ReceiptNo = verified ? ReceiptNo() : null,
            PackageSnapshotId = pkg.Id,
            PackageSnapshotName = pkg.Name,
            CreatedAt = now
        });
        var session = Add(new TrainingSession
        {
            CoachId = coachId, MemberId = memberId, Title = "Coach session",
            ScheduledStart = start, ScheduledEnd = end, Status = Status.Scheduled,
            PaymentId = pay.Id, HeldUntil = held, IsPaidAddon = true
        });
        Audit("coach_session.book", "training_sessions", session.Id.ToString());
        return new BookCoachResult(session.Id, pay.Id, pay.PaymentCode, disc.Amount, held, false);
    }

    void ExpireCoachHolds(DateTime now)
    {
        foreach (var p in All<Payment>().Where(p => p.Kind == PayKind.CoachAddon && p.Status == Status.Pending && p.ExpiresAt <= now))
        {
            p.Status = Status.Expired;
            foreach (var ts in All<TrainingSession>().Where(s => s.PaymentId == p.Id && s.Status == Status.Scheduled && s.HeldUntil is not null))
                ts.Status = Status.Cancelled;
        }
    }

    TodayCoachSession ToTodayCoachSession(TrainingSession s)
    {
        var names = All<Profile>().ToDictionary(p => p.Id, p => p.FullName);
        var st = s.HeldUntil is not null ? "Waiting for payment"
            : s.Status == Status.Scheduled && s.CoachArrivedAt is not null ? "In session"
            : s.Status == Status.Scheduled && s.CheckedInAt is not null ? "Checked in"
            : s.Status;
        return new TodayCoachSession(s.Id, s.SessionCode ?? $"SES-{s.Id:D4}", s.MemberId, names.GetValueOrDefault(s.MemberId, "Member"),
            s.CoachId, names.GetValueOrDefault(s.CoachId, "Coach"), s.ScheduledStart, s.ScheduledEnd, st, s.CheckedInAt, s.CoachArrivedAt, s.HeldUntil);
    }

    bool SessionCheckInCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        var now = SubscriptionNow;
        switch (fn)
        {
            case "scan_session":
            {
                RequireActive();
                if (!IsStaff) throw Err("forbidden");
                var code = ((string)a["p_code"]).Trim();
                var s = All<TrainingSession>().FirstOrDefault(x => string.Equals(x.SessionCode ?? $"SES-{x.Id:D4}", code, StringComparison.OrdinalIgnoreCase))
                    ?? throw Err("not_found");
                if (s.Status == Status.Cancelled) throw Err("session_cancelled");
                if (s.Status is Status.Completed or Status.NoShow) throw Err("already_processed");
                if (s.HeldUntil is not null) throw Err("payment_required");
                if (s.ScheduledStart.AddHours(8).Date != now.AddHours(8).Date) throw Err("wrong_day");
                s.CheckedInAt ??= now;
                result = new List<TodayCoachSession> { ToTodayCoachSession(s) };
                return true;
            }
            case "mark_coach_arrived":
            {
                RequireActive();
                if (!IsStaff) throw Err("forbidden");
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == Convert.ToInt32(a["p_session"])) ?? throw Err("not_found");
                if (s.Status != Status.Scheduled) throw Err("already_processed");
                if (s.HeldUntil is not null) throw Err("payment_required");
                if (s.CheckedInAt is null) throw Err("not_checked_in");
                s.CoachArrivedAt ??= now;
                return true;
            }
            case "today_coach_sessions":
            {
                RequireActive();
                if (!IsStaff) throw Err("forbidden");
                var today = now.AddHours(8).Date;
                result = All<TrainingSession>()
                    .Where(s => s.Status != Status.Cancelled && (s.HeldUntil is null || s.HeldUntil > now) && s.ScheduledStart.AddHours(8).Date == today)
                    .OrderBy(s => s.ScheduledStart)
                    .Select(ToTodayCoachSession)
                    .ToList();
                return true;
            }
            case "report_damage":
            {
                RequireActive();
                var desc = a.TryGetValue("p_description", out var dv) && dv is string ds ? ds.Trim() : "";
                if (desc.Length == 0) throw Err("bad_input");
                int? sid = a.TryGetValue("p_session_id", out var sv) && sv is not null ? Convert.ToInt32(sv) : null;
                int? aid = a.TryGetValue("p_amenity_id", out var av) && av is not null ? Convert.ToInt32(av) : null;
                string? coachId = null;
                if (sid is { } sessionId)
                {
                    var s = All<TrainingSession>().FirstOrDefault(x => x.Id == sessionId) ?? throw Err("not_found");
                    if (!IsStaff && s.CoachId != Uid) throw Err("forbidden");
                    coachId = s.CoachId;
                }
                else if (!IsStaff) throw Err("forbidden");
                var rep = Add(new DamageReport
                {
                    TrainingSessionId = sid, AmenityId = aid, CoachId = coachId, ReportedBy = Uid,
                    Description = desc, PhotoPath = a.TryGetValue("p_photo_path", out var pv) ? pv as string : null,
                    CreatedAt = now
                });
                foreach (var adm in All<UserRole>().Where(u => RolesOf(u.UserId).Any(r => r is "admin" or "subadmin")).Select(u => u.UserId).Distinct().ToList())
                    Notify(adm, "system", "Damage report filed", desc.Length > 120 ? desc[..120] : desc);
                Add(new AuditEntry { ActorId = Uid, Action = "damage_report.create", Entity = "damage_reports", EntityId = rep.Id.ToString(), CreatedAt = now });
                result = rep.Id;
                return true;
            }
            case "review_damage":
            {
                RequireActive();
                if (!RolesOf(Uid).Any(r => r is "admin" or "subadmin")) throw Err("forbidden");
                var status = (string)a["p_status"];
                if (status is not ("reviewed" or "closed")) throw Err("bad_input");
                var rep = All<DamageReport>().FirstOrDefault(r => r.Id == Convert.ToInt32(a["p_report_id"])) ?? throw Err("not_found");
                rep.Status = status;
                if (a.TryGetValue("p_note", out var nv) && nv is string ns && !string.IsNullOrWhiteSpace(ns)) rep.ReviewNote = ns.Trim();
                rep.ReviewedBy = Uid;
                rep.ReviewedAt = now;
                if (a.TryGetValue("p_set_maintenance", out var mv) && Convert.ToBoolean(mv) && rep.AmenityId is { } amId)
                {
                    CallLocked("set_amenity_status", new() { ["p_amenity_id"] = amId, ["p_status"] = "unavailable", ["p_reason"] = rep.ReviewNote ?? "Maintenance" });
                }
                Add(new AuditEntry { ActorId = Uid, Action = "damage_report.review", Entity = "damage_reports", EntityId = rep.Id.ToString(), CreatedAt = now });
                return true;
            }
            case "coach_hours_report":
            {
                RequireActive();
                if (!RolesOf(Uid).Any(r => r is "admin" or "subadmin")) throw Err("forbidden");
                var from = DateOnly.Parse((string)a["p_from"]);
                var to = DateOnly.Parse((string)a["p_to"]);
                if (to < from) throw Err("bad_input");
                var names = All<Profile>().ToDictionary(p => p.Id, p => p.FullName);
                result = All<Coach>().OrderBy(c => names.GetValueOrDefault(c.UserId, "Coach")).Select(c =>
                {
                    var ses = All<TrainingSession>().Where(s =>
                        s.CoachId == c.UserId
                        && (s.HeldUntil is null || s.HeldUntil > now)
                        && DateOnly.FromDateTime(s.ScheduledStart.AddHours(8)) >= from
                        && DateOnly.FromDateTime(s.ScheduledStart.AddHours(8)) <= to).ToList();
                    var completed = ses.Where(s => s.Status == Status.Completed).ToList();
                    var hrs = completed.Sum(s => (decimal)(s.ScheduledEnd - s.ScheduledStart).TotalHours);
                    return new CoachHoursRow(
                        c.UserId,
                        names.GetValueOrDefault(c.UserId, "Coach"),
                        completed.Count,
                        Math.Round(hrs, 2),
                        ses.Count(s => s.Status == Status.NoShow),
                        ses.Count(s => s.Status == Status.Cancelled));
                }).ToList();
                return true;
            }
        }
        return false;
    }
}
