using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 7 (F19, owner 2026-11-11): the Check-in page lists who came in today, newest first, with the count and a name filter. It reuses
// AttendanceService.CheckInsOnAsync (no new query) and reloads after each check-in; the page's live hint (F4) covers check-ins from elsewhere.
public class CheckInTodayTests
{
    static async Task<(DemoGateway Gw, CheckInViewModel Vm)> Desk(string email = "employee@demo.io", DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new CheckInViewModel(new AttendanceService(gw), new CoachService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task A_check_in_by_name_appears_at_the_top_with_the_count_up_one()
    {
        var (_, vm) = await Desk();
        var before = vm.TodayCount;
        vm.Search = "Alex";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.CheckInCommand.ExecuteAsync(Assert.Single(vm.Hits));
        vm.Pin = "1234";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Equal("Alex Tan is checked in.", vm.Notice);
        Assert.Equal(before + 1, vm.TodayCount);
        Assert.Equal("Alex Tan", vm.Today[0].Name);
        Assert.Equal("By name", vm.Today[0].HowText);
        Assert.Equal($"Checked in today: {before + 1}", vm.TodayTitle);
    }

    [Fact]
    public async Task A_scan_check_in_appears_at_the_top()
    {
        var gw = new DemoGateway();
        var pass = await MemberQrTests.PassOf(gw, "member@demo.io");
        var (_, vm) = await Desk(gw: gw);
        await vm.ScanCommand.ExecuteAsync(pass);
        await vm.CheckInScannedCommand.ExecuteAsync(null);
        Assert.Equal("Alex Tan", vm.Today[0].Name);
        Assert.Equal("QR", vm.Today[0].HowText);
    }

    [Fact]
    public async Task Newest_first_and_the_filter_narrows_by_name()
    {
        var (gw, vm) = await Desk();
        var svc = new AttendanceService(gw);
        Assert.True((await svc.CheckInAsync("u-alex", "manual", pin: "1234")).Ok);
        await vm.OnAppearingAsync();
        Assert.True(vm.Today.Zip(vm.Today.Skip(1)).All(p => p.First.At >= p.Second.At));
        var day = vm.TodayCount;
        vm.TodayFilter = "alex";
        Assert.NotEmpty(vm.Today);
        Assert.All(vm.Today, r => Assert.Contains("Alex", r.Name));
        vm.TodayFilter = "nobody by this name";
        Assert.Empty(vm.Today);
        Assert.Equal(day, vm.TodayCount);   // the count is the day's, not the filter's
        vm.TodayFilter = "";
        Assert.Equal(vm.TodayCount, vm.Today.Count);
    }

    [Theory]
    [InlineData("employee@demo.io")]
    [InlineData("subadmin@demo.io")]
    [InlineData("admin@demo.io")]
    public async Task Front_desk_admin_and_system_admin_all_see_it(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AttendanceService(gw).CheckInAsync("u-alex", "manual", pin: "1234")).Ok);
        var (_, vm) = await Desk(email, gw);
        Assert.Contains(vm.Today, r => r.Name == "Alex Tan");
    }

    sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task A_check_in_from_the_last_minute_is_tinted_however_often_the_list_reloads()
    {
        // the live hint reloads the list right after the desk's own reload, so "new" has to mean recent, not "since the last load"
        var clock = new Clock();
        var gw = new DemoGateway(clock: clock);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CheckInViewModel(new AttendanceService(gw, clock), new CoachService(gw), clock: clock);
        await vm.OnAppearingAsync();
        Assert.True((await new AttendanceService(gw, clock).CheckInAsync("u-alex", "manual", pin: "1234")).Ok);
        await vm.OnAppearingAsync();
        await vm.OnAppearingAsync();                     // a second reload straight after: still tinted
        Assert.True(vm.Today[0].IsNew);
        Assert.Equal("By name · By Marco Santos", vm.Today[0].HowByText);
        clock.Now = clock.Now.AddMinutes(2);
        await vm.OnAppearingAsync();
        Assert.DoesNotContain(vm.Today, r => r.IsNew);
        Assert.Equal("", vm.TodayMatchText);
        vm.TodayFilter = "alex";
        Assert.Equal($"{vm.Today.Count} of {vm.TodayCount} match", vm.TodayMatchText);
    }
}
