# Phase 6a: coach attendance calendar. Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The coach's "Attendance" page becomes a calendar (month and week) showing a check, an X or nothing per day, a day list with Present / Late / Absent / Excused buttons that correct a mark in place, and a summary of sessions this month, attendance rate and upcoming sessions.

**Architecture:** A small pure class (`AttendanceCalendar`) turns a list of the coach's sessions-with-marks into day cells and a summary, with no I/O, so every rule is tested without a screen. Two new read-only service calls load the data (a date range, and what is upcoming). `AttendanceViewModel` is rewritten around them and keeps the existing mark command. The page is plain XAML using the app's existing tokens and the outline icons from phase 5.

**Tech Stack:** C# / .NET 10 MAUI, CommunityToolkit.Mvvm, xUnit, `DemoGateway` for tests, MAUI's own path parser in `IconTests`.

**Spec:** `docs/superpowers/specs/2026-10-17-phase6a-attendance-calendar-design.md`.

## Spec changes this plan makes

1. **Two read calls, not one.** The spec said one new call. "Upcoming" means scheduled sessions from now on, whatever month is in view, so it needs its own small call: `UpcomingAsync()`. `CalendarAsync(from, to)` is the other.
2. **The attendance lookup always runs**, even for an empty month, so the server-translation test proves the "session id in (...)" filter. One extra, tiny request for an empty month.
3. **Demo seed:** the demo has only two sessions, so Task 3 adds nine past and future sessions (with marks) so the grid has something to show.

## Global Constraints

- Existing tests stay green: `dotnet test tests/GymMembership.Tests` (372 at the start) and the database checks are untouched (**no database change**, no migration).
- **No arcs in icon outlines**: only absolute M, L, C, Q, Z, every number spaced. `IconTests` already enforces it; new icons go through the arc-free generator, never hand-pasted SVG.
- No `RelativeSource AncestorType` command bindings. Row taps use `BasePage.Row<TVm>` with a plain `Tapped`/`Clicked` handler.
- Mark values are exactly `present`, `late`, `absent`, `excused` (what `record_session_attendance` accepts). A cancelled session is never marked and never counted.
- Days are the **device's local days**. Tests pass an explicit `TimeZoneInfo` so they do not depend on the machine's zone.
- First day of the week is **Monday** (an assumption; one constant, `AttendanceCalendar.FirstDay`).
- Match the file you edit: tabs in `*.xaml.cs` under `GymMembership.App`, 4 spaces in `GymMembership.Core`.
- Add files by explicit path. **Never `git add docs`, `git add -A` or `git add .`**; your screenshots folder is excluded locally but the habit matters. Do not push or merge.

## Review Focus

1. **Moving to another month quickly:** a slow answer for a month the coach already left must not replace the grid on screen (Task 4).
2. **Correcting a mark** must change that day's marks and the summary together, from one reload (Task 4).
3. **A session late on the last evening of a month** (UTC+8 local) belongs to the next local day and month, not the previous one (Task 2).
4. **A cancelled session** has no buttons, is not counted in "sessions this month" or the rate, and a mark request for it is ignored (Tasks 2 and 4).
5. **A failed reload after a successful mark** keeps the grid and the day list on screen and shows the error, instead of blanking them (Task 4).

## File structure

| File | Change | Responsibility |
| --- | --- | --- |
| `src/GymMembership.App/Controls/Icons.cs` | modify | add `check`, `x`, `chevron-left`, `chevron-right` |
| `src/GymMembership.Core/Services/AttendanceCalendar.cs` | create | `CalendarSession`, `CalendarDay`, `CalendarSummary`, `AttendanceCalendar` (pure) |
| `src/GymMembership.Core/Services/Services.cs` | modify | `ISessionService.CalendarAsync`, `UpcomingAsync` and their `SessionService` versions |
| `src/GymMembership.Core/Demo/DemoGateway.cs` | modify | seed nine sessions with marks |
| `src/GymMembership.Core/ViewModels/StaffViewModels.cs` | modify | `WeekRow`; rewrite `AttendanceViewModel` |
| `src/GymMembership.App/Views/AttendancePage.xaml` + `.xaml.cs` | rewrite | the calendar page |
| `tests/GymMembership.Tests/AttendanceCalendarTests.cs` | create | pure logic |
| `tests/GymMembership.Tests/CalendarServiceTests.cs` | create | the two service calls on the demo |
| `tests/GymMembership.Tests/AttendanceViewModelTests.cs` | create | view model with fakes |
| `tests/GymMembership.Tests/SupabaseGatewayTests.cs` | modify | prove the new filters are real server queries |
| `tests/GymMembership.Tests/IconTests.cs` | modify | name the new icons; add the page to the checked pages |

---

### Task 1: Four icons

**Files:**
- Modify: `src/GymMembership.App/Controls/Icons.cs` (inside the `Data` dictionary, after the `landmark` line)
- Test: `tests/GymMembership.Tests/IconTests.cs`

- [ ] **Step 1: Write the failing test.** Add inside `IconTests`:

```csharp
    [Fact]
    public void The_calendar_icons_are_drawn()
    {
        var drawn = Drawn();
        foreach (var name in new[] { "check", "x", "chevron-left", "chevron-right" }) Assert.Contains(name, drawn);
    }
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter The_calendar_icons_are_drawn`
Expected: FAIL, `check` not found.

- [ ] **Step 3: Add the data.** In `Icons.cs`, after the `["landmark"] = ...` line add exactly (these were generated with the arc-free generator, each stroke starting from the origin):

