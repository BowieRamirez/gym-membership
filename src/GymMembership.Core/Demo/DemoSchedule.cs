using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

// 0151/0153 in memory (owner fixes 2026-11-25, Fix 3): shift templates, assignments, overrides, the coach shifts they write, and the
// check-in switch. Same refusals as the server.
public sealed partial class DemoGateway
{
    bool ScheduleCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        if (!fn.StartsWith("schedule_")) return false;
        Require("reports:revenue");
        string? Str(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        int Int(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToInt32(v) : throw Err("bad_input");
        DateOnly Day(string k) => DateOnly.TryParse(Str(k), out var d) ? d : throw Err("bad_input");
        TimeSpan? Time(string k) => Str(k) is { } s ? TimeSpan.Parse(s) : null;
        var today = ManilaDay(SubscriptionNow);
        var gym = All<GymSettings>().FirstOrDefault() ?? new GymSettings();
        void CheckTimes(TimeSpan? s, TimeSpan? e)
        {
            if (s is not { } st || e is not { } en || en <= st) throw Err("bad_input");
            if (st.Seconds != 0 || en.Seconds != 0 || st.Minutes % 30 != 0 || en.Minutes % 30 != 0) throw Err("off_grid");
            if (st < gym.OpensAt || en > gym.ClosesAt) throw Err("outside_gym_hours");
        }
        void RequirePerson(string? user)
        {
            if (user is null || All<Profile>().FirstOrDefault(p => p.Id == user) is not { IsActive: true } || !RolesOf(user).Any(r => r is "coach" or "employee"))
                throw Err("not_schedulable");
        }
        switch (fn)
        {
            case "schedule_template_save":
            {
                var name = Str("p_name") ?? throw Err("bad_input");
                if (name.Length > 60 || a.GetValueOrDefault("p_days") is not IEnumerable<Dictionary<string, object>> raw) throw Err("bad_input");
                var days = raw.Select(d => (W: Convert.ToInt32(d["weekday"]), S: TimeSpan.Parse((string)d["starts"]), E: TimeSpan.Parse((string)d["ends"]))).ToList();
                foreach (var d in days) { if (d.W is < 1 or > 7) throw Err("bad_input"); CheckTimes(d.S, d.E); }
                if (days.Select(d => d.W).Distinct().Count() != days.Count) throw Err("bad_input");
                var id = a.GetValueOrDefault("p_id") is { } v ? Convert.ToInt32(v) : (int?)null;
                if (All<ScheduleTemplate>().Any(t => t.Name == name && t.Id != id)) throw Err("name_taken");
                ScheduleTemplate tpl;
                if (id is null) tpl = Add(new ScheduleTemplate { Name = name });
                else
                {
                    tpl = All<ScheduleTemplate>().FirstOrDefault(t => t.Id == id && t.ArchivedAt is null) ?? throw Err("not_found");
                    tpl.Name = name;
                    Table(typeof(ScheduleTemplateDay)).RemoveAll(r => ((ScheduleTemplateDay)r).TemplateId == tpl.Id);
                }
                foreach (var d in days) Add(new ScheduleTemplateDay { TemplateId = tpl.Id, Weekday = d.W, StartsAt = d.S, EndsAt = d.E });
                Audit("schedule.template_saved", "schedule_templates", tpl.Id.ToString());
                result = new ScheduleSaveResult { TemplateId = tpl.Id, Kept = SyncCoaches(All<ScheduleAssignment>().Where(x => x.TemplateId == tpl.Id).Select(x => x.UserId), today.AddDays(1), today.AddDays(56)) };
                return true;
            }
            case "schedule_template_duplicate":
            {
                var src = All<ScheduleTemplate>().FirstOrDefault(t => t.Id == Int("p_id")) ?? throw Err("not_found");
                var name = Str("p_name") ?? throw Err("bad_input");
                if (All<ScheduleTemplate>().Any(t => t.Name == name)) throw Err("name_taken");
                var copy = Add(new ScheduleTemplate { Name = name });
                foreach (var d in All<ScheduleTemplateDay>().Where(d => d.TemplateId == src.Id).ToList())
                    Add(new ScheduleTemplateDay { TemplateId = copy.Id, Weekday = d.Weekday, StartsAt = d.StartsAt, EndsAt = d.EndsAt });
                Audit("schedule.template_saved", "schedule_templates", copy.Id.ToString());
                result = copy.Id;
                return true;
            }
            case "schedule_template_archive":
            {
                var tpl = All<ScheduleTemplate>().FirstOrDefault(t => t.Id == Int("p_id") && t.ArchivedAt is null) ?? throw Err("not_found");
                var inUse = All<ScheduleAssignment>().Any(x => x.TemplateId == tpl.Id
                    && (x.From > today || ScheduleNow().CurrentAssignment(x.UserId, today)?.Id == x.Id));
                if (inUse) throw Err("template_in_use");
                tpl.ArchivedAt = SubscriptionNow;
                Audit("schedule.template_archived", "schedule_templates", tpl.Id.ToString());
                return true;
            }
            case "schedule_assign":
            {
                var user = Str("p_user"); RequirePerson(user);
                var from = Day("p_from");
                if (from < today) throw Err("past_date");
                var tpl = All<ScheduleTemplate>().FirstOrDefault(t => t.Id == Int("p_template")) ?? throw Err("not_found");
                if (tpl.ArchivedAt is not null) throw Err("template_archived");
                var row = Add(new ScheduleAssignment { UserId = user!, TemplateId = tpl.Id, EffectiveFrom = from.ToDateTime(TimeOnly.MinValue) });
                Audit("schedule.assigned", "schedule_assignments", row.Id.ToString());
                result = SyncCoaches([user!], from, today.AddDays(56));
                return true;
            }
            case "schedule_override_add":
            {
                var user = Str("p_user"); RequirePerson(user);
                var (from, to, reason) = (Day("p_from"), Day("p_to"), Str("p_reason"));
                if (to < from || to.DayNumber - from.DayNumber > 366 || reason is null || reason.Length > 200) throw Err("bad_input");
                if (from < today) throw Err("past_date");
                var (s, e) = (Time("p_start"), Time("p_end"));
                if (s is not null || e is not null) CheckTimes(s, e);
                var row = Add(new ScheduleOverride { UserId = user!, FromDate = from.ToDateTime(TimeOnly.MinValue), ToDate = to.ToDateTime(TimeOnly.MinValue), StartsAt = s, EndsAt = e, Reason = reason });
                Audit("schedule.override_added", "schedule_overrides", row.Id.ToString());
                result = SyncCoaches([user!], from, to);
                return true;
            }
            case "schedule_override_remove":
            {
                var row = All<ScheduleOverride>().FirstOrDefault(o => o.Id == Int("p_id") && o.From > today) ?? throw Err("not_found");
                Table(typeof(ScheduleOverride)).Remove(row);
                Audit("schedule.override_removed", "schedule_overrides", row.Id.ToString());
                result = SyncCoaches([row.UserId], row.From, row.To);
                return true;
            }
            case "schedule_set_enforced":
            {
                if (a.GetValueOrDefault("p_on") is not bool on) throw Err("bad_input");
                if (All<GymSettings>().FirstOrDefault() is { } g) g.ScheduleEnforced = on;
                Audit("schedule.enforced", "gym_settings", "1");
                return true;
            }
            default: return false;
        }
    }

    // S3/S4 (0151): check-in follows the shift once the switch is on; returns the shift it was checked in against (UTC)
    (DateTime? Start, DateTime? End) ScheduleClockIn(string user)
    {
        var (start, end) = ShiftToday(user);
        if (!(All<GymSettings>().FirstOrDefault()?.ScheduleEnforced ?? false)) return (start, end);
        if (start is not { } st) throw Err("no_shift_today");
        if (SubscriptionNow < st.AddMinutes(-5)) throw Err("too_early:" + st.AddHours(8).ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture));
        return (start, end);
    }

