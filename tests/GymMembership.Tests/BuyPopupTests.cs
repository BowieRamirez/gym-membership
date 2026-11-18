using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Plan 2026-11-17 Z7 (owner's sketch): Buy opens a popup with the code box and the package; Buy in it asks Online or Cash, then Pay.</summary>
public class BuyPopupTests
{
    static async Task<(DemoGateway Gw, MembershipService Member, PackagesViewModel Vm, MembershipPackage Pkg)> Open()
    {
        Confirm.Answer = (_, _, _) => throw new InvalidOperationException("the popup is the confirmation; nothing else should ask");
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        var vm = new PackagesViewModel(member, new ConfirmationTests.Nav(), new SubscriptionService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        return (gw, member, vm, vm.Packages.First(p => p.Name == "Quarterly"));
    }

    [Fact]
    public async Task Buy_opens_the_popup_on_step_1_with_no_code()
    {
        var (_, _, vm, pkg) = await Open();
        vm.DiscountCode = "OLD";
        vm.OpenBuyCommand.Execute(pkg);
        Assert.Same(pkg, vm.Buying);
        Assert.True(vm.IsBuying);
        Assert.Equal((true, false), (vm.OnCodeStep, vm.OnPayStep));
        Assert.Null(vm.DiscountCode);
        Assert.Contains(RefundRules.Line, vm.BuyTerms);   // G23: the refund rule is said before anyone pays
    }

    [Fact]
    public async Task Next_shows_online_and_cash_online_first()
    {
        var (_, _, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
        vm.PickMethodCommand.Execute(PayMethod.Cash);
        vm.CloseBuyCommand.Execute(null);
        vm.OpenBuyCommand.Execute(pkg);
        vm.NextCommand.Execute(null);
        Assert.Equal((false, true), (vm.OnCodeStep, vm.OnPayStep));
        Assert.False(vm.PayCash);
        vm.BackCommand.Execute(null);
        Assert.True(vm.OnCodeStep);
    }

    [Fact]
    public async Task Pay_with_cash_creates_a_desk_payment_and_closes()
    {
        var (_, _, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
        vm.NextCommand.Execute(null);
        vm.PickMethodCommand.Execute(PayMethod.Cash);
        await vm.PayCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsBuying);
        Assert.Equal(PayMethod.Cash, vm.PayAtDesk!.Method);
        Assert.Equal($"Payment created. Pay ₱{pkg.Price:N2} in cash at the front desk; your membership starts once the desk verifies it.", vm.Notice);
    }

    [Fact]
    public async Task Pay_online_shows_the_pay_card()
    {
        var (_, _, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
        vm.NextCommand.Execute(null);
        await vm.PayCommand.ExecuteAsync(null);
        Assert.False(vm.IsBuying);
        Assert.NotNull(vm.PayNow);
        Assert.Null(vm.PayAtDesk);
    }

    [Fact]
    public async Task Closing_buys_nothing_and_clears_the_code()
    {
        var (_, member, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
        vm.DiscountCode = "WELCOME10";
        vm.NextCommand.Execute(null);
        vm.CloseBuyCommand.Execute(null);
        Assert.False(vm.IsBuying);
        Assert.Null(vm.DiscountCode);
        Assert.Empty((await member.MyPaymentsAsync()).Value!);
    }

    [Fact]
    public async Task A_bad_code_keeps_the_popup_open_with_the_error()
    {
        var (_, member, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
        vm.DiscountCode = "NOSUCHCODE";
        vm.NextCommand.Execute(null);
        await vm.PayCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsBuying);
        Assert.Equal("NOSUCHCODE", vm.DiscountCode);
        Assert.Empty((await member.MyPaymentsAsync()).Value!);
    }
}
