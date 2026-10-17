using System.Reflection;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

public class CoachOverviewTests
{
    static readonly DateTime Now = DateTime.Parse("2026-10-31T16:30:00Z").ToUniversalTime();
    static (CoachOverviewViewModel Vm, AdminOverviewTests.Reads Coach, AdminOverviewTests.Reads Sessions, AdminOverviewTests.Reads Chat) Create()
    {
        var coach = DispatchProxy.Create<ICoachService, AdminOverviewTests.Reads>();
        var sessions = DispatchProxy.Create<ISessionService, AdminOverviewTests.Reads>();
        var chat = DispatchProxy.Create<IChatService, AdminOverviewTests.Reads>();
        var c = (AdminOverviewTests.Reads)(object)coach;
        var s = (AdminOverviewTests.Reads)(object)sessions;
        var m = (AdminOverviewTests.Reads)(object)chat;
        Set(c, "MyCoachAsync", new Coach { UserId = "coach", IsAvailable = true });
        Set(c, "TraineesAsync", (IReadOnlyList<TraineeRow>)[]);
        Set(c, "MyShiftsAsync", (IReadOnlyList<ShiftPost>)[]);
        Set(s, "SessionsAsync", (IReadOnlyList<SessionRow>)[]);
        Set(s, "RequestsAsync", new RequestsView([], true, []));
        Set(s, "CalendarAsync", (IReadOnlyList<CalendarSession>)[]);
        Set(m, "ThreadsAsync", (IReadOnlyList<ChatThread>)[]);
        return (new(coach, sessions, chat)
        {
            Clock = () => new DateTimeOffset(Now),
            Zone = TimeZoneInfo.CreateCustomTimeZone("Test Manila", TimeSpan.FromHours(8), "Test Manila", "Test Manila")
        }, c, s, m);
    }

    static void Set<T>(AdminOverviewTests.Reads reads, string method, T value) => reads.Results[method] = Task.FromResult(AppResult<T>.Success(value));
    static CoachOverviewSummary Summary(CoachOverviewViewModel vm, string key) => vm.Summaries.Single(s => s.Key == key);
    static SessionRow Row(DateTime start, string status = Status.Scheduled, bool mine = true) =>
        new(new TrainingSession { ScheduledStart = start, ScheduledEnd = start.AddHours(1), Status = status }, "Alex", mine, mine, true);

    [Fact]
    public async Task Seven_summary_sections_use_read_only_services_and_empty_states_are_not_failures()
    {
        var (vm, c, s, m) = Create();
        await vm.RefreshAsync();
        Assert.Equal(new[] { "sessions", "requests", "trainees", "shifts", "attendance", "messages", "profile" }, vm.Summaries.Select(x => x.Key));
        Assert.All(vm.Summaries, x => Assert.False(x.IsUnavailable));
        Assert.Equal("0 sessions today", Summary(vm, "sessions").Value);
        Assert.Equal("No attendance marked", Summary(vm, "attendance").Value);
        Assert.Equal("Accepting new trainees", Summary(vm, "profile").Value);
        Assert.All(c.Calls, x => Assert.Contains(x.Method, new[] { "TraineesAsync", "MyShiftsAsync", "MyCoachAsync" }));
        Assert.All(s.Calls, x => Assert.Contains(x.Method, new[] { "SessionsAsync", "RequestsAsync", "CalendarAsync" }));
        Assert.Equal("ThreadsAsync", Assert.Single(m.Calls).Method);
    }

    [Fact]
    public async Task Today_and_month_boundaries_follow_the_calendar_timezone_not_UTC()
    {
        var (vm, _, s, _) = Create();
        Set(s, "SessionsAsync", (IReadOnlyList<SessionRow>)[Row(Now.AddMinutes(-20)), Row(Now.AddHours(-2)), Row(Now.AddHours(1), Status.Cancelled), Row(Now.AddHours(1), mine: false)]);
        await vm.RefreshAsync();
        Assert.Equal("1 session today", Summary(vm, "sessions").Value);
        Assert.Contains("1 upcoming or in progress", Summary(vm, "sessions").Detail);
        Assert.Contains("Alex", Summary(vm, "sessions").Detail);
        Assert.Contains("Sunday, 1 Nov 2026", vm.UpdatedText);
        var range = Assert.Single(s.Calls, c => c.Method == "CalendarAsync").Args;
        Assert.Equal(DateTime.Parse("2026-10-31T16:00:00Z").ToUniversalTime(), range[0]);
        Assert.Equal(DateTime.Parse("2026-11-30T16:00:00Z").ToUniversalTime(), range[1]);
    }

    [Fact]
    public async Task Requests_distinguish_incoming_replies_from_sent_invitations_reschedules_and_open_slots()
    {
        var (vm, _, s, _) = Create();
        RequestRow R(string by, bool respond, bool cancel, string? member = "m", int? session = null, string status = Status.Pending) =>
            new(new TimeRequest { RequestedBy = by, MemberId = member, SessionId = session, Status = status }, "Member", "", respond, cancel);
        Set(s, "RequestsAsync", new RequestsView([R("member", true, false), R("member", true, false, session: 1),
            R("coach", false, true), R("coach", false, true, session: 2), R("coach", false, true, member: null),
            R("member", true, false, status: Status.Rejected)], true, []));
        await vm.RefreshAsync();
        Assert.Equal("2 awaiting your reply", Summary(vm, "requests").Value);
        Assert.Contains("1 invitation awaiting members", Summary(vm, "requests").Detail);
        Assert.Contains("1 reschedule awaiting members; 1 open slot", Summary(vm, "requests").Detail);
    }

