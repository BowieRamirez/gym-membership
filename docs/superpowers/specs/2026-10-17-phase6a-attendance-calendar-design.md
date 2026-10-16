# Phase 6a: coach attendance calendar

*Written 17 Oct 2026. Part of the role redesign (`2026-10-15-role-redesign-design.md`, phase 6). Phase 6 is split in two: **6a (this document)** is the calendar; **6b** is photos (coach photo prompt, visual coach, package and amenity cards, admin upload) and gets its own spec and plan.*

## 1. Goal

A coach opens one screen and sees, for any month or week, which days their sessions went well and which did not, can fix a wrong mark in two taps, and reads the month's numbers without counting by hand.

**Success:** from the coach's "Attendance" screen, a coach can (a) see a month grid and a week list with a check, an X or nothing per day, (b) tap a day and see its sessions with their current marks, (c) change any mark, and see the grid and the summary update at once, and (d) read sessions this month, attendance rate and upcoming sessions at the top.

## 2. Decisions (agreed 17 Oct 2026)

| # | Question | Decision |
| --- | --- | --- |
| 1 | A day with mixed results | **Show both marks**: a small check and a small X together. |
| 2 | How the attendance rate is worked out | **(present + late) ÷ (present + late + absent)** for the month in view. Excused, cancelled and not-yet-marked sessions are left out. No marked sessions: show a dash, not 0%. |
| 3 | Where it lives | **Replaces the Attendance page** in the coach menu. The flat list goes; the day list gives the same Present, Late, Absent and Excused buttons, so nothing is lost. |
| 4 | Database | **No change.** The coach already reads their own sessions and attendance (RLS `training_sessions_select`, `session_attendance_select`) and `record_session_attendance` already lets them correct a mark. |

## 3. What the coach sees

- **Header:** the page title "Attendance", a **Month | Week** switch, previous and next arrows, and a **Today** button. The period in view is named in words ("October 2026", "12 to 18 Oct").
- **Summary** above the grid, three figures:
  - **Sessions this month:** every session in the month that is not cancelled.
  - **Attendance rate:** see decision 2, for example "90%", or "—" when nothing is marked yet.
  - **Upcoming:** the number of scheduled sessions from now on, plus the next one's day and time ("Next: Tue 20 Oct, 6:00 am with Alex Tan").
- **Month view:** a 7-column grid, **Monday first**. This is an assumption, not something the owner chose; it is one constant, so changing it to Sunday-first is a one-line edit. Each cell shows the day number. A day with sessions also shows:
  - a small **check** if at least one session was attended (present or late);
  - a small **X** if at least one session was absent;
  - both when both are true;
  - nothing extra when its sessions are only excused, cancelled or not yet marked.
  Today has an outline; days outside the month are dimmed; the selected day is filled. Every mark is also in the cell's screen-reader description ("Mon 12 October, 2 attended, 1 absent"), so colour and shape are never the only signal.
- **Week view:** seven stacked rows, one per day, each showing the same marks and its sessions inline. A phone reads this better than seven narrow columns.
- **Day list** below the grid (month view) or inside each row (week view): each session shows the member's name, the time, and **Present / Late / Absent / Excused**. The current mark is the filled button; tapping another corrects it. A cancelled session reads "Cancelled" and has no buttons. A day with no sessions says "No sessions this day."
- **Icons:** check, X, and the two arrows, drawn as outlines like the phase 5 icons (curves only, no arcs; see the Windows crash of 17 Oct).

## 4. How it is built

### 4.1 Data

One new call on `ISessionService`: `CalendarAsync(DateTime fromUtc, DateTime toUtc)` returns the signed-in coach's sessions that start in the range, each with the member's name and its attendance mark (or none):

```
CalendarSession(TrainingSession Session, string With, string? Mark)   // Mark is present, late, absent, excused or null
```
It lists the coach's sessions with a date-range filter, then the attendance rows for those session ids. Both are reads through the existing tables. A test in `SupabaseGatewayTests` proves the two filters are real server queries (the same safeguard as phase 5's check-ins).

The coach's own sessions only: `coach_id = me` (a coach is never shown another coach's sessions, and RLS also stops it).