```csharp
        ["check"] = "M 20 6 L 9 17 L 4 12",
        ["x"] = "M 18 6 L 6 18 M 6 6 L 18 18",
        ["chevron-left"] = "M 15 18 L 9 12 L 15 6",
        ["chevron-right"] = "M 9 18 L 15 12 L 9 6",
```

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass, including `Every_icon_scales_to_real_numbers_that_windows_can_draw` over the four new outlines.

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.App/Controls/Icons.cs tests/GymMembership.Tests/IconTests.cs
git commit -m "feat: check, x and chevron icons for the attendance calendar"
```

---

### Task 2: The calendar logic

**Files:**
- Create: `src/GymMembership.Core/Services/AttendanceCalendar.cs`
- Test: `tests/GymMembership.Tests/AttendanceCalendarTests.cs`

**Interfaces:**
- Produces (used by Tasks 3 to 5):
  - `CalendarSession(TrainingSession Session, string With, string? Mark)` with `Cancelled`, `CanMark`, `Attended`, `Absent`, `Excused`, `Unmarked`.
  - `CalendarDay(DateOnly Date, bool InMonth, bool IsToday, bool IsSelected, int Attended, int Absent, int Excused, int Unmarked)` with `Number`, `Dimmed`, `HasCheck`, `HasX`, `Sessions`, `Description`.
  - `CalendarSummary(int SessionsThisMonth, int? RatePercent, int Upcoming, CalendarSession? Next)`.
  - `AttendanceCalendar`: `FirstDay`, `StartOfWeek`, `WeekOf`, `MonthGrid`, `LocalDay`, `LocalTime`, `StartOfDayUtc`, `Days`, `SessionsOn`, `SessionsInMonth`, `Summary`.

- [ ] **Step 1: Write the failing tests.** Create `tests/GymMembership.Tests/AttendanceCalendarTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter AttendanceCalendarTests`
Expected: build error, `CalendarSession` / `AttendanceCalendar` not defined.

- [ ] **Step 3: Implement.** Create `src/GymMembership.Core/Services/AttendanceCalendar.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass.

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/Services/AttendanceCalendar.cs tests/GymMembership.Tests/AttendanceCalendarTests.cs
git commit -m "feat: attendance calendar rules (grid, day marks, summary)"
```

---

### Task 3: Loading the data, and demo sessions to look at

**Files:**
- Modify: `src/GymMembership.Core/Services/Services.cs` (interface at ~line 606, class at ~line 620)
- Modify: `src/GymMembership.Core/Demo/DemoGateway.cs` (seed, after the two existing `TrainingSession` lines near 1142)
- Modify: `tests/GymMembership.Tests/SupabaseGatewayTests.cs`
- Test: `tests/GymMembership.Tests/CalendarServiceTests.cs`

**Interfaces:**
- Consumes: `CalendarSession` (Task 2).
- Produces: `ISessionService.CalendarAsync(DateTime fromUtc, DateTime toUtc) : Task<AppResult<IReadOnlyList<CalendarSession>>>` (the signed-in coach's sessions starting in `[from, to)`, with marks, oldest first) and `UpcomingAsync()` (their scheduled sessions from now on, nearest first, `Mark` null).

- [ ] **Step 1: Write the failing tests.** Create `tests/GymMembership.Tests/CalendarServiceTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class CalendarServiceTests
{
    static async Task<(DemoGateway Gw, SessionService Svc)> As(string email)
    {
        var gw = new DemoGateway(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new SessionService(gw));
    }

    [Fact]
    public async Task A_coach_gets_their_sessions_in_the_range_with_the_marks_and_names()
    {
        var (_, svc) = await As("coach@demo.io");
        var rows = (await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!;
        Assert.True(rows.Count >= 9);
        Assert.Equal(rows.OrderBy(r => r.Session.ScheduledStart).Select(r => r.Session.Id), rows.Select(r => r.Session.Id));   // oldest first
        Assert.Contains(rows, r => r.Mark == "present"); Assert.Contains(rows, r => r.Mark == "late");
        Assert.Contains(rows, r => r.Mark == "absent"); Assert.Contains(rows, r => r.Mark == "excused");
        Assert.Contains(rows, r => r.Cancelled);
        Assert.All(rows, r => Assert.NotEqual("Unknown", r.With));
        Assert.Contains(rows, r => r.With == "Alex Tan");
    }

    [Fact]
    public async Task The_range_is_start_inclusive_and_end_exclusive()
    {
        var (_, svc) = await As("coach@demo.io");
        var all = (await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!;
        var one = all.First(r => r.Mark == "present");
        var from = one.Session.ScheduledStart;
        Assert.Contains((await svc.CalendarAsync(from, from.AddMinutes(1))).Value!, r => r.Session.Id == one.Session.Id);          // starts exactly at the start
        Assert.DoesNotContain((await svc.CalendarAsync(from.AddMinutes(-1), from)).Value!, r => r.Session.Id == one.Session.Id);   // the end is exclusive
    }

    [Fact]
    public async Task Someone_who_is_not_a_coach_gets_nothing()
    {
        var (_, svc) = await As("member@demo.io");
        Assert.Empty((await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!);
        Assert.Empty((await svc.UpcomingAsync()).Value!);
    }

    [Fact]
    public async Task Another_coach_does_not_see_this_coachs_sessions()
    {
        var (_, svc) = await As("kai@demo.io");
        Assert.Empty((await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!);
    }

    [Fact]
    public async Task Upcoming_is_only_scheduled_sessions_from_now_on_nearest_first()
    {
        var (_, svc) = await As("coach@demo.io");
        var up = (await svc.UpcomingAsync()).Value!;
        Assert.NotEmpty(up);
        Assert.All(up, r => { Assert.Equal("scheduled", r.Session.Status); Assert.True(r.Session.ScheduledStart >= DateTime.UtcNow.AddMinutes(-1)); Assert.Null(r.Mark); });
        Assert.Equal(up.OrderBy(r => r.Session.ScheduledStart).Select(r => r.Session.Id), up.Select(r => r.Session.Id));
    }

    [Fact]
    public async Task Marking_a_session_shows_up_in_the_next_load()
    {
        var (gw, svc) = await As("coach@demo.io");
        var target = (await svc.UpcomingAsync()).Value!.First().Session;
        Assert.True((await new AttendanceService(gw).RecordAsync(target.Id, "late")).Ok);
        var rows = (await svc.CalendarAsync(target.ScheduledStart.AddMinutes(-1), target.ScheduledStart.AddMinutes(1))).Value!;
        Assert.Equal("late", rows.Single(r => r.Session.Id == target.Id).Mark);
    }
}
```
In `SupabaseGatewayTests.cs`, inside `Every_filter_the_services_use_translates_to_a_server_query`, just before the final `Assert.True(results.All(...))`, add:

```csharp
        Note("calendar", await new SessionService(gw).CalendarAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(30)));
        Note("upcoming", await new SessionService(gw).UpcomingAsync());
        Assert.Contains(_seen, r => r.Path.EndsWith("/training_sessions") && r.Query.Contains("scheduled_start"));          // the date range is a server filter
        Assert.Contains(_seen, r => r.Path.EndsWith("/session_attendance") && r.Query.Contains("training_session_id"));      // and so is the lookup of marks by session id
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter "CalendarServiceTests|Every_filter"`
Expected: build error, `CalendarAsync` / `UpcomingAsync` do not exist.

- [ ] **Step 3: Add the interface members.** In `ISessionService` (after `CancelSessionAsync`):

```csharp
    /// <summary>The signed-in coach's sessions that start in [fromUtc, toUtc), oldest first, each with how it went (Mark is null until marked).</summary>
    Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc);
    /// <summary>The signed-in coach's scheduled sessions from now on, nearest first.</summary>
    Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync();
```
In `SessionService` (after `SetSessionStatusAsync`):

```csharp
    public Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc) => Safe.RunAsync<IReadOnlyList<CalendarSession>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var sessions = await gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.ScheduledStart >= fromUtc && s.ScheduledStart < toUtc);
        var ids = sessions.Select(s => s.Id).ToList();
        var marks = (await gw.ListAsync<SessionAttendance>(a => ids.Contains(a.SessionId))).ToDictionary(a => a.SessionId, a => a.Status);   // asked even for an empty month: one tiny request, and the filter stays proven
        var names = await Directory_.UserNamesAsync(gw);
        return sessions.OrderBy(s => s.ScheduledStart)
            .Select(s => new CalendarSession(s, names.GetValueOrDefault(s.MemberId, "Member"), marks.GetValueOrDefault(s.Id))).ToList();
    });

    public Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync() => Safe.RunAsync<IReadOnlyList<CalendarSession>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var now = DateTime.UtcNow;
        var sessions = await gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.Status == Status.Scheduled && s.ScheduledStart >= now);
        var names = await Directory_.UserNamesAsync(gw);
        return sessions.OrderBy(s => s.ScheduledStart).Select(s => new CalendarSession(s, names.GetValueOrDefault(s.MemberId, "Member"), null)).ToList();
    });
