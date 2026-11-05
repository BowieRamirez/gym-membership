using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Item 10 and the forgot-phone fix: the desk keeps working when the connection drops, without ever claiming to be live.</summary>
public class OfflineDeskTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 20, 2, 10, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class MemoryStore : IDeskStore
    {
        public string? Text;
        public string? Load() => Text;
        public void Save(string text) => Text = text;
    }

    static async Task<(DemoGateway Gw, Clock Clock, MemoryStore Store, OfflineDesk Desk, CheckInViewModel Vm, string Code)> Setup()
    {
        var clock = new Clock(); var gw = new DemoGateway(clock: clock); var store = new MemoryStore();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var code = (await new AttendanceService(gw, clock).MyCodeAsync()).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new OfflineDesk(gw, store, clock);
        var vm = new CheckInViewModel(new AttendanceService(gw, clock), new CoachService(gw), desk);
        return (gw, clock, store, desk, vm, code);
    }
    static Exception Offline() => new HttpRequestException("no connection");

    [Fact]
    public async Task Online_a_scan_is_live_and_the_saved_list_is_refreshed_quietly()
    {
        var (_, _, store, _, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        Assert.NotNull(store.Text);
        await vm.ScanCommand.ExecuteAsync(code);
        Assert.Equal("active", vm.Scan!.Result);
        Assert.False(vm.IsOffline);
    }

    [Fact]
    public async Task Offline_a_paid_member_is_shown_from_the_saved_list_never_as_active_and_the_check_in_is_queued()
    {
        var (gw, clock, _, desk, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();

        await vm.ScanCommand.ExecuteAsync(code);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsOffline);
        Assert.True(vm.Scan!.CanCheckIn);
        Assert.Equal("Alex Tan", vm.Scan.Name);
        Assert.DoesNotContain("Active", vm.Scan.StatusText);
        Assert.Contains("Last updated", vm.Scan.StatusText);

        await vm.CheckInScannedCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("will be sent", vm.Notice);
        Assert.Equal(1, desk.Queued.Count);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, desk.Queued[0].AtUtc);
    }

    [Fact]
    public async Task The_queue_sends_when_back_online_with_the_real_time_and_only_once()
    {
        var (gw, clock, _, desk, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await vm.ScanCommand.ExecuteAsync(code);
        await vm.CheckInScannedCommand.ExecuteAsync(null);
        var when = clock.GetUtcNow().UtcDateTime;

        clock.Now = clock.Now.AddMinutes(30);
        await vm.AutoRefreshAsync();
        Assert.Equal(1, desk.Queued.Count);   // still offline: nothing lost

        gw.FailWith = null;
        await vm.AutoRefreshAsync();
        Assert.Empty(desk.Queued);
        var rows = (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).Where(c => c.Method == "offline").ToList();
        Assert.Equal(when, Assert.Single(rows).CheckedInAt);
        Assert.Contains("1 offline check-in", vm.Notice);

        await desk.SendAsync();   // a repeat changes nothing
        Assert.Single((await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).Where(c => c.Method == "offline"));
    }

    [Fact]
    public async Task A_check_in_the_server_refuses_on_sending_is_shown_to_staff_not_lost_silently()
    {
        var (gw, _, _, desk, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await vm.ScanCommand.ExecuteAsync(code);
        await vm.CheckInScannedCommand.ExecuteAsync(null);

        gw.FailWith = null;
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).SetActiveAsync("u-alex", false)).Ok);   // while the desk was offline
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);

        await vm.SendQueueCommand.ExecuteAsync(null);
        Assert.Empty(desk.Queued);
        var refused = Assert.Single(desk.Refused);
        Assert.Equal("Alex Tan", refused.Name);
        Assert.Contains("Alex Tan", vm.RefusedText);
        Assert.Empty((await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).Where(c => c.Method == "offline"));
    }

    [Fact]
    public async Task A_copy_older_than_24_hours_cannot_check_anyone()
    {
        var (gw, clock, _, _, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        clock.Now = clock.Now.AddHours(24).AddSeconds(1);
        await vm.ScanCommand.ExecuteAsync(code);
        Assert.False(vm.Scan!.CanCheckIn);
        Assert.Contains("can't check", vm.Scan.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_no_saved_list_offline_cannot_check_anyone()
    {
        var (gw, _, _, _, vm, code) = await Setup();
        gw.FailWith = Offline();
        await vm.ScanCommand.ExecuteAsync(code);
        Assert.False(vm.Scan!.CanCheckIn);
        Assert.Contains("can't check", vm.Scan.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_an_unknown_code_or_an_unpaid_member_cannot_check_in()
    {
        var (gw, _, _, desk, vm, _) = await Setup();
        await new AdminServiceFixture(gw).AddMemberWithNoMembership("Nora Vale");
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await vm.ScanCommand.ExecuteAsync("GYM1-not-a-real-code");
        Assert.False(vm.Scan!.CanCheckIn);
        Assert.Contains("Not on the saved list", vm.Scan.StatusText);

        vm.Search = "nora";
        await vm.SearchCommand.ExecuteAsync(null);
        var hit = Assert.Single(vm.Hits);
        Assert.False(hit.HasActiveMembership);
        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(desk.Queued);
    }

    [Fact]
    public async Task A_network_failure_is_the_only_thing_that_falls_back()
    {
        var (gw, _, _, desk, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = new Exception("forbidden");
        await vm.ScanCommand.ExecuteAsync(code);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.Scan);
        Assert.False(vm.IsOffline);
        Assert.Empty(desk.Queued);
    }

    [Fact]
    public async Task The_connection_dropping_between_scan_and_check_in_queues_it()
    {
        var (gw, _, _, desk, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        await vm.ScanCommand.ExecuteAsync(code);
        Assert.True(vm.Scan!.CanCheckIn);
        gw.FailWith = Offline();
        await vm.CheckInScannedCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(1, desk.Queued.Count);
    }

    [Fact]
    public async Task Forgot_phone_offline_search_uses_the_saved_list_and_queues_the_check_in()
    {
        var (gw, _, _, desk, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var hit = Assert.Single(vm.Hits);
        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.Equal(1, desk.Queued.Count);
    }

    [Fact]
    public async Task Forgot_phone_online_a_manual_check_in_is_recorded_as_manual_and_shows_the_status()
    {
        var (gw, _, _, _, vm, _) = await Setup();
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        var hit = Assert.Single(vm.Hits);
        Assert.True(hit.HasActiveMembership);
        Assert.NotNull(hit.PaidUntil);
        Assert.Contains("Paid until", hit.PlanText);

        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.Equal("manual", (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).Last().Method);
    }

    [Fact]
    public async Task Forgot_phone_a_member_without_a_membership_cannot_be_checked_in()
    {
        var (gw, _, _, _, vm, _) = await Setup();
        await new AdminServiceFixture(gw).AddMemberWithNoMembership("Nora Vale");
        vm.Search = "nora";
        await vm.SearchCommand.ExecuteAsync(null);
        var hit = Assert.Single(vm.Hits);
        Assert.False(hit.CanCheckIn);
        Assert.Equal("No active membership", hit.PlanText);
        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.NotNull(vm.ErrorMessage);
    }

    sealed class AdminServiceFixture(DemoGateway gw)
    {
        public async Task AddMemberWithNoMembership(string name)
        {
            await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
            var parts = name.Split(' ');
            Assert.True((await new AdminService(gw).CreateUserAsync(parts[0], parts[1], "nora@demo.io", "member", "temp1234")).Ok);
            await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        }
    }

    [Fact]
    public async Task The_store_is_read_back_after_a_restart_so_the_queue_survives()
    {
        var (gw, clock, store, desk, vm, code) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await vm.ScanCommand.ExecuteAsync(code);
        await vm.CheckInScannedCommand.ExecuteAsync(null);

        var restarted = new OfflineDesk(gw, store, clock);
        Assert.Equal(1, restarted.Queued.Count);
        Assert.NotNull(restarted.UpdatedAt);
    }
}
