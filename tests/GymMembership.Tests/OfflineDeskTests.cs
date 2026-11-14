using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>
/// Item 10 and the forgot-phone fix: the desk keeps working when the connection drops, without ever claiming to be live.
/// F6/F8 (0049): a pass can't be checked offline, so offline check-in is by name with the member's desk PIN, sent with the queue
/// and checked by the server then.
/// </summary>
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

    static async Task<(DemoGateway Gw, Clock Clock, MemoryStore Store, OfflineDesk Desk, CheckInViewModel Vm, string Pass)> Setup()
    {
        var clock = new Clock(); var gw = new DemoGateway(clock: clock); var store = new MemoryStore();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var pass = (await new AttendanceService(gw, clock).MyPassAsync()).Value!.Code;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new OfflineDesk(gw, store, clock);
        var vm = new CheckInViewModel(new AttendanceService(gw, clock), new CoachService(gw), desk);
        return (gw, clock, store, desk, vm, pass);
    }
    static Exception Offline() => new HttpRequestException("no connection");

    /// <summary>Finds Alex by name and checks him in with his PIN (1234 in the demo, or the one given).</summary>
    static async Task ByName(CheckInViewModel vm, string pin = "1234")
    {
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.CheckInCommand.ExecuteAsync(Assert.Single(vm.Hits));
        vm.Pin = pin;
        await vm.ConfirmPinCommand.ExecuteAsync(null);
    }

    static IEnumerable<GymCheckIn> Offline(DemoGateway gw) => gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex").Result.Where(c => c.Method == "offline");

    [Fact]
    public async Task Online_a_scan_is_live_and_the_saved_list_is_refreshed_quietly()
    {
        var (_, _, store, _, vm, pass) = await Setup();
        await vm.OnAppearingAsync();
        Assert.NotNull(store.Text);
        Assert.DoesNotContain("GYM", store.Text);   // F6: the saved list keeps no codes
        await vm.ScanCommand.ExecuteAsync(pass);
        Assert.Equal("active", vm.Scan!.Result);
        Assert.False(vm.IsOffline);
    }

    [Fact]
    public async Task F6_offline_a_QR_code_cannot_be_checked_and_the_desk_says_to_use_the_name_and_PIN()
    {
        var (gw, _, _, desk, vm, pass) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await vm.ScanCommand.ExecuteAsync(pass);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsOffline);
        Assert.False(vm.Scan!.CanCheckIn);
        Assert.Equal("Offline, so a QR code can't be checked. Find the member by name and ask for their desk PIN.", vm.Scan.StatusText);
        Assert.Empty(desk.Queued);
    }

    [Fact]
    public async Task Offline_a_paid_member_found_by_name_is_queued_with_the_PIN_never_shown_as_active()
    {
        var (gw, clock, _, desk, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        var hit = Assert.Single(vm.Hits);
        Assert.True(hit.CanCheckIn);
        Assert.DoesNotContain("Active", hit.PlanText);

        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.Same(hit, vm.PinFor);
        vm.Pin = "1234";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("will be sent", vm.Notice);
        Assert.Null(vm.PinFor);
        var queued = Assert.Single(desk.Queued);
        Assert.Equal((clock.GetUtcNow().UtcDateTime, "1234"), (queued.AtUtc, queued.Pin));
    }

    [Fact]
    public async Task The_queue_sends_when_back_online_with_the_real_time_and_only_once()
    {
        var (gw, clock, _, desk, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await ByName(vm);
        var when = clock.GetUtcNow().UtcDateTime;

        clock.Now = clock.Now.AddMinutes(30);
        await vm.AutoRefreshAsync();
        Assert.Single(desk.Queued);   // still offline: nothing lost

        gw.FailWith = null;
        await vm.AutoRefreshAsync();
        Assert.Empty(desk.Queued);
        Assert.Equal(when, Assert.Single(Offline(gw)).CheckedInAt);
        Assert.Contains("1 offline check-in", vm.Notice);

        await desk.SendAsync();   // a repeat changes nothing
        Assert.Single(Offline(gw));
    }

    [Fact]
    public async Task F8_a_wrong_PIN_typed_offline_is_refused_when_sent_and_shown_to_staff()
    {
        var (gw, _, _, desk, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await ByName(vm, "9999");
        gw.FailWith = null;
        await vm.SendQueueCommand.ExecuteAsync(null);
        Assert.Empty(desk.Queued);
        var refused = Assert.Single(desk.Refused);
        Assert.Contains("PIN is wrong", refused.Why);
        Assert.Contains("Alex Tan", vm.RefusedText);
        Assert.Empty(Offline(gw));
    }

    [Fact]
    public async Task A_check_in_the_server_refuses_on_sending_is_shown_to_staff_not_lost_silently()
    {
        var (gw, _, _, desk, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await ByName(vm);

        gw.FailWith = null;
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).SetActiveAsync("u-alex", false)).Ok);   // while the desk was offline
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);

        await vm.SendQueueCommand.ExecuteAsync(null);
        Assert.Empty(desk.Queued);
        Assert.Equal("Alex Tan", Assert.Single(desk.Refused).Name);
        Assert.Contains("Alex Tan", vm.RefusedText);
        Assert.Empty(Offline(gw));
    }

    [Fact]
    public async Task A_copy_older_than_24_hours_finds_nobody()
    {
        var (gw, clock, _, _, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        clock.Now = clock.Now.AddHours(24).AddSeconds(1);
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Empty(vm.Hits);
    }

    [Fact]
    public async Task With_no_saved_list_offline_finds_nobody()
    {
        var (gw, _, _, _, vm, _) = await Setup();
        gw.FailWith = Offline();
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Empty(vm.Hits);
        Assert.Contains("no saved list", vm.OfflineText);
    }

    [Fact]
    public async Task Offline_an_unpaid_member_cannot_check_in()
    {
        var (gw, _, _, desk, vm, _) = await Setup();
        await new AdminServiceFixture(gw).AddMemberWithNoMembership("Nora Vale");
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        vm.Search = "nora";
        await vm.SearchCommand.ExecuteAsync(null);
        var hit = Assert.Single(vm.Hits);
        Assert.False(hit.HasActiveMembership);
        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.PinFor);
        Assert.Empty(desk.Queued);
    }

    [Fact]
    public async Task A_network_failure_is_the_only_thing_that_falls_back()
    {
        var (gw, _, _, desk, vm, pass) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = new Exception("forbidden");
        await vm.ScanCommand.ExecuteAsync(pass);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.Scan);
        Assert.False(vm.IsOffline);
        Assert.Empty(desk.Queued);
    }

    [Fact]
    public async Task F6_the_connection_dropping_between_scan_and_check_in_sends_staff_to_the_name_and_PIN()
    {
        var (gw, _, _, desk, vm, pass) = await Setup();
        await vm.OnAppearingAsync();
        await vm.ScanCommand.ExecuteAsync(pass);
        Assert.True(vm.Scan!.CanCheckIn);
        gw.FailWith = Offline();
        await vm.CheckInScannedCommand.ExecuteAsync(null);
        Assert.Equal("The connection dropped. Find Alex Tan by name and check them in with their desk PIN.", vm.ErrorMessage);
        Assert.True(vm.IsOffline);
        Assert.Empty(desk.Queued);
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

        await ByName(vm);
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
        Assert.Equal(("No membership", "Open Cashier"), (hit.PlanText, hit.NextStepText));   // G10, G11
        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.Equal("Nora Vale has no active membership.", vm.ErrorMessage);
        Assert.Null(vm.PinFor);
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
        var (gw, clock, store, _, vm, _) = await Setup();
        await vm.OnAppearingAsync();
        gw.FailWith = Offline();
        await ByName(vm);

        var restarted = new OfflineDesk(gw, store, clock);
        Assert.Single(restarted.Queued);
        Assert.NotNull(restarted.UpdatedAt);
    }
}