```
Then check nothing else implements `ISessionService`: `grep -rn ": ISessionService" src tests` (expected: only `SessionService`). If a test fake exists, add the two members to it.

- [ ] **Step 4: Seed the demo.** In `DemoGateway.cs`, after the two existing `Add(new TrainingSession ...)` lines (~1142-1143), add (the seed helper `At(days, hour)` is already used on those lines; `Add` returns the entity with its id):

```csharp
        // a month of history for the coach's calendar: marks of every kind, a mixed day, a cancelled session and one more upcoming
        TrainingSession Past(string member, int days, int hour, string status, string? mark)
        {
            var s = Add(new TrainingSession { CoachId = "u-coach1", MemberId = member, Title = "Training session", ScheduledStart = At(days, hour), ScheduledEnd = At(days, hour + 1), Status = status });
            if (mark is not null) Add(new SessionAttendance { SessionId = s.Id, Status = mark, CheckedInAt = mark is "present" or "late" ? At(days, hour) : null });
            return s;
        }
        Past("u-alex", -1, 9, Status.Completed, "present");
        Past("u-bea", -2, 17, Status.Completed, "late");
        Past("u-alex", -5, 9, Status.NoShow, "absent");
        Past("u-bea", -6, 10, Status.Completed, "excused");
        Past("u-alex", -8, 9, Status.Completed, "present");        // a mixed day: attended and absent
        Past("u-bea", -8, 11, Status.NoShow, "absent");
        Past("u-alex", -12, 9, Status.Cancelled, null);
        Past("u-alex", -20, 9, Status.Completed, "present");
        Past("u-bea", -30, 9, Status.Completed, "present");
        Add(new TrainingSession { CoachId = "u-coach1", MemberId = "u-bea", Title = "Mobility", ScheduledStart = At(5, 9), ScheduledEnd = At(5, 10) });
```
If the local-function-in-the-seed does not compile in that method (for example if it is an expression-bodied block), move `Past` to a private method of the class.

- [ ] **Step 5: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass, including `Every_filter...`. If the new `Assert.Contains` lines fail on the substring, print the captured `Query` for those two paths and change only the substring to what the library really sent; if `ids.Contains(...)` is refused outright, `Note("calendar", ...)` fails: then list the coach's attendance rows unfiltered and match in memory (`ponytail: unfiltered until a coach has thousands of marks`). If existing tests that count the demo's sessions now fail, fix the assertion to compare before and after and record the change in the ledger as a ruling (the code is right; the seed grew).

- [ ] **Step 6: Commit.**
```bash
git add src/GymMembership.Core/Services/Services.cs src/GymMembership.Core/Demo/DemoGateway.cs tests/GymMembership.Tests/CalendarServiceTests.cs tests/GymMembership.Tests/SupabaseGatewayTests.cs
git commit -m "feat: load a coach's sessions with their marks for a date range, and what is upcoming"
```

---

### Task 4: The Attendance view model

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/StaffViewModels.cs` (replace the whole `AttendanceViewModel`, currently ~lines 439-459; add `WeekRow` just above it)
- Test: `tests/GymMembership.Tests/AttendanceViewModelTests.cs`

**Interfaces:**
- Consumes: `ISessionService.CalendarAsync/UpcomingAsync` (Task 3), `IAttendanceService.RecordAsync`, `AttendanceCalendar` (Task 2).
- Produces:
  - `WeekRow(CalendarDay Day, IReadOnlyList<CalendarSession> Sessions)` with `Title`.
  - `AttendanceViewModel(ISessionService sessions, IAttendanceService attendance)`: settable `Clock : Func<DateTime>` (UTC) and `Zone : TimeZoneInfo`; collections `Days` (42), `WeekRows` (7), `DaySessions`; `ViewMode` ("Month" or "Week"), `IsMonth`, `IsWeek`, `Focus`, `SelectedDay`, `PeriodTitle`, `DayTitle`, `HasDay`, `SessionsText`, `RateText`, `UpcomingText`, `NextText`; commands `ShowMonthCommand`, `ShowWeekCommand`, `PreviousCommand`, `NextCommand`, `TodayCommand`, `SelectDayCommand(CalendarDay)`, `MarkCommand(string "id|status")`.

- [ ] **Step 1: Write the failing tests.** Create `tests/GymMembership.Tests/AttendanceViewModelTests.cs`:

