using System.Globalization;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>One of the coach's sessions and how it went. Mark is present, late, absent or excused; null when it has not been marked.</summary>
public sealed record CalendarSession(TrainingSession Session, string With, string? Mark)
{
    public bool Cancelled => Session.Status == Status.Cancelled;
    public bool CanMark => !Cancelled;
    public bool Attended => !Cancelled && Mark is "present" or "late";
    public bool Absent => !Cancelled && Mark == "absent";
    public bool Excused => !Cancelled && Mark == "excused";
    public bool Unmarked => !Cancelled && Mark is null;
}

/// <summary>One day on the calendar: how many of its (not cancelled) sessions were attended, missed, excused or not marked yet.</summary>
public sealed record CalendarDay(DateOnly Date, bool InMonth, bool IsToday, bool IsSelected, int Attended, int Absent, int Excused, int Unmarked)
{
    public string Number => Date.Day.ToString(CultureInfo.InvariantCulture);
    public bool Dimmed => !InMonth;
    public bool HasCheck => Attended > 0;
    public bool HasX => Absent > 0;
    public int Sessions => Attended + Absent + Excused + Unmarked;

    /// <summary>What a screen reader says: the same facts as the check and the X, in words.</summary>
    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (Attended > 0) parts.Add($"{Attended} attended");
            if (Absent > 0) parts.Add($"{Absent} absent");
            if (Excused > 0) parts.Add($"{Excused} excused");
            if (Unmarked > 0) parts.Add($"{Unmarked} not marked yet");
            return $"{Date.ToString("ddd d MMMM", CultureInfo.InvariantCulture)}, {(parts.Count == 0 ? "no sessions" : string.Join(", ", parts))}";
        }
    }
}

public sealed record CalendarSummary(int SessionsThisMonth, int? RatePercent, int Upcoming, CalendarSession? Next);

/// <summary>The rules of the attendance calendar, with no screen and no database, so every one of them is tested directly.</summary>
public static class AttendanceCalendar
{
    /// <summary>The first column of a week. An assumption, not an owner choice: change it here to start on Sunday.</summary>
    public const DayOfWeek FirstDay = DayOfWeek.Monday;

    public static DateOnly StartOfWeek(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek - (int)FirstDay + 7) % 7));

    public static IReadOnlyList<DateOnly> WeekOf(DateOnly day) { var s = StartOfWeek(day); return Enumerable.Range(0, 7).Select(i => s.AddDays(i)).ToList(); }

    /// <summary>Six whole weeks that contain the month, so the grid never changes height.</summary>
    public static IReadOnlyList<DateOnly> MonthGrid(int year, int month)
    {
        var s = StartOfWeek(new DateOnly(year, month, 1));
        return Enumerable.Range(0, 42).Select(i => s.AddDays(i)).ToList();
    }

    // the server sends UTC; a value of unknown kind is taken as UTC, like the LocalDate converter does
    static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone));
    public static DateTime LocalTime(DateTime utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone);

    /// <summary>Midnight at the start of a local day, as a UTC time, for asking the server for a range.</summary>
    public static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);

    public static IReadOnlyList<CalendarDay> Days(IEnumerable<DateOnly> dates, IReadOnlyList<CalendarSession> sessions, DateOnly today, DateOnly? selected, DateOnly? monthOf, TimeZoneInfo zone)
    {
        var byDay = sessions.Where(s => !s.Cancelled).GroupBy(s => LocalDay(s.Session.ScheduledStart, zone)).ToDictionary(g => g.Key, g => g.ToList());
        return dates.Select(d =>
        {
            var on = byDay.GetValueOrDefault(d) ?? [];
            return new CalendarDay(d, monthOf is not { } m || (d.Year == m.Year && d.Month == m.Month), d == today, d == selected,
                on.Count(s => s.Attended), on.Count(s => s.Absent), on.Count(s => s.Excused), on.Count(s => s.Unmarked));
        }).ToList();
    }

    public static IReadOnlyList<CalendarSession> SessionsOn(IReadOnlyList<CalendarSession> sessions, DateOnly day, TimeZoneInfo zone) =>
        sessions.Where(s => LocalDay(s.Session.ScheduledStart, zone) == day).OrderBy(s => s.Session.ScheduledStart).ToList();

    public static IReadOnlyList<CalendarSession> SessionsInMonth(IReadOnlyList<CalendarSession> sessions, int year, int month, TimeZoneInfo zone) =>
        sessions.Where(s => { var d = LocalDay(s.Session.ScheduledStart, zone); return d.Year == year && d.Month == month; }).ToList();

    /// <summary>Sessions this month (cancelled ones left out), the attendance rate, and what is coming up.</summary>
    public static CalendarSummary Summary(IReadOnlyList<CalendarSession> monthSessions, IReadOnlyList<CalendarSession> upcoming)
    {
        var live = monthSessions.Where(s => !s.Cancelled).ToList();
        int attended = live.Count(s => s.Attended), absent = live.Count(s => s.Absent);
        int? rate = attended + absent == 0 ? null : (int)Math.Round(100.0 * attended / (attended + absent), MidpointRounding.AwayFromZero);
        return new CalendarSummary(live.Count, rate, upcoming.Count, upcoming.OrderBy(s => s.Session.ScheduledStart).FirstOrDefault());
    }
}
