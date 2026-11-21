using GymMembership.Core.Demo;
using GymMembership.Core.Models;

// 0131: member check-ins follow gym hours. Tests that check in on the real clock open the demo gym all day,
// so they pass at any time of day (CheckInHoursTests covers the hours themselves with a fixed clock).
static class DemoHours
{
    public static DemoGateway OpenAllDay(this DemoGateway gw)
    {
        gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword).GetAwaiter().GetResult();
        var g = gw.ListAsync<GymSettings>().GetAwaiter().GetResult().First();
        g.OpensAt = TimeSpan.Zero; g.ClosesAt = TimeSpan.FromDays(1);
        gw.UpdateAsync(g).GetAwaiter().GetResult();
        gw.SignOutAsync().GetAwaiter().GetResult();
        return gw;
    }
}