### 4.2 Pure logic (in `GymMembership.Core`, tested without any UI)

A small static class `AttendanceCalendar` with no I/O:
- `MonthGrid(year, month)`: the 6×7 days to draw, Monday first, with `InMonth` flags.
- `WeekDays(anyDayInWeek)`: the seven days of that Monday-to-Sunday week.
- `DayMarks(sessions on a day)`: counts of attended, absent, excused, unmarked and cancelled, from which the check and X are derived.
- `Summary(sessionsInMonth, now)`: sessions this month, rate (a nullable number), upcoming count and the next session.

Days use the **device's local midnight**. Known ceiling: the cash summary in the database is fixed to Manila time, so on a device set to another time zone the two could differ by a day; on a Manila device they agree.

### 4.3 View model and page

- `AttendanceViewModel` is rewritten around: the view (month or week), the date in view, the selected day, the grid cells, the day's session rows, and the summary. It reuses the existing mark command (`attendance.RecordAsync`), and after a mark it reloads the range so the grid and summary stay in step. The in-flight load follows the "newest request wins" rule used in the Cashier, so a slow answer for a month the coach has left never replaces the one on screen.
- The page is plain XAML: a 7-column grid of tappable cells (the same `Tapped` + `BasePage.Row` pattern as the rest of the app), the day list, and the summary row. No new library. Cells follow the existing tokens: Panel hairline, `Numeral` type, the Ink outline for today and selection.

### 4.4 Files

| File | Change |
| --- | --- |
| `Core/Services/AttendanceCalendar.cs` | create: the pure logic above |
| `Core/Services/Services.cs` | `CalendarSession`; `ISessionService.CalendarAsync`; `SessionService.CalendarAsync` |
| `Core/Demo/DemoGateway.cs` | only if the demo needs seed sessions in past months to show the grid |
| `Core/ViewModels/StaffViewModels.cs` | rewrite `AttendanceViewModel` |
| `App/Views/AttendancePage.xaml` + `.xaml.cs` | rewrite |
| `App/Controls/Icons.cs` | add `check`, `x`, `chevron-left`, `chevron-right` |
| tests | `AttendanceCalendarTests`, `AttendanceViewModelTests`, additions to `SupabaseGatewayTests` and `IconTests` |

## 5. Testing

- **Logic, first:** a month grid that starts on the right Monday and has 42 cells; February (28 and 29 days); a month that starts on a Monday and on a Sunday; a week across a month end; a mixed day (both marks); a day of only excused sessions (no marks); cancelled sessions (no marks, not counted); today and not-today; the rate with no marked sessions (a dash), all attended (100%), and the worked example (8 present, 1 late, 1 absent, 2 excused gives 90%); upcoming counts only scheduled sessions that start after `now`.
- **View model:** switching month and week; previous, next and Today; selecting a day shows that day's sessions; marking a session changes the cell's marks and the summary; correcting a mark changes it; a slow answer for a month already left does not replace the grid; a failed refresh keeps what is on screen and shows no new error, while a failed first load shows one.
- **Server:** the date-range filter and the attendance lookup by session ids translate to real queries.
- **Icons:** the existing test runs every new outline through MAUI's parser to reject invalid numbers.
- **By hand:** Windows and the phone layout, light and dark mode, then one fresh review of the whole branch.

## 6. Risks and things to check during the build

1. Whether the date-range filter on `scheduled_start` and the `session id in (...)` lookup are accepted by the client library (proved by the server-translation test; if not, fetch the coach's sessions unfiltered by date and filter in memory, with a `ponytail:` note).
2. A 42-cell grid of tappable cells inside a scrolling page on Windows and on a phone: check it draws and scrolls (the `FlexLayout` + `BindableLayout` pattern crashed nothing in phase 5, but the new cells have their own triggers).
3. The demo has few past sessions, so the by-hand check needs demo seed sessions in earlier months; the plan adds them only if missing.

## 7. Not in this phase

Anything in 6b (coach photo prompt, visual cards, admin photo upload); a way to *remove* a mark (the database function can only set one); exporting or printing the calendar; a member-side calendar; push notifications; a session history search.
