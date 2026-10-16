using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class DeskHomeTests
{
    static async Task<(DemoGateway Gw, ChatService Chat, DeskHomeService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw));
        return (gw, chat, new DeskHomeService(gw, chat));
    }

    [Fact]
    public async Task Amenities_are_counted_by_status_and_add_up_to_the_active_ones()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var s = (await svc.SummaryAsync()).Value!;
        var active = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        Assert.Equal(active.Count, s.AmenitiesTotal);
        Assert.Equal(active.Count(a => a.Availability == "unavailable"), s.AmenitiesUnavailable);   // the demo's sauna is out of service
        Assert.True(s.AmenitiesUnavailable >= 1);
    }

    [Fact]
    public async Task Payments_to_verify_match_the_pending_list()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var pending = (await new PaymentStaffService(gw).PendingAsync()).Value!;
        Assert.Equal(pending.Count, (await svc.SummaryAsync()).Value!.PaymentsToVerify);
    }

    [Fact]
    public async Task A_check_in_today_adds_one()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var before = (await svc.SummaryAsync()).Value!.CheckInsToday;
        Assert.True((await new AttendanceService(gw).CheckInAsync("u-alex")).Ok);
        Assert.Equal(before + 1, (await svc.SummaryAsync()).Value!.CheckInsToday);
    }

    [Fact]
    public async Task A_member_writing_to_the_front_desk_shows_as_unread_and_not_taken()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var before = (await svc.SummaryAsync()).Value!;

        var (_, memberChat, _) = await As("carlo@demo.io", gw);
        var mine = (await memberChat.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.True((await memberChat.SendAsync(mine, "my payment is stuck")).Ok);

        await As("employee@demo.io", gw);
        var after = (await svc.SummaryAsync()).Value!;
        Assert.Equal(before.UnreadMessages + 1, after.UnreadMessages);
        Assert.Equal(before.UnclaimedChats + 1, after.UnclaimedChats);
    }

    [Fact]
    public async Task A_members_own_front_desk_thread_is_not_counted_as_an_inbox_message()
    {
        var (_, _, svc) = await As("carlo@demo.io");   // a member has a Front desk thread but no inbox
        var s = (await svc.SummaryAsync()).Value!;
        Assert.Equal(0, s.UnreadMessages);
        Assert.Equal(0, s.UnclaimedChats);
    }
}
