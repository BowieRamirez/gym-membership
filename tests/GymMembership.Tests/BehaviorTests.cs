using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class BehaviorTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Theory]
    [InlineData("forbidden", AppErrorKind.Unauthorized)]
    [InlineData("Invalid login credentials", AppErrorKind.Unauthorized)]
    [InlineData("already_processed", AppErrorKind.Conflict)]
    [InlineData("overlap", AppErrorKind.Conflict)]
    [InlineData("no_active_membership", AppErrorKind.Validation)]
    [InlineData("something odd", AppErrorKind.Unknown)]
    public void Error_messages_map_to_kinds(string message, AppErrorKind expected) =>
        Assert.Equal(expected, ErrorMapper.Map(new Exception(message)));

    [Fact] public void Network_failure_is_offline() => Assert.Equal(AppErrorKind.Offline, ErrorMapper.Map(new HttpRequestException()));

    [Theory]
    [InlineData("member@demo.io", AppRole.Member)]
    [InlineData("coach@demo.io", AppRole.Coach)]
    [InlineData("employee@demo.io", AppRole.Employee)]
    [InlineData("admin@demo.io", AppRole.Admin)]
    public async Task Sign_in_resolves_the_highest_role(string email, AppRole expected)
    {
        var r = await new AuthService(new DemoGateway()).SignInAsync(email, DemoGateway.DemoPassword);
        Assert.Equal(expected, r.Value);
    }

    [Fact]
    public async Task Wrong_password_is_unauthorized()
    {
        var r = await new AuthService(new DemoGateway()).SignInAsync("member@demo.io", "nope");
        Assert.Equal(AppErrorKind.Unauthorized, r.Error);
    }

    [Fact]
    public async Task Member_only_sees_their_own_payments()
    {
        var gw = await As("member@demo.io");
        var mine = (await new MembershipService(gw).MyPaymentsAsync()).Value!;
        Assert.NotEmpty(mine);
        Assert.All(await gw.ListAsync<Payment>(), p => Assert.Equal("u-alex", p.UserId));
    }

    [Fact]
    public async Task Avail_then_staff_verify_activates_membership_and_blocks_double_verify()
    {
        var gw = await As("bea@demo.io");
        var annual = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Annual");
        var paymentId = (await new MembershipService(gw).AvailAsync(annual.Id)).Value;

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var staff = new PaymentStaffService(gw);
        Assert.True((await staff.VerifyAsync(paymentId, true)).Ok);
        Assert.Equal(AppErrorKind.Conflict, (await staff.VerifyAsync(paymentId, true)).Error);

        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var row = (await new MembershipService(gw).MyMembershipsAsync()).Value!.First(m => m.PackageName == "Annual");
        Assert.Equal("active", row.Status);
        Assert.InRange(row.DaysLeft!.Value, 364, 365);
    }

    [Fact]
    public async Task Staff_cannot_verify_their_own_payment()
    {
        var gw = await As("employee@demo.io");
        var monthly = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        var paymentId = (await new MembershipService(gw).AvailAsync(monthly.Id)).Value;
        Assert.Equal(AppErrorKind.Validation, (await new PaymentStaffService(gw).VerifyAsync(paymentId, true)).Error);
    }

    [Fact]
    public async Task Member_cannot_verify_payments()
    {
        var gw = await As("member@demo.io");
        Assert.Equal(AppErrorKind.Unauthorized, (await new PaymentStaffService(gw).VerifyAsync(1, true)).Error);
    }

    [Fact]
    public async Task Renewing_an_expired_membership_extends_from_today()
    {
        var gw = await As("carlo@demo.io");
        var expired = (await new MembershipService(gw).MyMembershipsAsync()).Value!.First(m => m.Status == "expired");
        var paymentId = (await new MembershipService(gw).RenewAsync(expired.Id)).Value;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await new PaymentStaffService(gw).VerifyAsync(paymentId, true);
        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        var renewed = (await new MembershipService(gw).MyMembershipsAsync()).Value!.First(m => m.Id == expired.Id);
        Assert.Equal("active", renewed.Status);
        Assert.InRange(renewed.DaysLeft!.Value, 29, 30);
    }

    [Fact]
    public async Task Overlapping_session_is_rejected_but_back_to_back_is_allowed()
    {
        var gw = await As("member@demo.io");
        var svc = new SessionService(gw);
        var booked = (await svc.SessionsAsync()).Value!.First(r => r.Session.Status == "scheduled").Session;
        Assert.True((await svc.RequestAsync(1, booked.ScheduledStart.AddMinutes(30), booked.ScheduledEnd.AddMinutes(30), "overlaps")).Ok);
        Assert.True((await svc.RequestAsync(1, booked.ScheduledEnd, booked.ScheduledEnd.AddHours(1), "back to back")).Ok);

        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var rows = (await svc.RequestsAsync()).Value!.Rows;
        var overlap = rows.First(r => r.Request.Message == "overlaps");
        var adjacent = rows.First(r => r.Request.Message == "back to back");
        Assert.Equal(AppErrorKind.Conflict, (await svc.RespondAsync(overlap.Request.Id, true)).Error);
        Assert.True((await svc.RespondAsync(adjacent.Request.Id, true)).Ok);
    }

    [Fact]
    public async Task Request_end_before_start_is_rejected_locally()
    {
        var gw = await As("member@demo.io");
        var t = DateTime.UtcNow.AddDays(1);
        Assert.Equal(AppErrorKind.Validation, (await new SessionService(gw).RequestAsync(1, t, t.AddHours(-1), null)).Error);
    }

    [Fact]
    public async Task A_member_cannot_approve_a_request_addressed_to_the_coach()
    {
        var gw = await As("member@demo.io");
        var svc = new SessionService(gw);
        var mine = (await svc.RequestsAsync()).Value!.Rows.First(r => r.Request.RequestedBy == "member");
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.RespondAsync(mine.Request.Id, true)).Error);
    }

    [Fact]
    public async Task Check_in_needs_an_active_membership()
    {
        var gw = await As("employee@demo.io");
        var svc = new AttendanceService(gw);
        var hits = (await svc.FindMembersAsync("")).Value!;
        Assert.True((await svc.CheckInAsync(hits.First(h => h.Username == "Alex Tan").MemberId)).Ok);
        Assert.Equal(AppErrorKind.Validation, (await svc.CheckInAsync(hits.First(h => h.Username == "Carlo Diaz").MemberId)).Error);
    }

    [Fact]
    public async Task Only_admins_see_revenue_and_inverted_ranges_are_rejected()
    {
        var gw = await As("employee@demo.io");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(AppErrorKind.Unauthorized, (await new AdminService(gw).RevenueAsync(today.AddDays(-30), today)).Error);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        Assert.NotEmpty((await admin.RevenueAsync(today.AddDays(-30), today)).Value!);
        Assert.Equal(AppErrorKind.Validation, (await admin.RevenueAsync(today, today.AddDays(-1))).Error);
    }

    [Fact]
    public async Task Promoting_a_user_to_coach_creates_a_coach_profile()
    {
        var gw = await As("admin@demo.io");
        var svc = new AdminService(gw);
        await svc.AssignRoleAsync("u-bea", "coach");
        Assert.Contains("coach", (await svc.UsersAsync()).Value!.First(u => u.UserId == "u-bea").Roles);
        Assert.Contains(await gw.ListAsync<Coach>(), c => c.UserId == "u-bea");
    }

    [Fact]
    public async Task Unknown_theme_is_rejected_and_valid_theme_is_saved()
    {
        var gw = await As("member@demo.io");
        var svc = new SettingsService(gw);
        Assert.Equal(AppErrorKind.Validation, (await svc.SaveThemeAsync("neon")).Error);
        Assert.True((await svc.SaveThemeAsync("dark")).Ok);
        Assert.Equal("dark", (await svc.LoadAsync()).Value!.Theme);
    }
}
