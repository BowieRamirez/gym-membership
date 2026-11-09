using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 3 (F1-F3, 0050): the cash-drawer shift, through the demo gateway that mirrors the database.
public class CashShiftTests
{
    static async Task<(DemoGateway Gw, PaymentStaffService Svc)> As(string email)
    {
        var gw = new DemoGateway(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new PaymentStaffService(gw));
    }

    static Task<AppResult<Payment>> WalkIn(PaymentStaffService svc, string method = PayMethod.Cash, string? reference = null) =>
        svc.RecordCounterAsync(PayKind.WalkIn, method, null, null, "Pedro Ramos", reference, null);

    [Fact]
    public async Task Without_a_shift_cash_is_refused_in_plain_words_and_GCash_still_goes_through()
    {
        var (_, svc) = await As("employee@demo.io");
        var state = (await svc.ShiftAsync()).Value;
        Assert.Null(state.Open); Assert.Equal(5000m, state.DefaultFloat);
        var cash = await WalkIn(svc);
        Assert.Equal("no_open_shift", cash.Message);
        Assert.Contains("Start your cash shift", ErrorText.For(cash.Error, cash.Message));
        Assert.True((await WalkIn(svc, PayMethod.Gcash, "GC-77")).Ok);
    }

    [Fact]
    public async Task A_float_other_than_the_gym_setting_needs_a_note_and_one_cashier_has_one_open_shift()
    {
        var (_, svc) = await As("employee@demo.io");
        Assert.Equal("note_required", (await svc.OpenShiftAsync(4500, null)).Message);
        var shift = (await svc.OpenShiftAsync(4500, "Short float from the safe")).Value!;
        Assert.Equal(4500m, shift.OpeningFloat);
        Assert.Equal("shift_open", (await svc.OpenShiftAsync(5000, null)).Message);
        Assert.Equal(AppErrorKind.Validation, (await svc.OpenShiftAsync(-1, "x")).Error);
    }

    [Fact]
    public async Task Expected_cash_is_the_float_plus_cash_taken_minus_a_cash_refund_paid_out()
    {
        var (gw, clock, subs, monthly, _) = await SubscriptionTests.Setup();
        clock.Now = DateTimeOffset.UtcNow.AddYears(1);
        var p = (await subs.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var term = Assert.Single((await subs.TermsAsync()).Value!).Term;
        Assert.True((await subs.RequestRefundAsync(term.Id, "Changed plans", "change_of_mind")).Ok);
        var request = Assert.Single((await subs.RefundsAsync()).Value!).Request;
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await subs.DecideAsync(request.Id, true, "Approved")).Ok);
        Assert.Equal("no_open_shift", (await subs.MarkPaidAsync(request.Id, PayMethod.Cash, null)).Message);