    // 0154: each coach's shift today and whether they are late (checked in after the start, or not in while the shift runs)
    List<CoachShiftToday> CoachShiftsToday()
    {
        Require("check_ins:record");
        var rows = new List<CoachShiftToday>();
        foreach (var c in All<Coach>().Where(c => All<Profile>().Any(p => p.Id == c.UserId && p.IsActive)))
        {
            var (s, e) = ShiftToday(c.UserId);
            if (s is not { } st || e is not { } en) continue;
            var open = All<CoachVisit>().FirstOrDefault(v => v.CoachId == c.UserId && v.CheckedOutAt is null);
            rows.Add(new CoachShiftToday { CoachId = c.UserId, StartsAt = st, EndsAt = en,
                Late = open is not null ? open.CheckedInAt > st : SubscriptionNow > st && SubscriptionNow < en });
        }
        return rows;
    }

    (DateTime? Start, DateTime? End) ShiftToday(string user)
    {
        var today = ManilaDay(SubscriptionNow);
        var (s, e, _, _) = ScheduleNow().ShiftFor(user, today);
        DateTime? At(TimeSpan? t) => t is { } v ? today.ToDateTime(TimeOnly.MinValue).Add(v).AddHours(-8) : null;
        return (At(s), At(e));
    }

    ScheduleData ScheduleNow()
    {
        var gym = All<GymSettings>().FirstOrDefault() ?? new GymSettings();
        return new ScheduleData(All<ScheduleTemplate>().ToList(), All<ScheduleTemplateDay>().ToList(), All<ScheduleAssignment>().ToList(),
            All<ScheduleOverride>().ToList(), All<GymClosure>().ToList(), gym.ScheduleEnforced, gym.OpensAt, gym.ClosesAt);
    }

    // S8: a coach's scheduled shifts from tomorrow follow the schedule; a shift with a booked session inside is kept and its day returned
    List<string> SyncCoaches(IEnumerable<string> users, DateOnly from, DateOnly to)
    {
        var kept = new List<string>();
        var today = ManilaDay(SubscriptionNow);
        var data = ScheduleNow();
        foreach (var coach in users.Distinct().Where(u => RolesOf(u).Contains("coach")))
            for (var day = from > today ? from : today.AddDays(1); day <= to && day <= today.AddDays(56); day = day.AddDays(1))
            {
                var (s, e, _, _) = data.ShiftFor(coach, day);
                DateTime At(TimeSpan t) => day.ToDateTime(TimeOnly.MinValue).Add(t).AddHours(-8);   // Manila wall clock to UTC
                var posts = All<ShiftPost>().Where(p => p.CoachId == coach && p.Status == "scheduled" && ManilaDay(p.Start) == day).ToList();
                if (posts.Count == 1 && s is { } s1 && e is { } e1 && posts[0].Start == At(s1) && posts[0].End == At(e1)) continue;
                var blocked = false;
                foreach (var p in posts)
                    if (All<TrainingSession>().Any(t => t.CoachId == coach && t.Status == "scheduled" && t.ScheduledStart < p.End && p.Start < t.ScheduledEnd)) blocked = true;
                    else p.Status = "cancelled";
                if (blocked) kept.Add(day.ToString("yyyy-MM-dd"));
                else if (s is { } st && e is { } en) Add(new ShiftPost { CoachId = coach, Start = At(st), End = At(en) });
            }
        return kept;
    }
}
