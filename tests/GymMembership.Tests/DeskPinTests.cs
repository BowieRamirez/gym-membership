using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// F8 (0049) in demo mode: checking in by name needs the member's 4-digit desk PIN; five wrong tries lock it for 15 minutes; only
// Admin and System admin may check in without a PIN, with a reason. The demo's Alex has PIN 1234.
public class DeskPinTests
{
    static async Task<(DemoGateway Gw, CheckInViewModel Vm)> Desk(string email = "employee@demo.io")
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new CheckInViewModel(new AttendanceService(gw), new CoachService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task<MemberHit> Find(CheckInViewModel vm, string name)
    {
        vm.Search = name;
        await vm.SearchCommand.ExecuteAsync(null);
        return Assert.Single(vm.Hits);
    }

    static async Task<int> CheckIns(DemoGateway gw, string member) => (await gw.ListAsync<GymCheckIn>(c => c.MemberId == member)).Count;

    /// <summary>Carlo bought a month at the counter: an active member who never set a PIN.</summary>
    static async Task MakeCarloActive(DemoGateway gw, string back)
    {
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var monthly = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        Assert.True((await new PaymentStaffService(gw).RecordCounterAsync("membership", "cash", "u-carlo", monthly.Id, null, null, null)).Ok);
        await gw.SignInAsync(back, DemoGateway.DemoPassword);
    }

    [Fact]
    public async Task Check_in_by_name_asks_for_the_PIN_first_and_records_nothing_until_it_is_right()
    {
        var (gw, vm) = await Desk();
        var before = await CheckIns(gw, "u-alex");
        var hit = await Find(vm, "alex");
        Assert.False(vm.CanSkipPin);                    // the front desk can't skip it

        await vm.CheckInCommand.ExecuteAsync(hit);
        Assert.Same(hit, vm.PinFor);
        Assert.Equal("AT", vm.PinLetters);              // no picture in the demo: the initials stand in for the photo
        Assert.Equal(before, await CheckIns(gw, "u-alex"));

        vm.Pin = "0000";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Equal("That PIN is wrong. Ask the member to try again. After 5 wrong tries it locks for 15 minutes.", vm.ErrorMessage);
        Assert.Same(hit, vm.PinFor); Assert.Equal("", vm.Pin);
        Assert.Equal(before, await CheckIns(gw, "u-alex"));

        vm.Pin = "1234";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Alex Tan is checked in.", vm.Notice);
        Assert.Null(vm.PinFor);
        Assert.Equal("manual", (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).Last().Method);
    }

    [Fact]
    public async Task The_PIN_step_shows_the_members_photo_when_there_is_one()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new ProfileService(gw).SetAvatarAsync("me.png", [137, 80, 78, 71, 13, 10, 26, 10, 1])).Ok);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CheckInViewModel(new AttendanceService(gw), new CoachService(gw), profile: new ProfileService(gw));
        await vm.OnAppearingAsync();
        await vm.CheckInCommand.ExecuteAsync(await Find(vm, "alex"));
        Assert.NotNull(vm.PinPhoto);
    }

    [Fact]
    public async Task Cancel_closes_the_PIN_step_without_checking_in()
    {
        var (gw, vm) = await Desk();
        var before = await CheckIns(gw, "u-alex");
        await vm.CheckInCommand.ExecuteAsync(await Find(vm, "alex"));
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-alex");   // the step stands in for the row: not listed twice
        await vm.SearchCommand.ExecuteAsync(null);                      // the page's own refresh doesn't bring it back either
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-alex");
        vm.Pin = "12";
        vm.CancelPinCommand.Execute(null);
        Assert.Null(vm.PinFor); Assert.Equal("", vm.Pin);
        Assert.Contains(vm.Hits, h => h.MemberId == "u-alex");
        Assert.Equal(before, await CheckIns(gw, "u-alex"));
    }

    [Fact]
    public async Task Five_wrong_PINs_lock_it_for_15_minutes_even_for_the_right_one()
    {
        var (gw, vm) = await Desk();
        var hit = await Find(vm, "alex");
        await vm.CheckInCommand.ExecuteAsync(hit);
        for (var n = 0; n < 5; n++) { vm.Pin = "9999"; await vm.ConfirmPinCommand.ExecuteAsync(null); }
        vm.Pin = "1234";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Equal("Too many wrong PINs: this member's desk PIN is locked for 15 minutes. Scan their QR code instead, or ask an Admin.", vm.ErrorMessage);
        Assert.Empty((await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).Where(c => c.Method == "manual"));
    }

    [Fact]
    public async Task A_member_with_no_PIN_is_sent_to_an_Admin()
    {
        var (gw, vm) = await Desk();
        await MakeCarloActive(gw, "employee@demo.io");
        await vm.CheckInCommand.ExecuteAsync(await Find(vm, "carlo"));
        vm.Pin = "1111";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Equal("This member hasn't set a desk PIN yet (Settings in their app). An Admin or System admin can check them in with a reason.", vm.ErrorMessage);
        Assert.Equal(0, await CheckIns(gw, "u-carlo"));
    }

    [Theory]
    [InlineData("admin@demo.io", "Dana Reyes")]
    [InlineData("subadmin@demo.io", "Sam Villanueva")]
    public async Task Admin_and_System_admin_can_check_in_without_a_PIN_only_with_a_reason(string email, string who)
    {
        var (gw, vm) = await Desk(email);
        Assert.True(vm.CanSkipPin);
        await MakeCarloActive(gw, email);
        await vm.CheckInCommand.ExecuteAsync(await Find(vm, "carlo"));
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Equal("Enter the member's desk PIN, or a reason for checking them in without it.", vm.ErrorMessage);
        Assert.Equal(0, await CheckIns(gw, "u-carlo"));

        vm.Reason = "Known member, phone broken";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Equal("Carlo Diaz is checked in.", vm.Notice);
        var row = (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-carlo")).Single();
        Assert.Equal(("manual", "Known member, phone broken"), (row.Method, row.Reason));
        Assert.Equal(who, (await new AttendanceService(gw).CheckInsOnAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)))).Value!.Last().By);
    }

    [Fact]
    public async Task The_front_desk_cannot_skip_the_PIN_even_by_calling_the_service()
    {
        var (gw, _) = await Desk();
        var r = await new AttendanceService(gw).CheckInAsync("u-alex", "manual", reason: "Trust me");
        Assert.Equal("pin_required", r.Message);
    }

    [Fact]
    public async Task A_member_sets_a_4_digit_PIN_in_Settings_and_staff_have_no_PIN_section()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new SettingsViewModel(new SettingsService(gw), new AuthService(gw), new ConfirmationTests.Nav(), new ProfileService(gw), new NoPicker());
        await vm.OnAppearingAsync();
        Assert.True(vm.IsMember);

        (vm.DeskPin, vm.DeskPinAgain) = ("4321", "4322");
        await vm.SaveDeskPinCommand.ExecuteAsync(null);
        Assert.Equal("The two PINs don't match.", vm.ErrorMessage);
        (vm.DeskPin, vm.DeskPinAgain) = ("43a1", "43a1");
        await vm.SaveDeskPinCommand.ExecuteAsync(null);
        Assert.Equal("Choose 4 digits for your desk PIN.", vm.ErrorMessage);
        (vm.DeskPin, vm.DeskPinAgain) = ("4321", "4321");
        await vm.SaveDeskPinCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Desk PIN saved. Type it at the front desk if you check in without your phone.", vm.Notice);
        Assert.Equal(("", ""), (vm.DeskPin, vm.DeskPinAgain));

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AttendanceService(gw).CheckInAsync("u-alex", "manual", pin: "4321")).Ok);
        Assert.Equal("wrong_pin", (await new AttendanceService(gw).CheckInAsync("u-alex", "manual", pin: "1234")).Message);

        var staff = new SettingsViewModel(new SettingsService(gw), new AuthService(gw), new ConfirmationTests.Nav(), new ProfileService(gw), new NoPicker());
        await staff.OnAppearingAsync();
        Assert.False(staff.IsMember);
        Assert.Equal(AppErrorKind.Unauthorized, (await new SettingsService(gw).SetDeskPinAsync("1234")).Error);
    }

    internal sealed class NoPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string, byte[])?>(null); }
}