```csharp
using System.Globalization;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AttendanceViewModelTests
{
    // a session service whose sessions the test sets, and whose answers it can hold back, standing in for the server
    sealed class FakeSessions : ISessionService
    {
        public List<CalendarSession> All = [];
        public List<CalendarSession> Upcoming = [];
        public bool FailCalendar;
        public readonly Queue<(DateTime From, TaskCompletionSource<AppResult<IReadOnlyList<CalendarSession>>> Reply)> Held = new();
        public bool Hold;
        public int CalendarCalls;

        public Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc)
        {
            CalendarCalls++;
            if (Hold) { var tcs = new TaskCompletionSource<AppResult<IReadOnlyList<CalendarSession>>>(); Held.Enqueue((fromUtc, tcs)); return tcs.Task; }
            return Task.FromResult(Answer(fromUtc, toUtc));
        }
        public AppResult<IReadOnlyList<CalendarSession>> Answer(DateTime from, DateTime to) => FailCalendar
            ? AppResult<IReadOnlyList<CalendarSession>>.Fail(AppErrorKind.Validation, "bad_input")
            : AppResult<IReadOnlyList<CalendarSession>>.Success(All.Where(s => s.Session.ScheduledStart >= from && s.Session.ScheduledStart < to).ToList());
        public Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync() => Task.FromResult(AppResult<IReadOnlyList<CalendarSession>>.Success(Upcoming));

        public Task<AppResult<RequestsView>> RequestsAsync() => throw new NotSupportedException();
        public Task<AppResult<bool>> RequestAsync(string coachId, DateTime startUtc, DateTime endUtc, string? message) => throw new NotSupportedException();
        public Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc) => throw new NotSupportedException();
        public Task<AppResult<bool>> RespondAsync(int requestId, bool approve) => throw new NotSupportedException();
        public Task<AppResult<bool>> CancelRequestAsync(int requestId) => throw new NotSupportedException();
        public Task<AppResult<IReadOnlyList<SessionRow>>> SessionsAsync() => throw new NotSupportedException();
        public Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status) => throw new NotSupportedException();
        public Task<AppResult<bool>> RescheduleAsync(int sessionId, DateTime startUtc, DateTime endUtc) => throw new NotSupportedException();
        public Task<AppResult<bool>> CancelSessionAsync(int sessionId) => throw new NotSupportedException();
    }

    // a marking service that changes the fake server's data, like the real one changes the table
    sealed class FakeAttendance(FakeSessions server) : IAttendanceService
    {
        public bool Fail;
        public List<(int Id, string Status)> Recorded = [];
        public Task<AppResult<bool>> RecordAsync(int sessionId, string status)
        {
            Recorded.Add((sessionId, status));
            if (Fail) return Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input"));
            var i = server.All.FindIndex(s => s.Session.Id == sessionId);
            server.All[i] = server.All[i] with { Mark = status };
            return Task.FromResult(AppResult<bool>.Success(true));
        }
        public Task<AppResult<bool>> CheckInAsync(string memberId) => throw new NotSupportedException();
        public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string usernameFragment) => throw new NotSupportedException();
    }

    static int ids = 100;
    static CalendarSession S(string startUtc, string? mark, string status = "scheduled", string with = "Alex Tan")
    {
        var start = DateTime.Parse(startUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new CalendarSession(new TrainingSession { Id = ids++, CoachId = "c", MemberId = "m", ScheduledStart = start, ScheduledEnd = start.AddHours(1), Status = status }, with, mark);
    }

    static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);   // a Friday

    static async Task<(FakeSessions Server, FakeAttendance Att, AttendanceViewModel Vm)> Open(Action<FakeSessions>? seed = null)
    {
        var server = new FakeSessions();
        server.All = [S("2026-10-12T01:00:00Z", "present"), S("2026-10-12T03:00:00Z", "absent"), S("2026-10-13T01:00:00Z", "excused"),
                      S("2026-10-14T01:00:00Z", null, status: "cancelled"), S("2026-10-20T01:00:00Z", null), S("2026-09-30T01:00:00Z", "present")];
        server.Upcoming = [S("2026-10-20T01:00:00Z", null, with: "Bea Lim")];
        seed?.Invoke(server);
        var att = new FakeAttendance(server);
        var vm = new AttendanceViewModel(server, att) { Clock = () => Now, Zone = TimeZoneInfo.Utc };
        await vm.OnAppearingAsync();
        return (server, att, vm);
    }

    static CalendarDay Day(AttendanceViewModel vm, string iso) => vm.Days.Single(d => d.Date == DateOnly.Parse(iso, CultureInfo.InvariantCulture));

    [Fact]
    public async Task It_opens_on_the_month_of_today_with_today_selected_and_marks_on_the_days()
    {
        var (_, _, vm) = await Open();
        Assert.True(vm.IsMonth);
        Assert.Equal("October 2026", vm.PeriodTitle);
        Assert.Equal(42, vm.Days.Count);
        Assert.Equal(new DateOnly(2026, 10, 16), vm.SelectedDay);
        Assert.True(Day(vm, "2026-10-16").IsToday);
        var mixed = Day(vm, "2026-10-12");
        Assert.True(mixed.HasCheck && mixed.HasX);
        Assert.False(Day(vm, "2026-10-13").HasCheck);                       // excused only
        Assert.True(Day(vm, "2026-09-30").HasCheck && !Day(vm, "2026-09-30").InMonth);   // a dimmed day from last month still carries its mark
    }

    [Fact]
    public async Task The_summary_reads_the_month_in_view_and_the_upcoming_list()
    {
        var (_, _, vm) = await Open();
        Assert.Equal("4", vm.SessionsText);                                 // 12th x2, 13th, 20th (the cancelled one and September are out)
        Assert.Equal("50%", vm.RateText);                                   // 1 present / (1 present + 1 absent)
        Assert.Equal("1", vm.UpcomingText);
        Assert.StartsWith("Next: Tue 20 Oct, ", vm.NextText); Assert.Contains("Bea Lim", vm.NextText);
    }

    [Fact]
    public async Task With_nothing_marked_the_rate_is_a_dash()
    {
        var (_, _, vm) = await Open(s => { s.All = [S("2026-10-20T01:00:00Z", null)]; s.Upcoming = []; });
        Assert.Equal("—", vm.RateText);
        Assert.Equal("No upcoming sessions.", vm.NextText);
    }

    [Fact]
    public async Task Selecting_a_day_lists_its_sessions_in_time_order()
    {
        var (_, _, vm) = await Open();
        vm.SelectDayCommand.Execute(Day(vm, "2026-10-12"));
        Assert.Equal("Monday 12 October", vm.DayTitle);
        Assert.Equal(2, vm.DaySessions.Count);
        Assert.True(vm.DaySessions[0].Session.ScheduledStart < vm.DaySessions[1].Session.ScheduledStart);
        Assert.True(Day(vm, "2026-10-12").IsSelected); Assert.False(Day(vm, "2026-10-16").IsSelected);
    }

    [Fact]
    public async Task Next_and_previous_move_by_a_month_in_month_view_and_by_a_week_in_week_view()
    {
        var (_, _, vm) = await Open();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal("November 2026", vm.PeriodTitle);
        await vm.PreviousCommand.ExecuteAsync(null); await vm.PreviousCommand.ExecuteAsync(null);
        Assert.Equal("September 2026", vm.PeriodTitle);

        await vm.TodayCommand.ExecuteAsync(null);
        vm.ShowWeekCommand.Execute(null);
        Assert.Equal("12 to 18 Oct", vm.PeriodTitle);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal("19 to 25 Oct", vm.PeriodTitle);
    }

    [Fact]
    public async Task Today_returns_to_this_month_with_today_selected()
    {
        var (_, _, vm) = await Open();
        await vm.NextCommand.ExecuteAsync(null);
        await vm.TodayCommand.ExecuteAsync(null);
        Assert.Equal("October 2026", vm.PeriodTitle);
        Assert.Equal(new DateOnly(2026, 10, 16), vm.SelectedDay);
    }

    [Fact]
    public async Task The_week_view_has_seven_rows_with_their_sessions_inline_and_needs_no_new_load()
    {
        var (server, _, vm) = await Open();
        var calls = server.CalendarCalls;
        vm.ShowWeekCommand.Execute(null);
        Assert.True(vm.IsWeek);
        Assert.Equal(7, vm.WeekRows.Count);
        Assert.Equal(new DateOnly(2026, 10, 12), vm.WeekRows[0].Day.Date);
        Assert.Equal(2, vm.WeekRows[0].Sessions.Count);
        Assert.Equal(calls, server.CalendarCalls);                           // the first load already covered the week
    }

    [Fact]
    public async Task Correcting_a_mark_changes_the_day_and_the_summary_together()
    {
        var (_, att, vm) = await Open();
        vm.SelectDayCommand.Execute(Day(vm, "2026-10-12"));
        var absent = vm.DaySessions.Single(s => s.Mark == "absent");
        await vm.MarkCommand.ExecuteAsync($"{absent.Session.Id}|present");

        Assert.Equal(new[] { (absent.Session.Id, "present") }, att.Recorded);
        Assert.Contains("Marked Alex Tan as present", vm.Notice);
        Assert.False(Day(vm, "2026-10-12").HasX);                            // no more absence on that day
        Assert.Equal(2, Day(vm, "2026-10-12").Attended);
        Assert.Equal("100%", vm.RateText);
        Assert.All(vm.DaySessions, s => Assert.Equal("present", s.Mark));    // the open day list shows the new marks
        Assert.Equal(new DateOnly(2026, 10, 12), vm.SelectedDay);            // and the selection stayed
    }

    [Fact]
    public async Task A_cancelled_session_cannot_be_marked()
    {
        var (server, att, vm) = await Open();
        var cancelled = server.All.Single(s => s.Cancelled);
        await vm.MarkCommand.ExecuteAsync($"{cancelled.Session.Id}|present");
        Assert.Empty(att.Recorded);
    }

    [Fact]
    public async Task A_failed_mark_shows_an_error_and_changes_nothing()
    {
        var (_, att, vm) = await Open();
        att.Fail = true;
        vm.SelectDayCommand.Execute(Day(vm, "2026-10-12"));
        var absent = vm.DaySessions.Single(s => s.Mark == "absent");
        await vm.MarkCommand.ExecuteAsync($"{absent.Session.Id}|present");
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.Notice);
        Assert.True(Day(vm, "2026-10-12").HasX);
    }

    [Fact]
    public async Task A_failed_reload_after_a_mark_keeps_what_is_on_screen_and_shows_the_error()
    {
        var (server, _, vm) = await Open();
        vm.SelectDayCommand.Execute(Day(vm, "2026-10-12"));
        var absent = vm.DaySessions.Single(s => s.Mark == "absent");
        server.FailCalendar = true;                                          // the mark succeeds, the look again does not
        await vm.MarkCommand.ExecuteAsync($"{absent.Session.Id}|present");
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(2, vm.DaySessions.Count);                               // the day list is still there
        Assert.True(Day(vm, "2026-10-12").HasCheck);                         // and so are the marks
    }

    [Fact]
    public async Task A_slow_answer_for_a_month_the_coach_already_left_does_not_replace_the_grid()
    {
        var (server, _, vm) = await Open();
        server.Hold = true;
        var toNovember = vm.NextCommand.ExecuteAsync(null);                  // asks for November's grid; held
        var backToOctober = vm.PreviousCommand.ExecuteAsync(null);           // the coach goes straight back; held too
        var november = server.Held.Dequeue(); var october = server.Held.Dequeue();

        october.Reply.SetResult(server.Answer(october.From, october.From.AddDays(42)));
        november.Reply.SetResult(AppResult<IReadOnlyList<CalendarSession>>.Success([]));   // November's answer lands last
        await Task.WhenAll(toNovember, backToOctober);

        Assert.Equal("October 2026", vm.PeriodTitle);
        Assert.True(Day(vm, "2026-10-12").HasCheck);                         // October's marks, not November's empty answer
    }

    [Fact]
    public async Task A_failed_first_load_shows_an_error_and_a_plain_grid()
    {
        var server = new FakeSessions { FailCalendar = true };
        var vm = new AttendanceViewModel(server, new FakeAttendance(server)) { Clock = () => Now, Zone = TimeZoneInfo.Utc };
        await vm.OnAppearingAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(42, vm.Days.Count);
        Assert.All(vm.Days, d => Assert.False(d.HasCheck || d.HasX));
    }

    [Fact]
    public async Task Days_follow_the_zone_so_a_late_session_lands_on_the_next_local_day()
    {
        var server = new FakeSessions { All = [S("2026-10-31T16:30:00Z", "present")] };
        var vm = new AttendanceViewModel(server, new FakeAttendance(server))
        { Clock = () => new DateTime(2026, 10, 31, 3, 0, 0, DateTimeKind.Utc), Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8") };
        await vm.OnAppearingAsync();
        Assert.False(Day(vm, "2026-10-31").HasCheck);
        Assert.True(Day(vm, "2026-11-01").HasCheck);
    }
}
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter AttendanceViewModelTests`
Expected: build errors (`Clock`, `Zone`, `Days`, ... missing on `AttendanceViewModel`).

