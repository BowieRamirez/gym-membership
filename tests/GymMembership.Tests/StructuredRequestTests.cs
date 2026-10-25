using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 7 in demo mode: Alex (member@demo.io) asks Lena (coach@demo.io) for a Pool session; Lena proposes another time;
// Alex accepts that exact proposal, asks for another time, or declines. Times are Manila wall-clock five days ahead.
public class StructuredRequestTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static readonly DateTime Day = DateTime.UtcNow.AddHours(8).Date.AddDays(5);   // a Manila date
    static DateTime Manila(int h, int m = 0) => DateTime.SpecifyKind(Day.AddHours(h - 8).AddMinutes(m), DateTimeKind.Utc);

    static async Task<(DemoGateway Gw, SessionService Svc, int Pool, Clock Clock)> Setup()
    {
        var clock = new Clock();
        var gw = new DemoGateway(clock: clock);
        var svc = new SessionService(gw);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        foreach (var row in (await svc.SessionsAsync()).Value!.Where(r => r.Session.MemberId == "u-alex" && r.Session.Status == Status.Scheduled))
            Assert.True((await svc.CancelSessionAsync(row.Session.Id)).Ok);              // clear Alex's seeded booking with Lena
        Assert.True((await new CoachService(gw).PostShiftAsync(Manila(6), Manila(22), null)).Ok);   // Lena opens the whole day
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        foreach (var row in (await svc.RequestsAsync()).Value!.Rows.Where(r => r.CanCancel))
            Assert.True((await svc.CancelRequestAsync(row.Request.Id)).Ok);
        var pool = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Pool").Id;
        return (gw, svc, pool, clock);
    }

    static async Task<RequestRow> Row(SessionService svc) => (await svc.RequestsAsync()).Value!.Rows.Single(r => r.Request.MemberId == "u-alex" && r.Request.CoachId == "u-coach1");

    [Fact]
    public async Task A_member_accepts_the_coachs_proposal_and_gets_the_session_and_its_amenity()
    {
        var (gw, svc, pool, _) = await Setup();
        Assert.True((await svc.RequestAsync("u-coach1", Manila(10), Manila(11), "Swim drills", pool)).Ok);
        Assert.Equal("Pool", (await Row(svc)).Amenity);

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var asked = await Row(svc);
        Assert.True(asked.CanPropose);
        Assert.Equal("bad_reason", (await svc.ProposeAsync(asked.Request.Id, Manila(12), Manila(13), pool, " ")).Message);
        Assert.True((await svc.ProposeAsync(asked.Request.Id, Manila(12), Manila(13), pool, "The pool is quieter at noon")).Ok);
        Assert.False((await Row(svc)).CanPropose);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var proposal = await Row(svc);
        Assert.True(proposal.CanAccept);
        Assert.Contains("The pool is quieter at noon", proposal.ProposalText);
        Assert.Equal("stale_proposal", (await svc.AcceptProposalAsync(proposal.Request.Id, proposal.Request.Version - 1)).Message);
        Assert.Equal("confirmed", (await svc.AcceptProposalAsync(proposal.Request.Id, proposal.Request.Version)).Value);

        var session = (await svc.SessionsAsync()).Value!.Single(r => r.Session.CoachId == "u-coach1" && r.Session.Status == Status.Scheduled).Session;
        Assert.Equal(Manila(12), session.ScheduledStart);
        var booking = Assert.Single((await new ReservationService(gw).MyBookingsAsync()).Value!);
        Assert.Equal(("Pool", "u-coach1"), (booking.Amenity, booking.Booking.CoachId));
    }

    [Fact]
    public async Task An_expired_proposal_goes_back_to_the_coach()
    {
        var (gw, svc, pool, clock) = await Setup();
        await svc.RequestAsync("u-coach1", Manila(10), Manila(11), null, pool);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        await svc.ProposeAsync((await Row(svc)).Request.Id, Manila(12), Manila(13), pool, "Noon is better");
        clock.Now = clock.Now.AddHours(25);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        // the screen judges expiry by the device clock; the server (here the demo clock) decides, so a late Accept is refused there
        var stale = await Row(svc);
        Assert.Equal("expired", (await svc.AcceptProposalAsync(stale.Request.Id, stale.Request.Version)).Value);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.True((await Row(svc)).CanPropose);
    }

    [Fact]
    public async Task Request_another_time_goes_back_to_the_same_coach()
    {
        var (gw, svc, pool, _) = await Setup();
        await svc.RequestAsync("u-coach1", Manila(10), Manila(11), null, pool);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        await svc.ProposeAsync((await Row(svc)).Request.Id, Manila(12), Manila(13), pool, "Noon is better");
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.RequestAnotherTimeAsync((await Row(svc)).Request.Id, Manila(16), Manila(17), null, "After work")).Ok);

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var again = await Row(svc);
        Assert.Equal((Manila(16), (int?)null), (again.Request.RequestedStart, again.Request.AmenityId));
        Assert.True(again.CanRespond);
        Assert.True((await svc.RespondAsync(again.Request.Id, true)).Ok);
    }

    [Fact]
    public async Task The_decline_panel_needs_a_reason()
    {
        var (gw, svc, pool, _) = await Setup();
        await svc.RequestAsync("u-coach1", Manila(10), Manila(11), null, pool);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new RequestsViewModel(svc);
        await vm.OnAppearingAsync();
        var row = vm.Requests.Single(r => r.Request.MemberId == "u-alex");

        vm.StartDeclineCommand.Execute(row);
        Assert.Same(row, vm.Responding);
        Assert.Equal($"Decline the request from {row.With}", vm.ResponseTitle);
        await vm.SubmitResponseCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Same(row, vm.Responding);

        vm.ResponseReason = "Fully booked that day";
        await vm.SubmitResponseCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Responding);
        Assert.DoesNotContain(vm.Requests, r => r.Request.Id == row.Request.Id);
    }

    [Fact]
    public async Task The_coach_adds_time_to_an_amenity_session_and_both_ends_move()
    {
        var (gw, svc, pool, _) = await Setup();
        await svc.RequestAsync("u-coach1", Manila(10), Manila(11), null, pool);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.RespondAsync((await Row(svc)).Request.Id, true)).Ok);

        var vm = new SessionsViewModel(svc);
        await vm.OnAppearingAsync();
        var row = vm.Sessions.Single(r => r.Session.MemberId == "u-alex" && r.Session.Status == Status.Scheduled);
        Assert.Equal("Pool", row.Amenity);
        Assert.True(row.CanExtend);
        await vm.ExtendCommand.ExecuteAsync(row);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(Manila(11, 30), vm.Sessions.Single(r => r.Session.Id == row.Session.Id).Session.ScheduledEnd);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.EndsWith("10:00–11:30", (await new ReservationService(gw).MyBookingsAsync()).Value!.Single().When);
    }

    [Fact]
    public async Task An_amenity_session_outside_the_coachs_shift_is_refused_and_stays_waiting()
    {
        var (gw, svc, pool, _) = await Setup();
        var otherDay = DateTime.SpecifyKind(Day.AddDays(1).AddHours(2), DateTimeKind.Utc);   // 10:00 Manila the next day: no shift
        Assert.True((await svc.RequestAsync("u-coach1", otherDay, otherDay.AddHours(1), null, pool)).Ok);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var row = await Row(svc);
        Assert.Equal("coach_unavailable", (await svc.RespondAsync(row.Request.Id, true)).Message);
        Assert.True((await Row(svc)).CanRespond);
    }
}
