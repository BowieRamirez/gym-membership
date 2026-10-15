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

    [Fact]
    public async Task The_unread_count_is_the_number_of_alerts_without_a_read_time()
    {
        var gw = await As("member@demo.io");
        var svc = new NotificationService(gw);
        var expected = (await svc.ListAsync()).Value!.Count(n => n.ReadAt is null);
        Assert.True(expected > 0);
        Assert.Equal(expected, (await svc.UnreadCountAsync()).Value);
    }

    [Fact]
    public async Task Mark_all_read_clears_the_count_and_announces_one_change()
    {
        var gw = await As("member@demo.io");
        var svc = new NotificationService(gw);
        var changes = 0;
        svc.Changed += () => changes++;

        Assert.True((await svc.MarkAllReadAsync()).Ok);

        Assert.Equal(0, (await svc.UnreadCountAsync()).Value);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Reading_one_alert_announces_a_change_and_a_failure_announces_nothing()
    {
        var gw = await As("member@demo.io");
        var svc = new NotificationService(gw);
        var changes = 0;
        svc.Changed += () => changes++;

        await svc.MarkReadAsync((await svc.ListAsync()).Value!.First(n => n.ReadAt is null).Id);
        Assert.Equal(1, changes);

        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await svc.MarkAllReadAsync()).Error);
        Assert.Equal(AppErrorKind.Offline, (await svc.UnreadCountAsync()).Error);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Two_hundred_alerts_come_back_newest_first_and_mark_all_is_still_one_call()
    {
        var gw = await As("member@demo.io");
        for (var i = 0; i < 200; i++) gw.Notify(gw.CurrentUserId, "system", $"n{i}");   // the demo's server-side "send an alert"
        var svc = new NotificationService(gw);

        var list = (await svc.ListAsync()).Value!;
        Assert.True(list.Count >= 200);
        Assert.Equal(list.OrderByDescending(n => n.CreatedAt).Select(n => n.Id), list.Select(n => n.Id));

        Assert.True((await svc.MarkAllReadAsync()).Ok);
        Assert.Equal(0, (await svc.UnreadCountAsync()).Value);
    }
}
