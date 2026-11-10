using System.Globalization;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

// ===================== F16: the member menu, by membership state =====================
/// <summary>One menu entry: what it is called and which page it opens (Packages reads "Renew or change" once a member is active).</summary>
public sealed record MenuItem(string Title, string Page, bool MembersOnly = false);

/// <summary>
/// The member menu in one place, so Windows and the phone read the same list (F16, 2026-11-10). With an active membership the
/// everyday pages lead and Packages sits near the end as "Renew or change"; without one Packages comes first. Members-only pages stay
/// listed and open their locked notice. The phone keeps the first three as tabs, plus Messages; the rest go under More.
/// </summary>
public static class MemberMenu
{
    static readonly MenuItem[] Everyday =
    [
        new("Calendar", "Calendar", true), new("Sessions", "Sessions", true), new("Book an amenity", "Book an amenity", true),
        new("Coaches", "Coaches"), new("Time requests", "Time requests", true), new("Messages", "Messages")
    ];
    static readonly MenuItem Membership = new("My membership", "My membership"), Settings = new("Settings", "Settings");

    public static IReadOnlyList<MenuItem> Order(bool active) => active
        ? [Membership, .. Everyday, new("Renew or change", "Packages"), Settings]
        : [new("Packages", "Packages"), Membership, .. Everyday, Settings];

    public static IReadOnlyList<MenuItem> Tabs(bool active) => [.. Order(active).Take(3), Everyday.Single(i => i.Page == "Messages")];
}

// ===================== F15: the member calendar =====================
/// <summary>Something on a member's calendar: their session, amenity booking, or their coach's shift or open time (Kind session, booking, shift, open).</summary>
public sealed record CalendarEntry(DateTime StartUtc, DateTime EndUtc, string Kind, string Title, string? CoachId = null)
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static DateTime Manila(DateTime utc) => utc.ToUniversalTime().AddHours(8);
    public string Time => $"{Manila(StartUtc).ToString("h:mm tt", En)} to {Manila(EndUtc).ToString("h:mm tt", En)}";
    public string KindText => Kind switch { "session" => "Session", "booking" => "Amenity booking", "shift" => "Coach on the floor", _ => "Open time" };
    /// <summary>The word a day tile has room for; the day's list carries the full title.</summary>
    public string Short => Kind switch { "session" => "Session", "shift" => "Shift", "open" => "Open slot", _ => Title };
    /// <summary>F15: tapping a coach's open time asks for it, with the same rules as Time requests.</summary>
    public bool CanRequest => Kind == "open" && StartUtc > DateTime.UtcNow;
    public bool CanOpen => Kind == "booking";
}

/// <summary>A day on the member calendar, in the coach Dashboard's style: two entries named in a word on Windows (times are in the day's list), then "+n more"; a dot per kind.</summary>
public sealed record MemberDay(DateOnly Date, bool InMonth, bool IsToday, bool IsSelected, string? Entry1, string? Entry2, string? More,
    bool HasSession, bool HasBooking, bool HasOpen)
{
    public string Number => Date.Day.ToString(CultureInfo.InvariantCulture);
    public bool Dimmed => !InMonth;
    /// <summary>What Entry1 and Entry2 are (session, booking, shift, open), for their dots.</summary>
    public string? Kind1 { get; init; }
    public string? Kind2 { get; init; }
    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (HasSession) parts.Add("a session");
            if (HasBooking) parts.Add("an amenity booking");
            if (HasOpen) parts.Add("your coach has open time");
            return Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture) + (IsToday ? ", today" : "") + (parts.Count == 0 ? "" : ", " + string.Join(", ", parts));
        }
    }
}

public static class MemberCalendar
{
    static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(utc.ToUniversalTime().AddHours(8));   // Manila

