using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 5 of the 2026-11-12 plan (G10 to G12, approved mockup docs/mockups/membership-checking.html) in demo mode: the reason a
// member can't check in, the desk's next step, and login mode (the member's own account password instead of the desk PIN).
// The demo's Alex is active with PIN 1234; Bea's payment waits; Carlo's month ended; every demo password is DemoPassword.
public class MembershipCheckingTests
{
    static async Task<(DemoGateway Gw, CheckInViewModel Vm)> Desk()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
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

    [Fact]
    public void Each_reason_is_said_plainly_with_the_next_step_at_the_desk()
    {
        var ended = new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Expired on 3 Nov", MemberCheck.Reason("expired", ended));
        Assert.Equal("No membership", MemberCheck.Reason("expired", null));
        Assert.Equal("Payment not verified yet", MemberCheck.Reason("pending", null));
        Assert.Equal("Account deactivated", MemberCheck.Reason("inactive", null));
        Assert.Equal(("Open Cashier", "cashier"), MemberCheck.NextStep("expired"));
        Assert.Equal(("Open Payments to verify", "verify"), MemberCheck.NextStep("pending"));
        Assert.Null(MemberCheck.NextStep("inactive"));
        Assert.Null(MemberCheck.NextStep("active"));
    }

    [Fact]
    public async Task A_name_search_gives_each_member_their_reason_and_next_step()
    {
        var (_, vm) = await Desk();
        var bea = await Find(vm, "bea");
        Assert.Equal(("Payment not verified yet", "Open Payments to verify", "verify", false), (bea.PlanText, bea.NextStepText, bea.NextStepRoute, bea.CanCheckIn));
        var carlo = await Find(vm, "carlo");
        Assert.StartsWith("Expired on ", carlo.PlanText);
        Assert.Equal("cashier", carlo.NextStepRoute);
        var alex = await Find(vm, "alex");
        Assert.True(alex.CanCheckIn);
        Assert.False(alex.HasNextStep);
        var joy = await Find(vm, "joy");   // never had a membership: not "expired"
        Assert.Equal(("No membership", "no_membership"), (joy.PlanText, joy.PlanStatus));
    }

    [Fact]
    public async Task A_scan_of_someone_not_active_gives_the_reason_and_next_step_and_no_check_in()
    {
        var (gw, vm) = await Desk();
        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        var pass = (await new AttendanceService(gw).MyPassAsync()).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await vm.ScanCommand.ExecuteAsync(pass.Code);
        Assert.False(vm.Scan!.CanCheckIn);
        Assert.StartsWith("Expired on ", vm.Scan.ReasonText);
        Assert.Equal(("Open Cashier", "cashier"), (vm.Scan.NextStepText, vm.Scan.NextStepRoute));
    }

    [Fact]
    public async Task Sign_in_instead_checks_the_member_in_with_their_password_and_clears_the_field_after_every_try()
    {
        var (gw, vm) = await Desk();
        await vm.CheckInCommand.ExecuteAsync(await Find(vm, "alex"));
        Assert.True(vm.CanUsePassword);
        vm.UsePasswordCommand.Execute(null);
        Assert.True(vm.IsPasswordMode);

        vm.Password = "not it";
        await vm.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.Equal("Wrong password. Ask the member to try again. After 5 wrong tries (PIN or password) it locks for 15 minutes.", vm.ErrorMessage);
        Assert.Equal("", vm.Password);
        Assert.NotNull(vm.PinFor);

        vm.Password = DemoGateway.DemoPassword;
        await vm.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(("", false), (vm.Password, vm.IsPasswordMode));
        Assert.Null(vm.PinFor);
        Assert.Equal("Alex Tan is checked in.", vm.Notice);
        Assert.Equal("password", (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).OrderBy(c => c.Id).Last().Method);
    }

    [Fact]
    public async Task Five_wrong_tries_of_PIN_or_password_lock_the_member_out_for_both()
    {
        var (_, vm) = await Desk();
        await vm.CheckInCommand.ExecuteAsync(await Find(vm, "alex"));
        for (var i = 0; i < 2; i++) { vm.Pin = "0000"; await vm.ConfirmPinCommand.ExecuteAsync(null); }
        vm.UsePasswordCommand.Execute(null);
        for (var i = 0; i < 3; i++) { vm.Password = "wrong"; await vm.ConfirmPasswordCommand.ExecuteAsync(null); }
        Assert.Equal("Too many wrong tries (PIN or password): this member is locked for 15 minutes. Scan their QR code instead, or ask an Admin.", vm.ErrorMessage);
        vm.Password = DemoGateway.DemoPassword;
        await vm.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.Contains("locked for 15 minutes", vm.ErrorMessage);
        vm.UsePinCommand.Execute(null);
        vm.Pin = "1234";
        await vm.ConfirmPinCommand.ExecuteAsync(null);
        Assert.Contains("locked for 15 minutes", vm.ErrorMessage);
    }

    [Fact]
    public async Task Closing_the_step_clears_the_password_and_an_empty_password_is_not_sent()
    {
        var (_, vm) = await Desk();
        var alex = await Find(vm, "alex");
        await vm.CheckInCommand.ExecuteAsync(alex);
        vm.UsePasswordCommand.Execute(null);
        await vm.ConfirmPasswordCommand.ExecuteAsync(null);
        Assert.Equal("Ask the member to type their account password.", vm.ErrorMessage);
        vm.Password = "typed";
        vm.CancelPinCommand.Execute(null);
        Assert.Equal(("", false), (vm.Password, vm.IsPasswordMode));
    }
}
