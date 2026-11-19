using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Cash plan 2026-11-17: Payments to verify is back. Every cash payment a member chose in the app waits here.</summary>
public class VerifyPaymentsTests
{
    // a new member buys a package with cash, then the desk signs in (drawer open or not)
    static async Task<(DemoGateway gw, Payment pay)> CashPackage(bool drawer = true)
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var pkg = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Quarterly");
        var pay = (await new SubscriptionService(gw).ExtendAsync(pkg.Id)).Value!;
        gw.OwnerMakeCash(pay.Id);   // 0099: a cash payment a member chose before members paid online only
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        if (drawer) await gw.OpenDrawerAsync();
        return (gw, pay);
    }

    static async Task<VerifyPaymentsViewModel> Page(DemoGateway gw)
    {
        var vm = new VerifyPaymentsViewModel(new PaymentStaffService(gw));
        await vm.OnAppearingAsync();
        return vm;
    }

    [Fact]
    public async Task Lists_cash_packages_and_not_online_payments()
    {
        var (gw, pay) = await CashPackage();
        var vm = await Page(gw);
        Assert.Contains(vm.Pending, r => r.Payment.Id == pay.Id);
        Assert.All(vm.Pending, r => Assert.Equal(PayMethod.Cash, r.Payment.Method));
        Assert.True(vm.HasPending);
    }

    [Fact]
    public async Task Approving_asks_then_takes_the_cash_into_the_drawer()
    {
        var asked = new List<string>();
        Confirm.Answer = (t, m, b) => { asked.Add($"{t}|{m}|{b}"); return Task.FromResult(true); };
        var (gw, pay) = await CashPackage();
        var vm = await Page(gw);
        var row = vm.Pending.Single(r => r.Payment.Id == pay.Id);
        await vm.ApproveCommand.ExecuteAsync(row);
        var q = Assert.Single(asked);
        Assert.StartsWith($"Take {row.Payer}'s cash?|", q);
        Assert.Contains($"₱{pay.Amount:N2} into your drawer.", q);
        Assert.EndsWith("|Approve", q);
        Assert.DoesNotContain(row, vm.Pending);
        var saved = (await gw.ListAsync<Payment>(p => p.Id == pay.Id)).Single();
        Assert.Equal(Status.Verified, saved.Status);
        Assert.NotNull(saved.CashShiftId);
        Assert.StartsWith($"Took ₱{pay.Amount:N2} from {row.Payer}.", vm.Notice);
    }

    [Fact]
    public async Task Approving_without_a_shift_says_start_one_and_keeps_the_row()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var (gw, pay) = await CashPackage(drawer: false);
        var vm = await Page(gw);
        var row = vm.Pending.Single(r => r.Payment.Id == pay.Id);
        await vm.ApproveCommand.ExecuteAsync(row);
        Assert.StartsWith("Start your cash shift on the Cashier page first", vm.ErrorMessage);
        Assert.Contains(row, vm.Pending);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_removes_the_row()
    {
        var (gw, pay) = await CashPackage();
        var vm = await Page(gw);
        var row = vm.Pending.Single(r => r.Payment.Id == pay.Id);
        vm.RejectCommand.Execute(row);
        Assert.Same(row, vm.Rejecting);
        vm.RejectReason = "  ";
        await vm.ConfirmRejectCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains(row, vm.Pending);
        vm.RejectReason = "Did not come in";
        await vm.ConfirmRejectCommand.ExecuteAsync(null);
        Assert.Null(vm.Rejecting);
        Assert.DoesNotContain(row, vm.Pending);
        Assert.Equal(Status.Rejected, (await gw.ListAsync<Payment>(p => p.Id == pay.Id)).Single().Status);
    }

    [Fact]
    public void The_cashier_no_longer_lists_desk_bookings()
    {
        Assert.Null(typeof(CashierViewModel).GetProperty("DeskBookings"));
        Assert.Null(typeof(CashierViewModel).GetProperty("TakeBookingCashCommand"));
    }
}