    [Fact]
    public async Task Attendance_uses_the_calendar_rate_excluding_excused_unmarked_and_cancelled()
    {
        var (vm, _, s, _) = Create();
        CalendarSession Mark(string? mark, string status = Status.Completed, DateTime? start = null) =>
            new(new TrainingSession { ScheduledStart = start ?? Now.AddHours(1), Status = status }, "Member", mark);
        Set(s, "CalendarAsync", (IReadOnlyList<CalendarSession>)[Mark("late"), Mark("absent"), Mark("excused"),
            Mark(null, Status.Scheduled), Mark("present", Status.Cancelled), Mark("present", start: Now.AddDays(-1))]);
        await vm.RefreshAsync();
        Assert.Equal("50% attendance", Summary(vm, "attendance").Value);
        Assert.Contains("November 2026: 4 sessions", Summary(vm, "attendance").Detail);
        Assert.Contains("1 attended, 1 absent, 1 excused", Summary(vm, "attendance").Detail);
    }

    [Fact]
    public async Task Trainees_are_distinct_shifts_are_current_and_messages_do_not_include_support()
    {
        var (vm, c, _, m) = Create();
        Set(c, "TraineesAsync", (IReadOnlyList<TraineeRow>)[new(1, "c", "a", "Alex", "", 0), new(2, "c", "a", "Alex", "", 0), new(3, "c", "b", "Bea", "", 0)]);
        Set(c, "MyShiftsAsync", (IReadOnlyList<ShiftPost>)[new() { Start = Now.AddMinutes(-10), End = Now.AddHours(1) },
            new() { Start = Now.AddHours(-2), End = Now }, new() { Start = Now, End = Now.AddHours(1), Status = Status.Cancelled }]);
        Set(m, "ThreadsAsync", (IReadOnlyList<ChatThread>)[new() { Unread = 3 }, new() { Unread = 0 }, new() { Kind = "support", Unread = 99 }]);
        Set(c, "MyCoachAsync", new Coach { IsAvailable = false, Specialty = "Strength", HourlyRate = 500 });
        await vm.RefreshAsync();
        Assert.Equal("2 active trainees", Summary(vm, "trainees").Value);
        Assert.Equal("1 scheduled shift", Summary(vm, "shifts").Value);
        Assert.Equal("3 unread messages", Summary(vm, "messages").Value);
        Assert.Contains("Across 1 coaching conversation", Summary(vm, "messages").Detail);
        Assert.Equal("Hiring paused", Summary(vm, "profile").Value);
        Assert.Contains("500.00", Summary(vm, "profile").Detail);
    }

    [Fact]
    public async Task Failed_or_thrown_reads_do_not_show_false_zeros_and_refresh_clears_old_values()
    {
        var (vm, c, s, _) = Create();
        Set(c, "TraineesAsync", (IReadOnlyList<TraineeRow>)[new(1, "c", "a", "Alex", "", 0)]);
        await vm.RefreshAsync();
        c.Results["TraineesAsync"] = Task.FromResult(AppResult<IReadOnlyList<TraineeRow>>.Fail(AppErrorKind.Unauthorized, "forbidden"));
        s.Results["SessionsAsync"] = Task.FromException<AppResult<IReadOnlyList<SessionRow>>>(new Exception("network unavailable"));
        await vm.RefreshAsync();
        Assert.True(Summary(vm, "trainees").IsUnavailable);
        Assert.Equal("Unavailable", Summary(vm, "trainees").Value);
        Assert.DoesNotContain("1 active", Summary(vm, "trainees").Value);
        Assert.True(Summary(vm, "sessions").IsUnavailable);
        Assert.Equal("0 unread messages", Summary(vm, "messages").Value);
        Assert.Contains("2 summaries unavailable", vm.UpdatedText);
        Set(c, "TraineesAsync", (IReadOnlyList<TraineeRow>)[]);
        Set(s, "SessionsAsync", (IReadOnlyList<SessionRow>)[]);
        await vm.RefreshAsync();
        Assert.All(vm.Summaries, x => Assert.False(x.IsUnavailable));
        Assert.Equal("0 active trainees", Summary(vm, "trainees").Value);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Refresh_cannot_overlap_a_refresh_already_loading()
    {
        var (vm, _, s, _) = Create();
        var pending = new TaskCompletionSource<AppResult<IReadOnlyList<SessionRow>>>();
        s.Results["SessionsAsync"] = pending.Task;
        var first = vm.RefreshAsync();
        Assert.True(vm.IsBusy);
        await vm.RefreshAsync();
        Assert.Single(s.Calls, c => c.Method == "SessionsAsync");
        pending.SetResult(AppResult<IReadOnlyList<SessionRow>>.Success([]));
        await first;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Real_demo_services_load_a_coach_summary_without_marking_messages_read()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw));
        var before = (await chat.ThreadsAsync()).Value!.Sum(t => t.Unread);
        var vm = new CoachOverviewViewModel(new CoachService(gw), new SessionService(gw), chat);
        await vm.RefreshAsync();
        Assert.All(vm.Summaries, s => Assert.False(s.IsUnavailable, s.Detail));
        Assert.Equal(before, (await chat.ThreadsAsync()).Value!.Sum(t => t.Unread));
    }
}
