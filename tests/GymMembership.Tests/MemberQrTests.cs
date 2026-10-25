using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 10 (D12) in demo mode: every member has one permanent code; the front desk scans it and sees live status
// (Alex active, Bea pending, Carlo expired); a scan never checks in by itself, Check in does, and only for Active.
public class MemberQrTests
{
    static async Task<(DemoGateway Gw, AttendanceService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new AttendanceService(gw));
    }

    static async Task<string> CodeOf(DemoGateway gw, string email)
    {
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (await new AttendanceService(gw).MyCodeAsync()).Value!;
    }

    [Fact]
    public async Task Each_member_has_one_code_that_stays_the_same_and_staff_have_none()
    {
        var (gw, svc) = await As("member@demo.io");
        var code = (await svc.MyCodeAsync()).Value;
        Assert.Matches("^GYM1-[0-9a-f]{32}$", code);
        Assert.Equal(code, (await svc.MyCodeAsync()).Value);
        Assert.NotEqual(code, await CodeOf(gw, "bea@demo.io"));
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.Null((await svc.MyCodeAsync()).Value);
    }

    [Fact]
    public async Task The_front_desk_sees_live_status_for_each_code()
    {
        var gw = new DemoGateway();
        string alex = await CodeOf(gw, "member@demo.io"), bea = await CodeOf(gw, "bea@demo.io"), carlo = await CodeOf(gw, "carlo@demo.io");
        var (_, svc) = await As("employee@demo.io", gw);

        var a = (await svc.ScanAsync(alex)).Value!;
        Assert.Equal(("active", "Alex Tan", true), (a.Result, a.Name, a.CanCheckIn));
        Assert.NotNull(a.Expires);
        Assert.StartsWith("Membership ends ", a.ExpiryText);
        Assert.Equal("pending", (await svc.ScanAsync(bea)).Value!.Result);
        var c = (await svc.ScanAsync(carlo)).Value!;
        Assert.Equal("expired", c.Result); Assert.False(c.CanCheckIn);
        var none = (await svc.ScanAsync("GYM1-nope")).Value!;
        Assert.Equal(("not_found", null), (none.Result, none.Name));
        Assert.Equal("Not found: this code does not belong to any member", none.StatusText);
    }

    [Fact]
    public async Task An_archived_member_scans_as_inactive_with_the_same_code()
    {
        var gw = new DemoGateway();
        var code = await CodeOf(gw, "carlo@demo.io");   // Carlo has no coach, sessions or pending payments, so he can be deleted (archived)
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var archive = new AccountArchiveService(gw);
        Assert.True((await archive.DeleteAsync("u-carlo", "Moved away")).Ok);
        var r = (await new AttendanceService(gw).ScanAsync(code)).Value!;
        Assert.Equal("inactive", r.Result); Assert.False(r.CanCheckIn);
        Assert.True((await archive.RestoreAsync("u-carlo")).Ok);
        Assert.Equal("expired", (await new AttendanceService(gw).ScanAsync(code)).Value!.Result);   // same code after restore
    }

    [Fact]
    public async Task Members_and_coaches_cannot_scan()
    {
        var gw = new DemoGateway();
        var code = await CodeOf(gw, "bea@demo.io");
        var (_, svc) = await As("member@demo.io", gw);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.ScanAsync(code)).Error);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.ScanAsync(code)).Error);
    }

    [Fact]
    public async Task A_scan_shows_the_member_and_Check_in_records_it_once_tapped()
    {
        var gw = new DemoGateway();
        var code = await CodeOf(gw, "member@demo.io");
        var (_, svc) = await As("employee@demo.io", gw);
        var vm = new CheckInViewModel(svc, new CoachService(gw));
        var before = (await gw.ListAsync<GymCheckIn>()).Count;

        vm.ScanInput = code + "\n";   // a scanner device types the code and presses Enter
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal("Alex Tan", vm.Scan!.Name);
        Assert.Equal("", vm.ScanInput);
        Assert.Equal(before, (await gw.ListAsync<GymCheckIn>()).Count);   // looking up never checks in

        await vm.CheckInScannedCommand.ExecuteAsync(null);
        Assert.Equal("Alex Tan is checked in.", vm.Notice);
        Assert.Null(vm.Scan);
        Assert.Equal(before + 1, (await gw.ListAsync<GymCheckIn>()).Count);
    }

    [Fact]
    public async Task The_admin_overview_lists_a_days_check_ins()
    {
        var gw = new DemoGateway();
        var (_, svc) = await As("employee@demo.io", gw);
        Assert.True((await svc.CheckInAsync("u-alex")).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new AdminOverviewViewModel(new AdminService(gw), new SubscriptionService(gw), svc);
        await vm.LoadCheckInsAsync();
        Assert.Contains(vm.CheckIns, r => r.Name == "Alex Tan");
        Assert.Matches(@"^\d+ check-ins?$", vm.CheckInsText);

        vm.CheckInDay = vm.CheckInDay.AddDays(-400);
        await vm.LoadCheckInsAsync();
        Assert.Empty(vm.CheckIns);
        Assert.Equal("No check-ins on this day.", vm.CheckInsText);
    }
}
