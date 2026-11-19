using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Plan 2026-11-17 Z7 (owner's sketch): Buy opens a popup with the code box and the package. Owner fixes 2026-11-19 Q6: members pay
/// online only, so Buy in the popup makes the online payment at once (no Online / Cash step).</summary>
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
    public async Task Buy_opens_the_popup_with_no_code()
    {
        var (_, _, vm, pkg) = await Open();
        vm.DiscountCode = "OLD";
        vm.OpenBuyCommand.Execute(pkg);
        Assert.Same(pkg, vm.Buying);
        Assert.True(vm.IsBuying);
        Assert.Null(vm.DiscountCode);
        Assert.Contains(RefundRules.Line, vm.BuyTerms);   // G23: the refund rule is said before anyone pays
    }

    [Fact]   // Q6: there is no cash choice for members any more
    public async Task Buy_pays_online_with_no_cash_choice()
    {
        var (_, _, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
        await vm.PayCommand.ExecuteAsync(null);
        Assert.Equal(PayMethod.Qr, vm.PayNow!.Method);
        Assert.Equal("Payment created. Pay online on this page to start it.", vm.Notice);
    }

    [Fact]
    public async Task Pay_online_shows_the_pay_card()
    {
        var (_, _, vm, pkg) = await Open();
        vm.OpenBuyCommand.Execute(pkg);
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
        await vm.PayCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsBuying);
        Assert.Equal("NOSUCHCODE", vm.DiscountCode);
        Assert.Empty((await member.MyPaymentsAsync()).Value!);
    }
}
