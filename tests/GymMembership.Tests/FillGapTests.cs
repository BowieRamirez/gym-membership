using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

// G26 (owner, 2026-11-15; 0070): a paid term fills a gap a refund left, and a term bought ahead that it runs into moves back.
public class FillGapTests
{
    [Fact]
    public async Task After_a_refund_a_new_term_starts_today_and_pushes_the_term_bought_ahead()
    {
        var (gw, _, svc, monthly, annual) = await SubscriptionTests.Setup();
        var p = (await svc.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var refunded = Assert.Single((await svc.TermsAsync()).Value!).Term;
        p = (await svc.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var ahead = (await svc.TermsAsync()).Value!.Single(t => t.Term.PaymentId == p.Id).Term;

        Assert.True((await svc.RequestRefundAsync(refunded.Id, "Changed plans", "change_of_mind")).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.DecideAsync(Assert.Single((await svc.RefundsAsync()).Value!).Request.Id, true, "Approve")).Ok);
        await gw.SignInAsync("phase7@demo.io", "secret12");
        Assert.False((await new MembershipService(gw).AccessAsync()).Value!.Active);   // the gap

        p = (await svc.ExtendAsync(annual.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var paid = (await gw.ListAsync<Payment>(x => x.Id == p.Id)).Single();
        var terms = (await gw.ListAsync<MembershipTerm>(t => t.MemberId == gw.CurrentUserId && t.CancelledAt == null)).OrderBy(t => t.StartsAt).ToList();
        Assert.Equal([p.Id, ahead.PaymentId], terms.Select(t => t.PaymentId));
        Assert.Equal(paid.VerifiedAt, terms[0].StartsAt);                        // starts the day it is verified
        Assert.Equal(terms[0].EndsAt, terms[1].StartsAt);                         // the term bought ahead moves back
        Assert.Equal(ahead.EndsAt - ahead.StartsAt, terms[1].EndsAt - terms[1].StartsAt);   // and keeps its length
        Assert.True((await new MembershipService(gw).AccessAsync()).Value!.Active);
    }
}