- [ ] **Step 3: Implement.** In `StaffViewModels.cs` replace the whole `AttendanceViewModel` class with this (and add the `WeekRow` record above it). `using System.Globalization;` is needed at the top of the file if not already there.

```csharp
/// <summary>One day of the week view with its sessions inline.</summary>
public sealed record WeekRow(CalendarDay Day, IReadOnlyList<CalendarSession> Sessions)
{
    public string Title => Day.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
}

// the coach's attendance calendar: a month or a week, a day list underneath, and marks that can be corrected in place
public partial class AttendanceViewModel(ISessionService sessions, IAttendanceService attendance) : LoadableViewModel
{
    /// <summary>The moment "now" is; tests set a fixed one.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>Days are the coach's local days. Tests set a fixed zone.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public ObservableCollection<CalendarDay> Days { get; } = new();          // the month grid, 42 cells
    public ObservableCollection<WeekRow> WeekRows { get; } = new();          // the week view, 7 rows
    public ObservableCollection<CalendarSession> DaySessions { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMonth), nameof(IsWeek), nameof(PeriodTitle))] private string viewMode = "Month";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PeriodTitle))] private DateOnly focus;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayTitle), nameof(HasDay))] private DateOnly? selectedDay;
    [ObservableProperty] private string sessionsText = "—";
    [ObservableProperty] private string rateText = "—";
    [ObservableProperty] private string upcomingText = "—";
    [ObservableProperty] private string nextText = "";

    List<CalendarSession> loaded = [];      // what the server last said for the range in view
    List<CalendarSession> upcoming = [];
    int loadTicket;

    public bool IsMonth => ViewMode == "Month";
    public bool IsWeek => ViewMode == "Week";
    public bool HasDay => SelectedDay is not null;
    public string DayTitle => SelectedDay is { } d ? d.ToString("dddd d MMMM", CultureInfo.InvariantCulture) : "";
    public string PeriodTitle => IsMonth ? Focus.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : WeekTitle();

    string WeekTitle()
    {
        var w = AttendanceCalendar.WeekOf(Focus);
        return w[0].Month == w[6].Month
            ? $"{w[0].Day} to {w[6].Day} {w[6].ToString("MMM", CultureInfo.InvariantCulture)}"
            : $"{w[0].Day} {w[0].ToString("MMM", CultureInfo.InvariantCulture)} to {w[6].Day} {w[6].ToString("MMM", CultureInfo.InvariantCulture)}";
    }

    DateOnly Today => AttendanceCalendar.LocalDay(Clock(), Zone);

    public override async Task OnAppearingAsync()
    {
        if (Focus == default) { Focus = Today; SelectedDay = Today; }
        await ReloadAsync();
    }

    // one range covers both views of the same date (the month grid and its week), so switching between them needs no new load
    (DateTime From, DateTime To) Range()
    {
        var grid = AttendanceCalendar.MonthGrid(Focus.Year, Focus.Month); var week = AttendanceCalendar.WeekOf(Focus);
        var first = grid[0] < week[0] ? grid[0] : week[0];
        var last = grid[^1] > week[6] ? grid[^1] : week[6];
        return (AttendanceCalendar.StartOfDayUtc(first, Zone), AttendanceCalendar.StartOfDayUtc(last.AddDays(1), Zone));
    }

    async Task ReloadAsync()
    {
        var ticket = ++loadTicket;                       // the newest request wins: a slow answer for a period the coach has left is dropped
        ErrorMessage = null;
        var (from, to) = Range();
        var calendar = sessions.CalendarAsync(from, to);
        var coming = sessions.UpcomingAsync();
        await Task.WhenAll(calendar, coming);
        if (ticket != loadTicket) return;
        var cal = await calendar; var up = await coming;
        if (cal.Ok) loaded = cal.Value!.ToList(); else ErrorMessage = ErrorText.For(cal.Error, cal.Message);   // a failed look leaves what is shown as it was
        if (up.Ok) upcoming = up.Value!.ToList();
        Rebuild();
    }

    void Rebuild()
    {
        var today = Today;
        Sync(Days, AttendanceCalendar.Days(AttendanceCalendar.MonthGrid(Focus.Year, Focus.Month), loaded, today, SelectedDay, new DateOnly(Focus.Year, Focus.Month, 1), Zone), d => d.Date, (a, b) => a == b);
        var week = AttendanceCalendar.Days(AttendanceCalendar.WeekOf(Focus), loaded, today, SelectedDay, null, Zone);
        Fill(WeekRows, week.Select(d => new WeekRow(d, AttendanceCalendar.SessionsOn(loaded, d.Date, Zone))));
        Fill(DaySessions, SelectedDay is { } day ? AttendanceCalendar.SessionsOn(loaded, day, Zone) : Array.Empty<CalendarSession>());

        var summary = AttendanceCalendar.Summary(AttendanceCalendar.SessionsInMonth(loaded, Focus.Year, Focus.Month, Zone), upcoming);
        SessionsText = summary.SessionsThisMonth.ToString(CultureInfo.InvariantCulture);
        RateText = summary.RatePercent is { } r ? $"{r}%" : "—";
        UpcomingText = summary.Upcoming.ToString(CultureInfo.InvariantCulture);
        NextText = summary.Next is { } n
            ? $"Next: {AttendanceCalendar.LocalTime(n.Session.ScheduledStart, Zone).ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture)} with {n.With}"
            : "No upcoming sessions.";
    }

    [RelayCommand] private void ShowMonth() { ViewMode = "Month"; Rebuild(); }
    [RelayCommand] private void ShowWeek() { ViewMode = "Week"; Rebuild(); }

    [RelayCommand] private Task PreviousAsync() => MoveAsync(IsMonth ? Focus.AddMonths(-1) : Focus.AddDays(-7));
    [RelayCommand] private Task NextAsync() => MoveAsync(IsMonth ? Focus.AddMonths(1) : Focus.AddDays(7));
    [RelayCommand] private Task TodayAsync() => MoveAsync(Today, select: Today);

    async Task MoveAsync(DateOnly to, DateOnly? select = null)
    {
        Focus = to; SelectedDay = select;
        loaded = [];                                      // do not show the last period's marks on the new one while it loads
        Rebuild();
        await ReloadAsync();
    }

    [RelayCommand]
    private void SelectDay(CalendarDay day) { SelectedDay = day.Date; Rebuild(); }

    // the argument is "<sessionId>|<mark>" so one XAML button template serves all four marks
    [RelayCommand]
    private async Task MarkAsync(string arg)
    {
        var parts = arg.Split('|');
        var id = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var row = loaded.FirstOrDefault(s => s.Session.Id == id);
        if (row is null || !row.CanMark) return;          // a cancelled session cannot be marked
        Notice = null; ErrorMessage = null;
        var result = await attendance.RecordAsync(id, parts[1]);
        if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        Notice = $"Marked {row.With} as {parts[1]}.";
        await ReloadAsync();
    }
}
```
Note `ReloadAsync` clears `ErrorMessage` at its start, so a failed reload after a mark sets it again; the `Notice` stays.

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass. (The old `AttendancePage` still names `MarkCommand` with `SessionRow`; the app project is rewritten in Task 5, and the unit-test project does not reference the app, so the tests build.)

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/ViewModels/StaffViewModels.cs tests/GymMembership.Tests/AttendanceViewModelTests.cs
git commit -m "feat: attendance calendar view model with month, week, day list and in-place corrections"
```

---

### Task 5: The page

**Files:**
- Rewrite: `src/GymMembership.App/Views/AttendancePage.xaml`, `AttendancePage.xaml.cs`
- Modify: `tests/GymMembership.Tests/IconTests.cs` (the `Pages` array)

**Interfaces:**
- Consumes: everything from Tasks 1 to 4. The shell is unchanged (`CoachShell.xaml` already has `Title="Attendance"` bound to `AttendancePage`).

- [ ] **Step 1: Make `IconTests` check the new page.** Change the `Pages` array to `["HomePage.xaml", "CashierPage.xaml", "AttendancePage.xaml"]`. Run `dotnet test tests/GymMembership.Tests --filter IconTests`; expected: `Every_icon_a_page_names_is_drawn` still passes (the old page names no icons, the new one will).

- [ ] **Step 2: The page.** Replace `AttendancePage.xaml.cs`:

```csharp
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AttendancePage : BasePage
{
	public AttendancePage(AttendanceViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnDay(object? sender, TappedEventArgs e) => Row<AttendanceViewModel>(sender, v => v.SelectDayCommand);

	void OnMarkPresent(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|present");
	void OnMarkLate(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|late");
	void OnMarkAbsent(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|absent");
	void OnMarkExcused(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|excused");
}
```
Replace `AttendancePage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<v:BasePage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
            xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
            xmlns:v="clr-namespace:GymMembership.App.Views"
            xmlns:c="clr-namespace:GymMembership.App.Controls"
            xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
            xmlns:svc="clr-namespace:GymMembership.Core.Services;assembly=GymMembership.Core"
            x:Class="GymMembership.App.Views.AttendancePage" x:DataType="vm:AttendanceViewModel" Title="Attendance">

    <v:BasePage.Resources>
        <!-- one session with its four marks; the filled button is the current mark. Used by the day list and by every row of the week view. -->
        <DataTemplate x:Key="SessionTemplate" x:DataType="svc:CalendarSession">
            <Border Style="{StaticResource Panel}" Padding="14,12" Margin="0,0,0,8">
                <VerticalStackLayout Spacing="8">
                    <Label Text="{Binding With}" Style="{StaticResource Heading}" />
                    <Label Text="{Binding Session.ScheduledStart, Converter={StaticResource LocalDate}, ConverterParameter='h:mm tt'}" Style="{StaticResource Caption}" />
                    <Label Text="Cancelled" Style="{StaticResource Caption}" FontFamily="BarlowSemiBold" IsVisible="{Binding Cancelled}" />
                    <FlexLayout Wrap="Wrap" IsVisible="{Binding CanMark}">
                        <Button Text="Present" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnMarkPresent">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Mark}" Value="present"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                        <Button Text="Late" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnMarkLate">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Mark}" Value="late"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                        <Button Text="Absent" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnMarkAbsent">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Mark}" Value="absent"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                        <Button Text="Excused" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnMarkExcused">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Mark}" Value="excused"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                    </FlexLayout>
                </VerticalStackLayout>
            </Border>
        </DataTemplate>
    </v:BasePage.Resources>

    <ScrollView MaximumWidthRequest="920">
        <VerticalStackLayout Padding="20,16,20,24" Spacing="14">
            <c:Banner Message="{Binding ErrorMessage}" />
            <c:Banner Kind="notice" Message="{Binding Notice}" />

            <!-- this month at a glance -->
            <FlexLayout Wrap="Wrap">
                <Border Style="{StaticResource Panel}" WidthRequest="{OnIdiom Phone=148, Default=200}" Margin="0,0,12,12">
                    <VerticalStackLayout Spacing="4">
                        <Label Text="{Binding SessionsText}" Style="{StaticResource Numeral}" />
                        <Label Text="Sessions this month" Style="{StaticResource Caption}" />
                    </VerticalStackLayout>
                </Border>
                <Border Style="{StaticResource Panel}" WidthRequest="{OnIdiom Phone=148, Default=200}" Margin="0,0,12,12">
                    <VerticalStackLayout Spacing="4">
                        <Label Text="{Binding RateText}" Style="{StaticResource Numeral}" />
                        <Label Text="Attendance rate" Style="{StaticResource Caption}" />
                    </VerticalStackLayout>
                </Border>
                <Border Style="{StaticResource Panel}" WidthRequest="{OnIdiom Phone=148, Default=260}" Margin="0,0,12,12">
                    <VerticalStackLayout Spacing="4">
                        <Label Text="{Binding UpcomingText}" Style="{StaticResource Numeral}" />
                        <Label Text="Upcoming" Style="{StaticResource Caption}" />
                        <Label Text="{Binding NextText}" Style="{StaticResource Caption}" />
                    </VerticalStackLayout>
                </Border>
            </FlexLayout>

            <!-- Month | Week, then back, the period, forward, Today -->
            <FlexLayout Wrap="Wrap" AlignItems="Center">
                <Button Text="Month" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding ShowMonthCommand}">
                    <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding IsMonth}" Value="True"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                </Button>
                <Button Text="Week" Style="{StaticResource SmallOutlineButton}" Margin="0,0,20,8" Command="{Binding ShowWeekCommand}">
                    <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding IsWeek}" Value="True"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                </Button>
                <Grid WidthRequest="44" HeightRequest="44" Margin="0,0,4,8">
                    <Button Style="{StaticResource SmallOutlineButton}" Padding="0" Command="{Binding PreviousCommand}" SemanticProperties.Description="Previous" />
                    <c:IconView Icon="chevron-left" HorizontalOptions="Center" VerticalOptions="Center" />
                </Grid>
                <Label Text="{Binding PeriodTitle}" Style="{StaticResource Heading}" VerticalOptions="Center" Margin="8,0,8,8" />
                <Grid WidthRequest="44" HeightRequest="44" Margin="0,0,12,8">
                    <Button Style="{StaticResource SmallOutlineButton}" Padding="0" Command="{Binding NextCommand}" SemanticProperties.Description="Next" />
                    <c:IconView Icon="chevron-right" HorizontalOptions="Center" VerticalOptions="Center" />
                </Grid>
                <Button Text="Today" Style="{StaticResource SmallOutlineButton}" Margin="0,0,0,8" Command="{Binding TodayCommand}" />
            </FlexLayout>

            <!-- the month: a check for attended, an X for missed, both on a mixed day, nothing otherwise; every mark is also in the cell's spoken description -->
            <VerticalStackLayout IsVisible="{Binding IsMonth}" Spacing="6">
                <Grid ColumnDefinitions="*,*,*,*,*,*,*">
                    <Label Grid.Column="0" Text="Mon" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                    <Label Grid.Column="1" Text="Tue" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                    <Label Grid.Column="2" Text="Wed" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                    <Label Grid.Column="3" Text="Thu" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                    <Label Grid.Column="4" Text="Fri" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                    <Label Grid.Column="5" Text="Sat" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                    <Label Grid.Column="6" Text="Sun" Style="{StaticResource Caption}" HorizontalOptions="Center" />
                </Grid>
                <FlexLayout Wrap="Wrap" BindableLayout.ItemsSource="{Binding Days}">
                    <BindableLayout.ItemTemplate>
                        <DataTemplate x:DataType="svc:CalendarDay">
                            <Border Style="{StaticResource Panel}" FlexLayout.Basis="14.2857%" Padding="2,8" HeightRequest="58"
                                    SemanticProperties.Description="{Binding Description}">
                                <Border.Triggers>
                                    <DataTrigger TargetType="Border" Binding="{Binding Dimmed}" Value="True"><Setter Property="Opacity" Value="0.45" /></DataTrigger>
                                    <DataTrigger TargetType="Border" Binding="{Binding IsToday}" Value="True">
                                        <Setter Property="Stroke" Value="{AppThemeBinding Light={StaticResource InkLight}, Dark={StaticResource InkDark}}" />
                                        <Setter Property="StrokeThickness" Value="2" />
                                    </DataTrigger>
                                    <DataTrigger TargetType="Border" Binding="{Binding IsSelected}" Value="True">
                                        <Setter Property="BackgroundColor" Value="{AppThemeBinding Light={StaticResource ChalkLight}, Dark={StaticResource ChalkDark}}" />
                                    </DataTrigger>
                                </Border.Triggers>
                                <Border.GestureRecognizers><TapGestureRecognizer Tapped="OnDay" /></Border.GestureRecognizers>
                                <VerticalStackLayout Spacing="4" HorizontalOptions="Center">
                                    <Label Text="{Binding Number}" FontFamily="BarlowSemiBold" HorizontalOptions="Center" />
                                    <HorizontalStackLayout Spacing="3" HorizontalOptions="Center" HeightRequest="14">
                                        <c:IconView Icon="check" WidthRequest="12" HeightRequest="12" IsVisible="{Binding HasCheck}" />
                                        <c:IconView Icon="x" WidthRequest="12" HeightRequest="12" IsVisible="{Binding HasX}" />
                                    </HorizontalStackLayout>
                                </VerticalStackLayout>
                            </Border>
                        </DataTemplate>
                    </BindableLayout.ItemTemplate>
                </FlexLayout>

                <Label Text="{Binding DayTitle}" Style="{StaticResource Heading}" IsVisible="{Binding HasDay}" Margin="0,10,0,0" />
                <Label Text="Tap a day to see its sessions." Style="{StaticResource Muted}" IsVisible="{Binding HasDay, Converter={StaticResource Not}}" Margin="0,10,0,0" />
                <VerticalStackLayout IsVisible="{Binding HasDay}" BindableLayout.ItemsSource="{Binding DaySessions}" BindableLayout.ItemTemplate="{StaticResource SessionTemplate}">
                    <BindableLayout.EmptyView><Label Text="No sessions this day." Style="{StaticResource Muted}" /></BindableLayout.EmptyView>
                </VerticalStackLayout>
            </VerticalStackLayout>

            <!-- the week: one row per day, its sessions inline -->
            <VerticalStackLayout IsVisible="{Binding IsWeek}" Spacing="10" BindableLayout.ItemsSource="{Binding WeekRows}">
                <BindableLayout.ItemTemplate>
                    <DataTemplate x:DataType="vm:WeekRow">
                        <VerticalStackLayout Spacing="6">
                            <HorizontalStackLayout Spacing="8" SemanticProperties.Description="{Binding Day.Description}">
                                <Label Text="{Binding Title}" Style="{StaticResource Heading}" VerticalOptions="Center" />
                                <c:IconView Icon="check" WidthRequest="14" HeightRequest="14" IsVisible="{Binding Day.HasCheck}" />
                                <c:IconView Icon="x" WidthRequest="14" HeightRequest="14" IsVisible="{Binding Day.HasX}" />
                            </HorizontalStackLayout>
                            <VerticalStackLayout BindableLayout.ItemsSource="{Binding Sessions}" BindableLayout.ItemTemplate="{StaticResource SessionTemplate}">
                                <BindableLayout.EmptyView><Label Text="No sessions." Style="{StaticResource Muted}" /></BindableLayout.EmptyView>
                            </VerticalStackLayout>
                        </VerticalStackLayout>
                    </DataTemplate>
                </BindableLayout.ItemTemplate>
            </VerticalStackLayout>
        </VerticalStackLayout>
    </ScrollView>
</v:BasePage>
```

- [ ] **Step 3: Build everything and run the tests.**
Run: `dotnet test tests/GymMembership.Tests`, then `dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0` and `dotnet build src/GymMembership.App -f net10.0-android`.
Expected: tests pass (including `XamlBindingTests` over the new page and `IconTests` over its four icon names); both builds succeed with 0 errors and no new warnings from the new files. If `XamlBindingTests` objects to a binding inside the shared `SessionTemplate` resource (it checks bindings by their `x:DataType` scope), read its message and give the failing element the right `x:DataType` rather than loosening the test.

- [ ] **Step 4: Commit.**
```bash
git add src/GymMembership.App/Views/AttendancePage.xaml src/GymMembership.App/Views/AttendancePage.xaml.cs tests/GymMembership.Tests/IconTests.cs
git commit -m "feat: attendance calendar page"
```

---

### Task 6: Look at it, review it, record it

**Files:**
- Modify: `docs/progress-2026-10-16.md`, `docs/superpowers/specs/2026-10-15-role-redesign-design.md`, `docs/superpowers/specs/2026-10-17-phase6a-attendance-calendar-design.md`

- [ ] **Step 1: Look at it (the owner, on Windows, in the demo or real project).** Sign in as the coach (demo: `coach@demo.io`). Check and write down what you saw:
  1. Attendance opens on this month with today outlined and today selected; the summary shows numbers (demo: several sessions, a rate, upcoming 2).
  2. The grid has seven even columns, six rows, Monday first; days from other months are dim; checks and Xs sit under the right days; the mixed day (8 days ago) shows both.
  3. Tapping a day fills the list below it; the current mark is the filled button; tapping another corrects it and the cell and the numbers change at once.
  4. A cancelled session reads "Cancelled" with no buttons.
  5. Week switches to seven rows with sessions inline; the arrows move by a week; Today returns.
  6. Light and dark mode; and, if you can, the phone layout (cells readable, no sideways scrolling).
  If the grid draws as one column, or the cells have uneven widths, the percentage width (`FlexLayout.Basis="14.2857%"`) is not honoured on that platform: report it and the fix is a fixed cell width.
- [ ] **Step 2: Whole-branch checks.** `dotnet test tests/GymMembership.Tests`; both app builds. Use superpowers:verification-before-completion and quote the real counts.
- [ ] **Step 3: Independent review.** One fresh review (most capable model, read-only) of the 6a commits with the Review Focus section above; fix Critical and Important findings with a failing test first; list minors as deferred.
- [ ] **Step 4: Record it.** Update the spec's section 4.1 to say two calls (`CalendarAsync`, `UpcomingAsync`), mark 6a done in the progress doc table and the redesign spec, and list deferred minors. Commit:
```bash
git add docs/progress-2026-10-16.md docs/superpowers/specs/2026-10-15-role-redesign-design.md docs/superpowers/specs/2026-10-17-phase6a-attendance-calendar-design.md
git commit -m "docs: phase 6a recorded"
```