    public static IReadOnlyList<MemberDay> Days(DateOnly month, IReadOnlyList<CalendarEntry> entries, DateOnly today, DateOnly? selected)
    {
        var byDay = entries.GroupBy(e => Day(e.StartUtc)).ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartUtc).ToList());
        return AttendanceCalendar.MonthGrid(month.Year, month.Month).Select(d =>
        {
            var on = byDay.GetValueOrDefault(d) ?? [];
            return new MemberDay(d, d.Month == month.Month, d == today, d == selected, on.Count > 0 ? on[0].Short : null, on.Count > 1 ? on[1].Short : null,
                on.Count > 2 ? $"+{on.Count - 2} more" : null, on.Any(e => e.Kind == "session"), on.Any(e => e.Kind == "booking"), on.Any(e => e.Kind == "open"))
            { Kind1 = on.ElementAtOrDefault(0)?.Kind, Kind2 = on.ElementAtOrDefault(1)?.Kind };
        }).ToList();
    }
}

/// <summary>What the calendar shows for a stretch of time, whether the member has a coach, and the gym's hours for one who hasn't.</summary>
public sealed record MemberCalendarData(IReadOnlyList<CalendarEntry> Entries, bool HasCoach, TimeSpan Opens, TimeSpan Closes);

public interface IMemberCalendarService
{
    /// <summary>The signed-in member's sessions and amenity bookings, and their hired coaches' shifts and open times, starting in [fromUtc, toUtc).</summary>
    Task<AppResult<MemberCalendarData>> RangeAsync(DateTime fromUtc, DateTime toUtc);
}

public sealed class MemberCalendarService(ISupabaseGateway gw) : IMemberCalendarService
{
    public Task<AppResult<MemberCalendarData>> RangeAsync(DateTime fromUtc, DateTime toUtc) => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        bool In(DateTime t) => t.ToUniversalTime() >= fromUtc && t.ToUniversalTime() < toUtc;
        var names = await Directory_.UserNamesAsync(gw);
        string Coach(string id) => AttendanceCalendar.ShortName(names.GetValueOrDefault(id, "your coach"));
        var coaches = (await gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active")).Select(h => h.CoachId).ToHashSet();
        var amenities = (await gw.ListAsync<Amenity>()).ToDictionary(a => a.Id, a => a.Name);
        var entries = new List<CalendarEntry>();
        entries.AddRange((await gw.ListAsync<TrainingSession>(s => s.MemberId == uid)).Where(s => s.Status != Status.Cancelled && In(s.ScheduledStart))
            .Select(s => new CalendarEntry(s.ScheduledStart.ToUniversalTime(), s.ScheduledEnd.ToUniversalTime(), "session", $"Session with {Coach(s.CoachId)}", s.CoachId)));
        // a booking made for a coaching session is that session; only the member's own bookings show on their own
        entries.AddRange((await gw.ListAsync<Reservation>(r => r.MemberId == uid)).Where(r => r.Status is "confirmed" or "awaiting_payment" && r.TrainingSessionId is null && In(r.StartsAt))
            .Select(r => new CalendarEntry(r.StartsAt.ToUniversalTime(), r.EndsAt.ToUniversalTime(), "booking", amenities.GetValueOrDefault(r.AmenityId, "Amenity"))));
        if (coaches.Count > 0)
        {
            entries.AddRange((await gw.ListAsync<ShiftPost>()).Where(p => coaches.Contains(p.CoachId) && p.Status == Status.Scheduled && In(p.Start))
                .Select(p => new CalendarEntry(p.Start.ToUniversalTime(), p.End.ToUniversalTime(), "shift", $"{Coach(p.CoachId)} on the floor", p.CoachId)));
            entries.AddRange((await gw.ListAsync<TimeRequest>(r => r.Status == "pending")).Where(r => r.MemberId is null && coaches.Contains(r.CoachId) && In(r.RequestedStart))
                .Select(r => new CalendarEntry(r.RequestedStart.ToUniversalTime(), r.RequestedEnd.ToUniversalTime(), "open", $"Open with {Coach(r.CoachId)}", r.CoachId)));
        }
        var gym = (await gw.ListAsync<GymSettings>()).First();
        return new MemberCalendarData(entries.OrderBy(e => e.StartUtc).ToList(), coaches.Count > 0, gym.OpensAt, gym.ClosesAt);
    });
}
