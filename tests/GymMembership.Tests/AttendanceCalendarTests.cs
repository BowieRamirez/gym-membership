using System.Globalization;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class AttendanceCalendarTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly TimeZoneInfo Manila = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    static int nextId = 1;

    static CalendarSession S(string startUtc, string? mark, string status = "scheduled", string with = "Alex Tan")
    {
        var start = DateTime.Parse(startUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new CalendarSession(new TrainingSession { Id = nextId++, CoachId = "c", MemberId = "m", ScheduledStart = start, ScheduledEnd = start.AddHours(1), Status = status }, with, mark);
    }

    static DateOnly D(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    // ---- the grid
    [Fact]
    public void A_month_grid_is_42_days_starting_on_the_Monday_on_or_before_the_first()
    {
        var oct = AttendanceCalendar.MonthGrid(2026, 10);       // 1 Oct 2026 is a Thursday
        Assert.Equal(42, oct.Count);
        Assert.Equal(D("2026-09-28"), oct[0]); Assert.Equal(DayOfWeek.Monday, oct[0].DayOfWeek);
        Assert.Equal(D("2026-11-08"), oct[41]);

        Assert.Equal(D("2026-06-01"), AttendanceCalendar.MonthGrid(2026, 6)[0]);     // the 1st is itself a Monday
        Assert.Equal(D("2026-01-26"), AttendanceCalendar.MonthGrid(2026, 2)[0]);     // 1 Feb 2026 is a Sunday
        Assert.Contains(D("2028-02-29"), AttendanceCalendar.MonthGrid(2028, 2));     // a leap day
        Assert.Equal(42, AttendanceCalendar.MonthGrid(2028, 2).Count);
    }

    [Fact]
    public void A_week_runs_Monday_to_Sunday_even_across_a_month_end()
    {
        var w = AttendanceCalendar.WeekOf(D("2026-10-14"));          // a Wednesday
        Assert.Equal(D("2026-10-12"), w[0]); Assert.Equal(D("2026-10-18"), w[6]);
        var across = AttendanceCalendar.WeekOf(D("2026-10-31"));     // a Saturday
        Assert.Equal(D("2026-10-26"), across[0]); Assert.Equal(D("2026-11-01"), across[6]);
    }

    // ---- what a day shows
    [Fact]
    public void A_day_of_attended_and_absent_sessions_shows_both_marks()
    {
        var sessions = new[] { S("2026-10-12T01:00:00Z", "present"), S("2026-10-12T03:00:00Z", "late"), S("2026-10-12T05:00:00Z", "absent") };
        var day = AttendanceCalendar.Days([D("2026-10-12")], sessions, D("2026-10-13"), null, D("2026-10-01"), Utc).Single();
        Assert.Equal(2, day.Attended); Assert.Equal(1, day.Absent);
        Assert.True(day.HasCheck); Assert.True(day.HasX);
        Assert.Equal("Mon 12 October, 2 attended, 1 absent", day.Description);
    }

    [Fact]
    public void Excused_unmarked_and_cancelled_sessions_show_no_mark()
    {
        var sessions = new[] { S("2026-10-13T01:00:00Z", "excused"), S("2026-10-13T03:00:00Z", null), S("2026-10-13T05:00:00Z", "present", status: "cancelled") };
        var day = AttendanceCalendar.Days([D("2026-10-13")], sessions, D("2026-10-20"), null, D("2026-10-01"), Utc).Single();
        Assert.False(day.HasCheck); Assert.False(day.HasX);
        Assert.Equal(1, day.Excused); Assert.Equal(1, day.Unmarked);
        Assert.Equal(2, day.Sessions);                               // the cancelled one is not a session of that day
        Assert.Equal("Tue 13 October, 1 excused, 1 not marked yet", day.Description);
    }

    [Fact]
    public void A_day_with_nothing_says_so_and_flags_today_selection_and_the_month()
    {
        var days = AttendanceCalendar.Days([D("2026-09-30"), D("2026-10-14")], [], D("2026-10-14"), D("2026-10-14"), D("2026-10-01"), Utc);
        Assert.Equal("Wed 30 September, no sessions", days[0].Description);
        Assert.False(days[0].InMonth); Assert.True(days[0].Dimmed);
        Assert.True(days[1].InMonth); Assert.True(days[1].IsToday); Assert.True(days[1].IsSelected);
    }

    [Fact]
    public void A_session_late_on_the_last_evening_belongs_to_the_next_local_day_and_month()
    {
        var late = S("2026-10-31T16:30:00Z", "present");                    // 00:30 on 1 Nov in UTC+8
        Assert.Equal(D("2026-11-01"), AttendanceCalendar.LocalDay(late.Session.ScheduledStart, Manila));
        Assert.Equal(D("2026-10-31"), AttendanceCalendar.LocalDay(late.Session.ScheduledStart, Utc));
        Assert.Empty(AttendanceCalendar.SessionsInMonth([late], 2026, 10, Manila));
        Assert.Single(AttendanceCalendar.SessionsInMonth([late], 2026, 11, Manila));
        Assert.Single(AttendanceCalendar.SessionsOn([late], D("2026-11-01"), Manila));
    }

    [Fact]
    public void The_start_of_a_local_day_is_returned_in_utc()
    {
        Assert.Equal(new DateTime(2026, 10, 11, 16, 0, 0, DateTimeKind.Utc), AttendanceCalendar.StartOfDayUtc(D("2026-10-12"), Manila));
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), AttendanceCalendar.StartOfDayUtc(D("2026-10-12"), Utc));
    }

    // a zone whose clocks skip the hour after midnight on 29 March, and repeat it on 25 October (some real zones do)
    static TimeZoneInfo SkipsAndRepeatsMidnight() => TimeZoneInfo.CreateCustomTimeZone("Midnight", TimeSpan.FromHours(2), "Midnight", "Midnight", "Midnight DST",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 25))]);

    [Fact]
    public void A_day_whose_midnight_is_skipped_or_repeated_still_has_a_start()
    {
        var zone = SkipsAndRepeatsMidnight();
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 29, 0, 30, 0)), "test zone: the hour after midnight is skipped");
        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 10, 25, 0, 30, 0)), "test zone: the hour after midnight is repeated");

        // skipped: the day starts when the clocks resume, 01:00 in daylight time (UTC+3)
        Assert.Equal(new DateTime(2026, 3, 28, 22, 0, 0, DateTimeKind.Utc), AttendanceCalendar.StartOfDayUtc(D("2026-03-29"), zone));
        // repeated: the day starts the first time midnight happens, in daylight time (UTC+3)
        Assert.Equal(new DateTime(2026, 10, 24, 21, 0, 0, DateTimeKind.Utc), AttendanceCalendar.StartOfDayUtc(D("2026-10-25"), zone));
        // and the days stay in order, so a range from one start to the next never runs backwards
        Assert.True(AttendanceCalendar.StartOfDayUtc(D("2026-10-24"), zone) < AttendanceCalendar.StartOfDayUtc(D("2026-10-25"), zone));
        Assert.True(AttendanceCalendar.StartOfDayUtc(D("2026-10-25"), zone) < AttendanceCalendar.StartOfDayUtc(D("2026-10-26"), zone));
    }

    [Fact]
    public void Sessions_of_a_day_come_back_in_time_order()
    {
        var a = S("2026-10-12T09:00:00Z", null); var b = S("2026-10-12T02:00:00Z", null);
        Assert.Equal(new[] { b, a }, AttendanceCalendar.SessionsOn([a, b], D("2026-10-12"), Utc));
    }

    // ---- the summary
    [Fact]
    public void The_rate_counts_attended_out_of_marked_and_leaves_excused_cancelled_and_unmarked_out()
    {
        var month = new List<CalendarSession>();
        for (var i = 0; i < 8; i++) month.Add(S($"2026-10-{i + 1:00}T01:00:00Z", "present"));
        month.Add(S("2026-10-09T01:00:00Z", "late"));
        month.Add(S("2026-10-10T01:00:00Z", "absent"));
        month.Add(S("2026-10-11T01:00:00Z", "excused")); month.Add(S("2026-10-12T01:00:00Z", "excused"));
        month.Add(S("2026-10-13T01:00:00Z", null));
        month.Add(S("2026-10-14T01:00:00Z", "present", status: "cancelled"));

        var summary = AttendanceCalendar.Summary(month, []);
        Assert.Equal(13, summary.SessionsThisMonth);                  // 14 minus the cancelled one
        Assert.Equal(90, summary.RatePercent);                        // (8 + 1) / (8 + 1 + 1)
    }

    [Fact]
    public void With_nothing_marked_there_is_no_rate_not_zero_percent()
    {
        var summary = AttendanceCalendar.Summary([S("2026-10-01T01:00:00Z", null), S("2026-10-02T01:00:00Z", "excused")], []);
        Assert.Null(summary.RatePercent);
        Assert.Equal(2, summary.SessionsThisMonth);
    }

    [Fact]
    public void The_rate_rounds_to_a_whole_percent_and_can_be_a_perfect_one_hundred()
    {
        Assert.Equal(67, AttendanceCalendar.Summary([S("2026-10-01T01:00:00Z", "present"), S("2026-10-02T01:00:00Z", "late"), S("2026-10-03T01:00:00Z", "absent")], []).RatePercent);
        Assert.Equal(100, AttendanceCalendar.Summary([S("2026-10-01T01:00:00Z", "present")], []).RatePercent);
    }

    [Fact]
    public void Upcoming_is_the_given_list_and_next_is_its_earliest()
    {
        var later = S("2026-10-25T01:00:00Z", null, with: "Bea Lim"); var sooner = S("2026-10-20T01:00:00Z", null, with: "Alex Tan");
        var summary = AttendanceCalendar.Summary([], [later, sooner]);
        Assert.Equal(2, summary.Upcoming);
        Assert.Equal("Alex Tan", summary.Next!.With);
        Assert.Null(AttendanceCalendar.Summary([], []).Next);
    }
}
