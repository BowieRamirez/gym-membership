using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q3 (0102): at most 10 package payments a member starts in an hour.</summary>
public class BuyLimitTests
{
    [Fact]
    public async Task The_11th_package_payment_in_an_hour_is_refused_with_its_own_sentence()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw); var subs = new SubscriptionService(gw);
        var pkg = (await member.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        for (var i = 0; i < 10; i++)
            Assert.True((await subs.CancelPendingPaymentAsync((await subs.ExtendAsync(pkg.Id)).Value!.Id)).Ok);
        var r = await subs.ExtendAsync(pkg.Id);
        Assert.Equal("too_many_tries", r.Message);
        Assert.Contains("10", ErrorText.For(r.Error, r.Message));
    }
}
