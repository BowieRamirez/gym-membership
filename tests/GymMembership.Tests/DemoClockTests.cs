using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using Xunit;

// The demo seeds from the clock it is given, so a test with a fixed day keeps working whatever today's date is
// (15 tests broke on 2026-11-14 when the seed still used the real clock).
public class DemoClockTests
{
    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Theory]
    [InlineData(2020)]
    [InlineData(2035)]
    public async Task The_seed_member_has_a_running_membership_on_the_given_clock(int year)
    {
        var now = new DateTimeOffset(year, 3, 1, 2, 0, 0, TimeSpan.Zero);
        var gw = new DemoGateway(clock: new Clock(now));
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var terms = await gw.ListAsync<MembershipTerm>(t => t.MemberId == "u-alex");
        Assert.Contains(terms, t => t.StartsAt <= now.UtcDateTime && t.EndsAt > now.UtcDateTime);
    }
}
