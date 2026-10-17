using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

public class ActiveSessionRequestTests
{
    static readonly DateTime Start = DateTime.UtcNow.Date.AddDays(14).AddHours(9);
    static async Task SignIn(DemoGateway gw, string email) => await gw.SignInAsync(email, DemoGateway.DemoPassword);

    [Fact]
    public async Task An_existing_booking_blocks_new_requests_in_both_directions()
    {
        var gw = new DemoGateway();
        var svc = new SessionService(gw);
        await SignIn(gw, "coach@demo.io");
        var invite = await svc.InviteMemberAsync("u-alex", Start, Start.AddHours(1));
        Assert.False(invite.Ok);
        Assert.Equal(AppErrorKind.Conflict, invite.Error);
        Assert.Contains("active", ErrorText.For(invite.Error, invite.Message), StringComparison.OrdinalIgnoreCase);
        await SignIn(gw, "member@demo.io");
        foreach (var row in (await svc.RequestsAsync()).Value!.Rows.Where(r => r.CanCancel))
            Assert.True((await svc.CancelRequestAsync(row.Request.Id)).Ok);
        Assert.False((await svc.RequestAsync("u-coach1", Start, Start.AddHours(1), null)).Ok);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("completed")]
    [InlineData("no_show")]
    public async Task Resolving_the_booking_allows_a_new_invitation(string status)
    {
        var gw = new DemoGateway();
        var svc = new SessionService(gw);
        await SignIn(gw, "coach@demo.io");
        foreach (var row in (await svc.SessionsAsync()).Value!.Where(r => r.Session.MemberId == "u-alex" && r.Session.Status == Status.Scheduled))
            Assert.True((status == Status.Cancelled ? await svc.CancelSessionAsync(row.Session.Id) : await svc.SetSessionStatusAsync(row.Session.Id, status)).Ok);
        Assert.True((await svc.InviteMemberAsync("u-alex", Start, Start.AddHours(1))).Ok);
    }

    [Fact]
    public async Task Old_pending_requests_and_open_slots_cannot_create_a_second_booking()
    {
        var gw = new DemoGateway();
        var svc = new SessionService(gw);
        await SignIn(gw, "coach@demo.io");
        var request = (await svc.RequestsAsync()).Value!.Rows.Single(r => r.CanRespond && r.Request.MemberId == "u-alex");
        Assert.False((await svc.RespondAsync(request.Request.Id, true)).Ok);
        Assert.True((await svc.RespondAsync(request.Request.Id, false)).Ok);
        await SignIn(gw, "member@demo.io");
        var slot = (await svc.RequestsAsync()).Value!.Rows.First(r => r.Request.MemberId is null);
        Assert.False((await svc.RespondAsync(slot.Request.Id, true)).Ok);
    }

    [Fact]
    public async Task Reschedule_moves_the_existing_booking_and_does_not_unlock_new_requests()
    {
        var gw = new DemoGateway();
        var svc = new SessionService(gw);
        await SignIn(gw, "coach@demo.io");
        var session = (await svc.SessionsAsync()).Value!.Single(r => r.Session.MemberId == "u-alex" && r.Session.Status == Status.Scheduled).Session;
        Assert.True((await svc.RescheduleAsync(session.Id, Start, Start.AddHours(1))).Ok);
        await SignIn(gw, "member@demo.io");
        var reschedule = (await svc.RequestsAsync()).Value!.Rows.Single(r => r.Request.SessionId == session.Id);
        Assert.True((await svc.RespondAsync(reschedule.Request.Id, true)).Ok);
        Assert.Equal(Start, (await svc.SessionsAsync()).Value!.Single(r => r.Session.Id == session.Id).Session.ScheduledStart);
        await SignIn(gw, "coach@demo.io");
        Assert.False((await svc.InviteMemberAsync("u-alex", Start.AddDays(1), Start.AddDays(1).AddHours(1))).Ok);
    }

    [Theory]
    [InlineData("coach@demo.io")]
    [InlineData("member@demo.io")]
    public async Task Windows_form_disables_new_requests_for_an_active_booking(string email)
    {
        var gw = new DemoGateway();
        await SignIn(gw, email);
        var vm = new RequestsViewModel(new SessionService(gw)) { UseWindowsRequestForm = true };
        await vm.OnAppearingAsync();
        if (vm.IsCoach) vm.SelectedMember = vm.Members.Single(m => m.MemberId == "u-alex");
        Assert.False(vm.SubmitCommand.CanExecute(null));
    }
}
