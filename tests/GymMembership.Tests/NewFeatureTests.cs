using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class NewFeatureTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    // ---------- admin adds users ----------
    [Fact]
    public async Task Admin_adds_a_coach_who_can_sign_in_with_the_temporary_password()
    {
        var gw = await As("admin@demo.io");
        Assert.True((await new AdminService(gw).CreateUserAsync("Rey Santos", "rey@demo.io", "coach", "temp1234")).Ok);

        var wrong = await new AuthService(gw).SignInAsync("rey@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, wrong.Error);
        var ok = await new AuthService(gw).SignInAsync("rey@demo.io", "temp1234");
        Assert.Equal(AppRole.Coach, ok.Value);
        Assert.Contains(await gw.ListAsync<Coach>(), c => c.UserId == gw.CurrentUserId);
    }

    [Fact]
    public async Task Adding_a_user_rejects_bad_input_duplicates_and_non_admins()
    {
        var gw = await As("admin@demo.io");
        var svc = new AdminService(gw);
        Assert.Equal(AppErrorKind.Validation, (await svc.CreateUserAsync("", "x@demo.io", "member", "temp1234")).Error);
        Assert.Equal(AppErrorKind.Validation, (await svc.CreateUserAsync("X", "no-at-sign", "member", "temp1234")).Error);
        Assert.Equal(AppErrorKind.Validation, (await svc.CreateUserAsync("X", "x@demo.io", "member", "123")).Error);
        Assert.Equal(AppErrorKind.Validation, (await svc.CreateUserAsync("X", "x@demo.io", "admin", "temp1234")).Error);
        Assert.Equal(AppErrorKind.Validation, (await svc.CreateUserAsync("Dupe", "member@demo.io", "member", "temp1234")).Error);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.CreateUserAsync("Y", "y@demo.io", "member", "temp1234")).Error);
    }

    // ---------- discounts ----------
    [Fact]
    public async Task A_valid_code_cuts_the_payment_and_a_bad_or_expired_code_is_refused()
    {
        var gw = await As("bea@demo.io");
        var svc = new MembershipService(gw);
        var annual = (await svc.ListPackagesAsync()).Value!.First(p => p.Name == "Annual");

        Assert.Equal(AppErrorKind.Validation, (await svc.AvailAsync(annual.Id, "NOPE")).Error);
        Assert.Equal(AppErrorKind.Validation, (await svc.AvailAsync(annual.Id, "NEWYEAR20")).Error); // seeded as expired

        var paymentId = (await svc.AvailAsync(annual.Id, " welcome10 ")).Value;
        var item = (await svc.MyPaymentsAsync()).Value!.First(p => p.Id == paymentId);
        Assert.Equal(252m, item.Amount); // 280 less 10%
        Assert.Contains("WELCOME10", item.What);
    }

    [Fact]
    public async Task Members_cannot_manage_or_read_discounts_and_admins_can_delete_them()
    {
        var gw = await As("member@demo.io");
        Assert.Empty(await gw.ListAsync<Discount>());
        Assert.Equal(AppErrorKind.Unauthorized, (await new AdminService(gw).SaveDiscountAsync(new Discount { Code = "HACK", Percent = 100, ExpiresAt = DateTime.UtcNow.AddDays(1) })).Error);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        Assert.Equal(AppErrorKind.Validation, (await admin.SaveDiscountAsync(new Discount { Code = "", Percent = 10 })).Error);
        Assert.Equal(AppErrorKind.Validation, (await admin.SaveDiscountAsync(new Discount { Code = "BIG", Percent = 150 })).Error);

        var rows = (await admin.DiscountsAsync()).Value!;
        Assert.Equal("expired", rows.First(r => r.Discount.Code == "NEWYEAR20").State);
        Assert.True((await admin.DeleteDiscountAsync(rows.First(r => r.Discount.Code == "NEWYEAR20").Discount)).Ok);
        Assert.DoesNotContain((await admin.DiscountsAsync()).Value!, r => r.Discount.Code == "NEWYEAR20");
    }

    // ---------- plans: edit and delete ----------
    [Fact]
    public async Task A_plan_in_use_cannot_be_deleted_but_an_unused_one_can()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var plans = (await admin.AllPackagesAsync()).Value!;

        Assert.Equal(AppErrorKind.Conflict, (await admin.DeletePackageAsync(plans.First(p => p.Name == "Monthly"))).Error); // Alex has it
        Assert.True((await admin.DeletePackageAsync(plans.First(p => p.Name == "Annual"))).Ok);

        var quarterly = plans.First(p => p.Name == "Quarterly");
        quarterly.Price = 75;
        Assert.True((await admin.SavePackageAsync(quarterly)).Ok);
        Assert.Equal(75m, (await admin.AllPackagesAsync()).Value!.First(p => p.Name == "Quarterly").Price);
    }

    // ---------- messaging ----------
    [Fact]
    public async Task Member_and_coach_can_chat_only_while_hired()
    {
        var gw = await As("member@demo.io");
        var svc = new MessageService(gw);
        var thread = (await svc.ThreadsAsync()).Value!.Single();
        Assert.True(thread.CanSend);
        Assert.True((await svc.SendAsync(thread.CoachId, thread.MemberId, "  See you at 9  ")).Ok);

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var seen = (await svc.MessagesAsync(thread.CoachId, thread.MemberId)).Value!;
        Assert.Equal("See you at 9", seen.Last().Message.Body);
        Assert.False(seen.Last().Mine);
        Assert.Contains(await gw.ListAsync<UserNotification>(), n => n.Type == "message");
    }

    [Fact]
    public async Task Chat_rejects_empty_text_outsiders_and_ended_hires()
    {
        var gw = await As("member@demo.io");
        var svc = new MessageService(gw);
        var thread = (await svc.ThreadsAsync()).Value!.Single();
        Assert.Equal(AppErrorKind.Validation, (await svc.SendAsync(thread.CoachId, thread.MemberId, "   ")).Error);

        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword); // never hired Lena
        Assert.Empty((await svc.MessagesAsync(thread.CoachId, thread.MemberId)).Value!);
        Assert.Equal(AppErrorKind.Validation, (await svc.SendAsync(thread.CoachId, thread.MemberId, "hi")).Error);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var hire = (await gw.ListAsync<CoachHire>()).First(h => h.Status == "active");
        Assert.True((await new CoachService(gw).EndHireAsync(hire.Id)).Ok);
        Assert.False((await svc.ThreadsAsync()).Value!.Single().CanSend); // history stays, sending stops
        Assert.Equal(AppErrorKind.Validation, (await svc.SendAsync(thread.CoachId, thread.MemberId, "still there?")).Error);
    }

    // ---------- shifts and trainees ----------
    [Fact]
    public async Task Coach_shifts_show_on_the_coach_card_and_cancelled_ones_disappear()
    {
        var gw = await As("coach@demo.io");
        var coaches = new CoachService(gw);
        var start = DateTime.UtcNow.AddDays(5);
        Assert.Equal(AppErrorKind.Validation, (await coaches.PostShiftAsync(start, start.AddHours(-1), null)).Error);
        Assert.True((await coaches.PostShiftAsync(start, start.AddHours(4), "Late shift")).Ok);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var card = (await coaches.ListCoachesAsync()).Value!.First(c => c.Name == "Lena Cruz");
        Assert.Contains(card.Shifts!, s => s.Note == "Late shift");
        Assert.Contains("Late shift", card.ShiftsText);

        Assert.Equal(AppErrorKind.Validation, (await coaches.PostShiftAsync(start, start.AddHours(2), null)).Error); // a member has no coach profile

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var mine = (await coaches.MyShiftsAsync()).Value!.First(s => s.Note == "Late shift");
        Assert.True((await coaches.CancelShiftAsync(mine.Id)).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.DoesNotContain((await coaches.ListCoachesAsync()).Value!.First(c => c.Name == "Lena Cruz").Shifts!, s => s.Note == "Late shift");
    }

    [Fact]
    public async Task A_coach_sees_only_their_own_trainees()
    {
        var gw = await As("coach@demo.io");
        var names = (await new CoachService(gw).TraineesAsync()).Value!.Select(t => t.Name).ToList();
        Assert.Equal(new[] { "Alex Tan", "Bea Lim" }, names);

        await gw.SignInAsync("kai@demo.io", DemoGateway.DemoPassword);
        Assert.Empty((await new CoachService(gw).TraineesAsync()).Value!);
    }

    // ---------- reschedule and cancel ----------
    [Fact]
    public async Task Rescheduling_moves_the_same_session_once_the_other_side_approves()
    {
        var gw = await As("member@demo.io");
        var sessions = new SessionService(gw);
        var booked = (await sessions.SessionsAsync()).Value!.First(r => r.Session.Status == "scheduled").Session;
        var start = booked.ScheduledStart.AddDays(1);

        Assert.True((await sessions.RescheduleAsync(booked.Id, start, start.AddHours(1))).Ok);
        Assert.Equal(AppErrorKind.Conflict, (await sessions.RescheduleAsync(booked.Id, start, start.AddHours(1))).Error); // one open proposal at a time

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var proposal = (await sessions.RequestsAsync()).Value!.Rows.First(r => r.Request.SessionId == booked.Id);
        Assert.Equal("Reschedule asked by member", proposal.Kind);
        Assert.True(proposal.CanRespond);
        Assert.True((await sessions.RespondAsync(proposal.Request.Id, true)).Ok);

        var after = (await sessions.SessionsAsync()).Value!.Select(r => r.Session).Where(s => s.Status == "scheduled").ToList();
        Assert.Single(after, s => s.Id == booked.Id && s.ScheduledStart == start); // moved, not duplicated
    }

    [Fact]
    public async Task A_coach_can_propose_a_new_time_and_the_member_answers_it()
    {
        var gw = await As("coach@demo.io");
        var sessions = new SessionService(gw);
        var booked = (await sessions.SessionsAsync()).Value!.First(r => r.Session.Status == "scheduled").Session;
        Assert.Equal(AppErrorKind.Validation, (await sessions.RescheduleAsync(booked.Id, booked.ScheduledStart, booked.ScheduledStart.AddHours(-1))).Error);

        var start = booked.ScheduledStart.AddDays(2);
        Assert.True((await sessions.RescheduleAsync(booked.Id, start, start.AddHours(1))).Ok);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var proposal = (await sessions.RequestsAsync()).Value!.Rows.First(r => r.Request.SessionId == booked.Id);
        Assert.True(proposal.CanRespond);
        Assert.False(proposal.CanCancel);
    }

    [Fact]
    public async Task Cancelling_a_session_works_for_either_side_and_drops_its_open_reschedule()
    {
        var gw = await As("member@demo.io");
        var sessions = new SessionService(gw);
        var booked = (await sessions.SessionsAsync()).Value!.First(r => r.Session.Status == "scheduled").Session;
        var start = booked.ScheduledStart.AddDays(1);
        await sessions.RescheduleAsync(booked.Id, start, start.AddHours(1));

        Assert.True((await sessions.CancelSessionAsync(booked.Id)).Ok);
        Assert.Equal(AppErrorKind.Conflict, (await sessions.CancelSessionAsync(booked.Id)).Error); // already cancelled

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.DoesNotContain((await sessions.RequestsAsync()).Value!.Rows, r => r.Request.SessionId == booked.Id);
        Assert.Contains(await gw.ListAsync<UserNotification>(), n => n.Type == "session");

        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await sessions.CancelSessionAsync(booked.Id)).Error); // not their session
    }
}
