using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 10 (D12), changed by F6 (0049) in demo mode: a member shows a one-use pass that lasts 5 minutes; the front desk scans it and
// sees live status (Alex active, Bea pending, Carlo expired). A scan never checks in by itself, Check in does, and only for Active.
public class MemberQrTests
{
    static async Task<(DemoGateway Gw, AttendanceService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new AttendanceService(gw));
    }

    internal static async Task<string> PassOf(DemoGateway gw, string email)
    {
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (await new AttendanceService(gw).MyPassAsync()).Value!.Code;
    }

    [Fact]
    public async Task F6_a_member_gets_a_one_use_pass_for_five_minutes_and_staff_get_none()
    {
        var (gw, svc) = await As("member@demo.io");
        var pass = (await svc.MyPassAsync()).Value!;
        Assert.Matches("^GYM2-[0-9a-f]{32}$", pass.Code);
        Assert.InRange((pass.ExpiresAt - DateTime.UtcNow).TotalMinutes, 4.9, 5.01);
        Assert.NotEqual(pass.Code, (await svc.MyPassAsync()).Value!.Code);   // a new one each time
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.MyPassAsync()).Error);
    }

    [Fact]
    public async Task F6_the_member_QR_says_when_it_changes_and_renews_itself_before_it_runs_out()
    {
        var (gw, _) = await As("member@demo.io");
        var vm = new MyMembershipViewModel(new MembershipService(gw), new DeskPinTests.NoPicker(), new SubscriptionService(gw), new AttendanceService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        var first = vm.MyCode;
        Assert.StartsWith("GYM2-", first);
        var now = DateTime.UtcNow;
        await vm.TickPassAsync(now);
        Assert.Equal((first, "New code in 5 minutes"), (vm.MyCode, vm.PassLeftText));
        await vm.TickPassAsync(now.AddMinutes(3));
        Assert.Equal((first, "New code in 2 minutes"), (vm.MyCode, vm.PassLeftText));
        await vm.TickPassAsync(now.AddMinutes(4).AddSeconds(20));
        Assert.Equal((first, "New code in under a minute"), (vm.MyCode, vm.PassLeftText));
        await vm.TickPassAsync(now.AddMinutes(4).AddSeconds(35));   // under 30 seconds left (the page ticks every 20): a fresh pass
        Assert.NotEqual(first, vm.MyCode);
    }

    sealed class AheadClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(1); }

    [Fact]
    public async Task F6_the_countdown_runs_on_the_phones_clock_so_a_server_clock_that_differs_does_not_matter()
    {
        var gw = new DemoGateway(clock: new AheadClock());   // the server's clock an hour ahead of the phone's
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MyMembershipViewModel(new MembershipService(gw), new DeskPinTests.NoPicker(), new SubscriptionService(gw), new AttendanceService(gw));
        var now = DateTime.UtcNow;
        await vm.TickPassAsync(now);
        Assert.Equal("New code in 5 minutes", vm.PassLeftText);
        var first = vm.MyCode;
        await vm.TickPassAsync(now.AddMinutes(4).AddSeconds(35));
        Assert.NotEqual(first, vm.MyCode);
    }

    [Fact]
    public async Task The_front_desk_sees_live_status_for_each_pass()
    {
        var gw = new DemoGateway();
        string alex = await PassOf(gw, "member@demo.io"), bea = await PassOf(gw, "bea@demo.io"), carlo = await PassOf(gw, "carlo@demo.io");
        var (_, svc) = await As("employee@demo.io", gw);

        var a = (await svc.ScanAsync(alex)).Value!;
        Assert.Equal(("active", "Alex Tan", true), (a.Result, a.Name, a.CanCheckIn));
        Assert.NotNull(a.Expires);
        Assert.StartsWith("Membership ends ", a.ExpiryText);
        Assert.Equal("pending", (await svc.ScanAsync(bea)).Value!.Result);
        var c = (await svc.ScanAsync(carlo)).Value!;
        Assert.Equal("expired", c.Result); Assert.False(c.CanCheckIn);
        var none = (await svc.ScanAsync("GYM2-nope")).Value!;
        Assert.Equal(("not_found", null), (none.Result, none.Name));
        Assert.Equal("Not found: this code does not belong to any member", none.StatusText);
    }

    [Fact]
    public async Task F6_a_screenshot_an_expired_pass_and_the_old_permanent_code_are_refused()
    {
        var gw = new DemoGateway();
        var first = await PassOf(gw, "member@demo.io");
        var permanent = (await gw.ListAsync<MemberCode>(c => c.UserId == "u-alex")).Single().Code;
        var replaced = await PassOf(gw, "member@demo.io");
        var current = await PassOf(gw, "member@demo.io");
        var (_, svc) = await As("employee@demo.io", gw);

        Assert.Equal("old_code", (await svc.ScanAsync(first)).Value!.Result);      // replaced by a newer pass
        Assert.Equal("active", (await svc.ScanAsync(current)).Value!.Result);
        var again = (await svc.ScanAsync(current)).Value!;                          // a screenshot of the same pass
        Assert.Equal(("old_code", null, false), (again.Result, again.Member, again.CanCheckIn));
        Assert.Equal("Old code: it was already scanned or has run out. Ask the member to open their QR again.", again.StatusText);
        Assert.Equal("old_code", (await svc.ScanAsync(permanent)).Value!.Result);
        Assert.Equal("old_code", (await svc.ScanAsync(replaced)).Value!.Result);
    }

    [Fact]
    public async Task An_archived_member_scans_as_inactive()
    {
        var gw = new DemoGateway();
        var pass = await PassOf(gw, "carlo@demo.io");   // Carlo has no coach, sessions or pending payments, so he can be deleted (archived)
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var archive = new AccountArchiveService(gw);
        Assert.True((await archive.DeleteAsync("u-carlo", "Moved away")).Ok);
        var r = (await new AttendanceService(gw).ScanAsync(pass)).Value!;
        Assert.Equal("inactive", r.Result); Assert.False(r.CanCheckIn);
    }

    [Fact]
    public async Task Members_and_coaches_cannot_scan()
    {
        var gw = new DemoGateway();
        var pass = await PassOf(gw, "bea@demo.io");
        var (_, svc) = await As("member@demo.io", gw);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.ScanAsync(pass)).Error);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.ScanAsync(pass)).Error);
    }

    [Fact]
    public async Task A_scan_shows_the_member_and_Check_in_records_it_once_tapped()
    {
        var gw = new DemoGateway();
        var pass = await PassOf(gw, "member@demo.io");
        var (_, svc) = await As("employee@demo.io", gw);
        var vm = new CheckInViewModel(svc, new CoachService(gw));
        var before = (await gw.ListAsync<GymCheckIn>()).Count;

        vm.ScanInput = pass + "\n";   // a scanner device types the code and presses Enter
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal("Alex Tan", vm.Scan!.Name);
        Assert.Equal("", vm.ScanInput);
        Assert.Equal(before, (await gw.ListAsync<GymCheckIn>()).Count);   // looking up never checks in

        await vm.CheckInScannedCommand.ExecuteAsync(null);
        Assert.Equal("Alex Tan is checked in.", vm.Notice);
        Assert.Null(vm.Scan);
        Assert.Equal(before + 1, (await gw.ListAsync<GymCheckIn>()).Count);
        Assert.Equal("qr", (await gw.ListAsync<GymCheckIn>()).Last().Method);
    }

    [Fact]
    public async Task F6_a_QR_check_in_needs_a_pass_this_desk_just_scanned_and_uses_it_once()
    {
        var gw = new DemoGateway();
        var (_, svc) = await As("employee@demo.io", gw);
        var refused = await svc.CheckInAsync("u-alex", "qr");
        Assert.Equal("scan_required", refused.Message);
        Assert.Equal("Scan the member's QR code again, then check them in.", ErrorText.For(refused.Error, refused.Message));
        Assert.Equal("scan_required", (await svc.CheckInAsync("u-alex")).Message);   // no method means QR

        var pass = await PassOf(gw, "member@demo.io");
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await svc.ScanAsync(pass);
        Assert.True((await svc.CheckInAsync("u-alex", "qr")).Ok);
        Assert.Equal("scan_required", (await svc.CheckInAsync("u-alex", "qr")).Message);
    }

    [Fact]
    public async Task The_admin_overview_lists_a_days_check_ins_with_who_recorded_them()
    {
        var gw = new DemoGateway();
        var (_, svc) = await As("employee@demo.io", gw);
        Assert.True((await svc.CheckInAsync("u-alex", "manual", pin: "1234")).Ok);
        // F8: a reason only for a member with no desk PIN: Carlo buys a month at the counter and never set one
        await gw.OpenDrawerAsync();
        var monthly = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        Assert.True((await new PaymentStaffService(gw).RecordCounterAsync("membership", "cash", "u-carlo", monthly.Id, null, null, null)).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.CheckInAsync("u-carlo", "manual", reason: "Phone broken, known member")).Ok);
        var vm = new AdminOverviewViewModel(new AdminService(gw), new SubscriptionService(gw), svc);
        await vm.LoadCheckInsAsync();
        Assert.Contains(vm.CheckIns, r => r.Name == "Alex Tan" && r.HowText == "By name" && r.By == "Marco Santos" && r.Reason is null);
        Assert.Contains(vm.CheckIns, r => r.Name == "Carlo Diaz" && r.HowText == "By name, no PIN" && r.By == "Dana Reyes" && r.Reason == "Phone broken, known member");
        Assert.Contains(vm.CheckIns, r => r.ByText == "By Marco Santos");
        Assert.Contains(vm.CheckIns, r => r.ByText == "By Dana Reyes. Reason: Phone broken, known member");
        Assert.Matches(@"^\d+ check-ins?$", vm.CheckInsText);

        // F8: the "Manual check-ins" list is the same list, narrowed to check-ins by name
        var pass = await PassOf(gw, "member@demo.io");
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await svc.ScanAsync(pass);
        Assert.True((await svc.CheckInAsync("u-alex", "qr")).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await vm.LoadCheckInsAsync();
        Assert.Contains(vm.CheckIns, r => r.Method == "qr");
        vm.ByNameOnly = true;
        await vm.LoadCheckInsAsync();
        Assert.Equal(2, vm.CheckIns.Count);
        Assert.All(vm.CheckIns, r => Assert.Equal("manual", r.Method));
        Assert.Equal("2 check-ins by name", vm.CheckInsText);

        vm.CheckInDay = vm.CheckInDay.AddDays(-400);
        await vm.LoadCheckInsAsync();
        Assert.Empty(vm.CheckIns);
        Assert.Equal("No check-ins by name on this day.", vm.CheckInsText);
        vm.ByNameOnly = false;
        await vm.LoadCheckInsAsync();
        Assert.Equal("No check-ins on this day.", vm.CheckInsText);
    }
}
