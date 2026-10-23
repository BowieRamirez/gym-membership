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
        public Task<AppResult<bool>> RequestAsync(string coachId, DateTime startUtc, DateTime endUtc, string? message, int? amenityId = null) => throw new NotSupportedException();
        public Task<AppResult<bool>> ProposeAsync(int requestId, DateTime startUtc, DateTime endUtc, int? amenityId, string reason) => throw new NotSupportedException();
        public Task<AppResult<string>> AcceptProposalAsync(int requestId, int version) => throw new NotSupportedException();
        public Task<AppResult<bool>> RequestAnotherTimeAsync(int requestId, DateTime startUtc, DateTime endUtc, int? amenityId, string? message) => throw new NotSupportedException();
        public Task<AppResult<bool>> DeclineAsync(int requestId, string reason) => throw new NotSupportedException();
        public Task<AppResult<bool>> ExtendAsync(int reservationId, int minutes) => throw new NotSupportedException();
        public Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc) => throw new NotSupportedException();
        public Task<AppResult<bool>> InviteMemberAsync(string memberId, DateTime startUtc, DateTime endUtc) => throw new NotSupportedException();
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
        public Task<AppResult<string?>> MyCodeAsync() => throw new NotSupportedException();
        public Task<AppResult<MemberScan>> ScanAsync(string code) => throw new NotSupportedException();
        public Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay) => throw new NotSupportedException();
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
    public async Task A_failed_look_at_another_month_says_the_summary_is_unknown_instead_of_zero()
    {
        var (server, _, vm) = await Open();
        server.FailCalendar = true;                                          // offline: the coach goes to November and it cannot load
        await vm.NextCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal("November 2026", vm.PeriodTitle);
        Assert.Equal(42, vm.Days.Count);
        Assert.Equal("—", vm.SessionsText);                                  // not "0 sessions"
        Assert.Equal("—", vm.RateText);                                      // and not 0%

        server.FailCalendar = false;                                         // back online: the numbers return
        await vm.TodayCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("4", vm.SessionsText);
        Assert.Equal("50%", vm.RateText);
    }

    [Fact]
    public async Task While_another_month_loads_the_summary_is_not_a_misleading_partial_count()
    {
        var (server, _, vm) = await Open();
        server.Hold = true;
        var moving = vm.NextCommand.ExecuteAsync(null);                      // November's answer has not come back yet
        Assert.Equal("—", vm.SessionsText);
        server.Held.Dequeue().Reply.SetResult(AppResult<IReadOnlyList<CalendarSession>>.Success([]));
        await moving;
        Assert.Equal("0", vm.SessionsText);                                  // a real, loaded, empty month is a real zero
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
