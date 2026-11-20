using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Cash plan (2026-11-17) let a member pick cash for a package. Owner fixes 2026-11-19 Q6: members pay online only;
/// cash is the front desk's, recorded against the member on the Cashier page.</summary>
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

    [Fact]   // Q6: an older app that still sends cash is refused, and nothing is made
    public async Task A_member_choosing_cash_is_refused()
    {
        var (_, member, subs, pkg) = await NewMember();
        var r = await subs.ExtendAsync(pkg.Id, null, PayMethod.Cash);
        Assert.Equal("online_only", r.Message);
        Assert.Contains("front desk", ErrorText.For(r.Error, r.Message));
        Assert.Empty((await member.MyPaymentsAsync()).Value!);
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

    [Fact]   // Q6: paying again for an old cash payment makes an online one
    public async Task Pay_again_goes_online()
    {
        var (gw, member, subs, pkg) = await NewMember();
        var pay = (await subs.ExtendAsync(pkg.Id)).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, false, "Did not come in")).Ok);
        await gw.SignInAsync("ana@demo.io", "secret12");
        var item = (await member.MyPaymentsAsync()).Value!.Single() with { Method = PayMethod.Cash };
        Assert.Equal(PayMethod.Qr, (await member.PayAgainAsync(item)).Value!.Method);
    }
}
