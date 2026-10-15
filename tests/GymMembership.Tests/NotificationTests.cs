using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class NotificationTests
{
    static async Task<DemoGateway> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Fact]
    public async Task The_demo_filtered_update_touches_only_your_own_matching_rows()
    {
        var gw = await As("member@demo.io");
        var before = await gw.ListAsync<UserNotification>();
        var read = before.Where(n => n.ReadAt is not null).ToDictionary(n => n.Id, n => n.ReadAt);
        Assert.Contains(before, n => n.ReadAt is null);
        var stamp = DateTime.UtcNow;

        await gw.UpdateWhereAsync<UserNotification, DateTime?>(n => n.UserId == gw.CurrentUserId && n.ReadAt == null, n => n.ReadAt, stamp);

        var after = await gw.ListAsync<UserNotification>();
        Assert.All(after, n => Assert.NotNull(n.ReadAt));
        foreach (var (id, was) in read) Assert.Equal(was, after.First(n => n.Id == id).ReadAt);   // already-read rows keep their time
    }
}