        var svc = new PaymentStaffService(gw);
        await svc.OpenShiftAsync(5000, null);
        Assert.True((await WalkIn(svc)).Ok);
        var walkIn = (await gw.ListAsync<GymSettings>(_ => true)).Single().WalkInPrice;
        Assert.True((await subs.MarkPaidAsync(request.Id, PayMethod.Cash, null)).Ok);
        var open = (await svc.ShiftAsync()).Value.Open!;
        Assert.Equal((walkIn, monthly.Price, 5000m + walkIn - monthly.Price), (open.CashIn, open.CashOut, open.Expected));
    }

    [Fact]
    public async Task Closing_keeps_the_count_and_difference_and_cash_is_refused_until_a_new_shift()
    {
        var (_, svc) = await As("employee@demo.io");
        await svc.OpenShiftAsync(5000, null);
        await WalkIn(svc);
        Assert.Equal("note_required", (await svc.CloseShiftAsync(5100, null)).Message);
        var closed = (await svc.CloseShiftAsync(5100, "Gave change twice")).Value!;
        Assert.Equal((5150m, 5100m, -50m), (closed.Expected, closed.Counted!.Value, closed.Difference!.Value));
        Assert.Null((await svc.ShiftAsync()).Value.Open);
        Assert.Equal("no_open_shift", (await svc.CloseShiftAsync(5150, null)).Message);
        Assert.Equal("no_open_shift", (await WalkIn(svc)).Message);
    }

    [Theory]
    [InlineData(5150, "Matches the expected cash")]
    [InlineData(5100, "₱50.00 short")]
    [InlineData(5175.5, "₱25.50 over")]
    public void The_count_is_described_against_the_expected_cash(decimal counted, string words) =>
        Assert.Equal(words, CashCount.Words(counted - 5150m));

    static async Task<(DemoGateway Gw, CashierViewModel Vm)> Cashier()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task The_cashier_starts_on_Start_shift_with_the_float_prefilled_and_a_note_only_when_it_differs()
    {
        var (_, vm) = await Cashier();
        Assert.True(vm.NeedsShift); Assert.False(vm.HasShift);
        Assert.Equal(5000m, vm.StartFloat); Assert.False(vm.StartNeedsNote);
        vm.StartFloat = 4000; Assert.True(vm.StartNeedsNote);
        vm.StartFloat = 5000;
        await vm.StartShiftCommand.ExecuteAsync(null);
        Assert.True(vm.HasShift); Assert.False(vm.NeedsShift); Assert.Null(vm.ErrorMessage);
        Assert.Equal(5000m, vm.Shift!.Expected);
    }

    [Fact]
    public async Task Cash_taken_shows_on_the_shift_and_ending_it_shows_the_difference_and_needs_a_note()
    {
        var (_, vm) = await Cashier();
        await vm.StartShiftCommand.ExecuteAsync(null);
        vm.Kind = "Walk-in"; vm.WalkInName = "Pedro Ramos";
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Equal(150m, vm.Shift!.CashIn); Assert.Equal(5150m, vm.Shift.Expected);

        vm.BeginCloseCommand.Execute(null);
        Assert.True(vm.IsClosing); Assert.Equal("", vm.CountText);
        vm.Counted = 5150; Assert.Equal("Matches the expected cash", vm.CountText); Assert.False(vm.CloseNeedsNote);
        vm.Counted = 5100; Assert.Equal("₱50.00 short", vm.CountText); Assert.True(vm.CloseNeedsNote);
        await vm.EndShiftCommand.ExecuteAsync(null);
        Assert.True(vm.HasShift); Assert.NotNull(vm.ErrorMessage);   // the note is missing: the server refuses, the shift stays open
        vm.CloseNote = "Gave change twice";
        await vm.EndShiftCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsShift); Assert.False(vm.IsClosing);
        Assert.Contains("₱50.00 short", vm.Notice);
    }

    [Fact]
    public async Task Admin_and_System_admin_list_every_shift_with_the_cashier_the_front_desk_cannot()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new PaymentStaffService(gw);
        await desk.OpenShiftAsync(5000, null); await WalkIn(desk); await desk.CloseShiftAsync(5100, "Gave change twice");
        await desk.OpenShiftAsync(5000, null);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        Assert.False((await new AdminService(gw).CashShiftsAsync(today, today)).Ok);
        foreach (var who in new[] { "admin@demo.io", "subadmin@demo.io" })
        {
            await gw.SignInAsync(who, DemoGateway.DemoPassword);
            var rows = (await new AdminService(gw).CashShiftsAsync(today, today)).Value!;
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("Marco Santos", r.Cashier));
            Assert.Null(rows[0].ClosedAt);   // newest first: the open one, with live totals
            Assert.Equal((-50m, "Gave change twice"), (rows[1].Difference!.Value, rows[1].CloseNote));
        }
    }

    [Fact]
    public async Task The_opening_float_is_saved_in_gym_settings()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new GymSettingsViewModel(new AdminService(gw), new DemoPhotoPicker());
        await vm.OnAppearingAsync();
        Assert.Equal(5000m, vm.OpeningFloat);
        vm.OpeningFloat = 3000;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(3000m, (await gw.ListAsync<GymSettings>(_ => true)).Single().OpeningFloat);
    }
}
