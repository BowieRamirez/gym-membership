using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

public class SessionRequestRulesTests
{
    static async Task SignIn(DemoGateway gw, string email) => await gw.SignInAsync(email, DemoGateway.DemoPassword);
    static readonly DateTime Start = DateTime.UtcNow.Date.AddDays(12).AddHours(9);
    static async Task ClearBookings(DemoGateway gw)
    {
        await SignIn(gw, "coach@demo.io");
        var svc = new SessionService(gw);
        foreach (var row in (await svc.SessionsAsync()).Value!.Where(r => r.Session.MemberId == "u-alex" && r.Session.Status == Status.Scheduled))
            Assert.True((await svc.CancelSessionAsync(row.Session.Id)).Ok);
    }

    static async Task ClearMine(SessionService svc)
    {
        foreach (var row in (await svc.RequestsAsync()).Value!.Rows.Where(r => r.CanCancel))
            Assert.True((await svc.CancelRequestAsync(row.Request.Id)).Ok);
    }

    [Fact]
    public async Task A_member_cannot_send_two_pending_requests_to_the_same_coach()
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        await SignIn(gw, "member@demo.io");
        var svc = new SessionService(gw);
        var result = await svc.RequestAsync("u-coach1", Start, Start.AddHours(1), null);
        Assert.False(result.Ok);
        Assert.Equal(AppErrorKind.Conflict, result.Error);
        Assert.Contains("pending request", ErrorText.For(result.Error, result.Message));
        await ClearMine(svc);
        Assert.True((await svc.RequestAsync("u-coach1", Start, Start.AddHours(1), null)).Ok);
        Assert.False((await svc.RequestAsync("u-coach1", Start.AddDays(1), Start.AddDays(1).AddHours(1), null)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_rejection_allows_another_request_while_an_accepted_session_is_active(bool approve)
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        var svc = new SessionService(gw);
        await SignIn(gw, "member@demo.io");
        await ClearMine(svc);
        Assert.True((await svc.RequestAsync("u-coach1", Start, Start.AddHours(1), null)).Ok);
        var pending = (await svc.RequestsAsync()).Value!.Rows.Single(r => r.CanCancel);
        await SignIn(gw, "coach@demo.io");
        Assert.True((await svc.RespondAsync(pending.Request.Id, approve)).Ok);
        await SignIn(gw, "member@demo.io");
        Assert.Equal(!approve, (await svc.RequestAsync("u-coach1", Start.AddDays(1), Start.AddDays(1).AddHours(1), null)).Ok);
    }

    [Fact]
    public async Task Targeted_invitation_is_visible_only_to_the_coach_and_selected_trainee()
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        var svc = new SessionService(gw);
        await SignIn(gw, "coach@demo.io");
        var view = (await svc.RequestsAsync()).Value!;
        Assert.Contains(view.Trainees!, m => m.MemberId == "u-alex");
        Assert.True((await svc.InviteMemberAsync("u-alex", Start, Start.AddHours(1))).Ok);
        var invite = (await svc.RequestsAsync()).Value!.Rows.Single(r => r.Request.RequestedBy == "coach" && r.Request.MemberId == "u-alex");
        Assert.True(invite.CanCancel);
        Assert.False(invite.CanRespond);
        await SignIn(gw, "bea@demo.io");
        Assert.DoesNotContain((await svc.RequestsAsync()).Value!.Rows, r => r.Request.Id == invite.Request.Id);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.RespondAsync(invite.Request.Id, true)).Error);
        await SignIn(gw, "member@demo.io");
        Assert.True((await svc.RequestsAsync()).Value!.Rows.Single(r => r.Request.Id == invite.Request.Id).CanRespond);
        Assert.True((await svc.RespondAsync(invite.Request.Id, true)).Ok);
        Assert.Contains((await svc.SessionsAsync()).Value!, s => s.Session.ScheduledStart == Start && s.Session.CoachId == "u-coach1");
    }

    [Fact]
    public async Task Members_cannot_invite_other_members_and_coaches_cannot_invite_unhired_members()
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        var svc = new SessionService(gw);
        await SignIn(gw, "member@demo.io");
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.InviteMemberAsync("u-bea", Start, Start.AddHours(1))).Error);
        await SignIn(gw, "kai@demo.io");
        Assert.Equal(AppErrorKind.Validation, (await svc.InviteMemberAsync("u-alex", Start, Start.AddHours(1))).Error);
    }

    [Fact]
    public async Task Windows_member_submit_is_blocked_for_the_selected_pending_coach_but_can_cancel()
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        await SignIn(gw, "member@demo.io");
        var vm = new RequestsViewModel(new SessionService(gw)) { UseWindowsRequestForm = true };
        await vm.OnAppearingAsync();
        Assert.True(vm.HasPendingRequest);
        Assert.True(vm.ShowPendingRequest);
        Assert.False(vm.SubmitCommand.CanExecute(null));
        await vm.CancelCommand.ExecuteAsync(vm.Requests.First(r => r.CanCancel));
        Assert.False(vm.HasPendingRequest);
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Windows_coach_must_select_a_trainee_and_the_submit_creates_a_direct_invitation()
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        await SignIn(gw, "coach@demo.io");
        var svc = new SessionService(gw);
        var vm = new RequestsViewModel(svc) { UseWindowsRequestForm = true };
        await vm.OnAppearingAsync();
        Assert.True(vm.ShowMemberPicker);
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.SelectedMember = vm.Members.Single(m => m.MemberId == "u-alex");
        vm.Date = Start.Date;
        vm.Time = Start.TimeOfDay;
        Assert.True(vm.SubmitCommand.CanExecute(null));
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Contains((await svc.RequestsAsync()).Value!.Rows, r => r.Request.RequestedBy == "coach" && r.Request.MemberId == "u-alex");
    }

    [Fact]
    public async Task Switching_coaches_only_blocks_the_one_with_a_pending_member_request()
    {
        var svc = System.Reflection.DispatchProxy.Create<ISessionService, AdminOverviewTests.Reads>();
        var reads = (AdminOverviewTests.Reads)(object)svc;
        var a = new CoachCard(new Coach { UserId = "a" }, "Coach A", true, null);
        var b = new CoachCard(new Coach { UserId = "b" }, "Coach B", true, null);
        var pending = new RequestRow(new TimeRequest { CoachId = "a", MemberId = "member", RequestedBy = "member" }, "Coach A", "Requested by member", false, true);
        reads.Results["RequestsAsync"] = Task.FromResult(AppResult<RequestsView>.Success(new([pending], false, [a, b])));
        var vm = new RequestsViewModel(svc) { UseWindowsRequestForm = true };
        await vm.OnAppearingAsync();
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.SelectedCoach = b;
        Assert.False(vm.HasPendingRequest);
        Assert.True(vm.SubmitCommand.CanExecute(null));
        vm.SelectedCoach = a;
        Assert.True(vm.ShowPendingRequest);
        Assert.False(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_removed_trainee_is_not_kept_selected_after_refresh()
    {
        var svc = System.Reflection.DispatchProxy.Create<ISessionService, AdminOverviewTests.Reads>();
        var reads = (AdminOverviewTests.Reads)(object)svc;
        var member = new RequestMember("member", "Member");
        reads.Results["RequestsAsync"] = Task.FromResult(AppResult<RequestsView>.Success(new([], true, [], [member])));
        var vm = new RequestsViewModel(svc) { UseWindowsRequestForm = true };
        await vm.OnAppearingAsync();
        vm.SelectedMember = member;
        Assert.True(vm.SubmitCommand.CanExecute(null));
        reads.Results["RequestsAsync"] = Task.FromResult(AppResult<RequestsView>.Success(new([], true, [], [])));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedMember);
        Assert.False(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void Unique_index_errors_have_the_same_friendly_message_as_the_demo_guard()
    {
        const string raw = "23505 duplicate key value violates unique constraint time_requests_one_pending_member_per_coach";
        Assert.Equal(AppErrorKind.Conflict, ErrorMapper.Map(new Exception(raw)));
        Assert.Equal(ErrorText.For(AppErrorKind.Conflict, "pending_session_request"), ErrorText.For(AppErrorKind.Conflict, raw));
    }

    [Fact]
    public async Task Non_Windows_coach_keeps_the_existing_open_slot_form()
    {
        var gw = new DemoGateway();
        await ClearBookings(gw);
        await SignIn(gw, "coach@demo.io");
        var vm = new RequestsViewModel(new SessionService(gw));
        await vm.OnAppearingAsync();
        Assert.False(vm.ShowMemberPicker);
        Assert.Equal("Offer an open slot", vm.ComposeTitle);
        Assert.Equal("Post slot", vm.SubmitText);
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }
}
