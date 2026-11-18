using GymMembership.Core.Demo;
using GymMembership.Core.ViewModels;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Cash plan (2026-11-17): a member can pick cash for a package; it waits for the front desk.</summary>
public class CashPackageTests
{
    static async Task<(DemoGateway gw, MembershipService member, SubscriptionService subs, MembershipPackage pkg)> NewMember()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        var pkg = (await member.ListPackagesAsync()).Value!.First(p => p.Name == "Quarterly");
        return (gw, member, new SubscriptionService(gw), pkg);
    }

    [Fact]
    public async Task Extending_with_cash_creates_a_pending_cash_payment()
    {
        var (_, member, subs, pkg) = await NewMember();
        var pay = (await subs.ExtendAsync(pkg.Id, null, PayMethod.Cash)).Value!;
        Assert.Equal((PayMethod.Cash, Status.Pending), (pay.Method, pay.Status));
        Assert.NotNull(pay.ExpiresAt);
        var item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.False(item.NeedsPayment);   // no Pay online card for cash
    }

    [Fact]
    public async Task Online_stays_the_default()
    {
        var (_, _, subs, pkg) = await NewMember();
        Assert.Equal(PayMethod.Qr, (await subs.ExtendAsync(pkg.Id)).Value!.Method);
    }

    [Fact]
    public async Task Unknown_method_is_refused()
    {
        var (_, _, subs, pkg) = await NewMember();
        Assert.Equal(AppErrorKind.Validation, (await subs.ExtendAsync(pkg.Id, null, PayMethod.Gcash)).Error);
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task A_cash_payment_past_its_deadline_cannot_be_approved_and_stops_showing_as_due()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var gw = new DemoGateway(clock: clock);
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        var pkg = (await member.ListPackagesAsync()).Value!.First(p => p.Name == "Quarterly");
        var pay = (await new SubscriptionService(gw).ExtendAsync(pkg.Id, null, PayMethod.Cash)).Value!;
        var item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.True(item.PaysAtDesk);
        Assert.False((item with { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) }).PaysAtDesk);   // PaymentItem reads the real clock, like NeedsPayment
        clock.Now = pay.ExpiresAt!.Value.AddMinutes(1);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await gw.OpenDrawerAsync();
        Assert.False((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);
    }

    [Fact]
    public async Task My_membership_counts_a_cash_payment_as_waiting_for_the_desk()
    {
        var (_, member, subs, pkg) = await NewMember();
        await subs.ExtendAsync(pkg.Id, null, PayMethod.Cash);
        var vm = new MyMembershipViewModel(member, subs);
        await vm.OnAppearingAsync();
        Assert.False(vm.CanStart);
        Assert.Equal($"Pay ₱{pkg.Price:N2} in cash at the front desk; your membership starts once the desk verifies it.", vm.HeroCaption);
    }

    [Fact]
    public async Task Pay_again_keeps_cash()
    {
        var (gw, member, subs, pkg) = await NewMember();
        var pay = (await subs.ExtendAsync(pkg.Id, null, PayMethod.Cash)).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, false, "Did not come in")).Ok);
        await gw.SignInAsync("ana@demo.io", "secret12");
        var item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.Equal(PayMethod.Cash, (await member.PayAgainAsync(item)).Value!.Method);
    }
}
