using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// G30 (owner, 2026-11-15; 0073/0074): Hire sends a request; the coach accepts or declines on their Sessions page.
public class HireRequestTests
{
    static async Task<(DemoGateway Gw, CoachService Svc)> NewMember()
    {
        var (gw, _, subs, monthly, _) = await SubscriptionTests.Setup();
        var p = (await subs.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        return (gw, new CoachService(gw));
    }
    /// <summary>The coach accepts the newest waiting request, then the member signs back in.</summary>
    internal static async Task Accept(DemoGateway gw, string coachEmail, string memberEmail, string memberPassword)
    {
        await gw.SignInAsync(coachEmail, DemoGateway.DemoPassword);
        var svc = new CoachService(gw);
        Assert.True((await svc.AnswerHireAsync((await svc.HireRequestsAsync()).Value!.Last().HireId, true)).Ok);   // the newest: the one just sent
        await gw.SignInAsync(memberEmail, memberPassword);
    }
    static async Task<CoachCard> Card(CoachService svc, string name) => (await svc.ListCoachesAsync()).Value!.Single(c => c.Name == name);

    [Fact]
    public async Task Hire_sends_a_request_that_unlocks_nothing_and_can_be_cancelled()
    {
        var (gw, svc) = await NewMember();
        Assert.True((await svc.HireAsync("u-coach2")).Ok);
        var kai = await Card(svc, "Kai Mendoza");
        Assert.True(kai.Requested); Assert.False(kai.Hired); Assert.False(kai.CanHire); Assert.Equal("requested", kai.HireBadge); Assert.False(kai.ShowHire); Assert.Equal("Waiting for Kai to accept.", kai.WaitingText);

        var again = await svc.HireAsync("u-coach2");
        Assert.Equal("You have already asked this coach. Wait for them to answer, or cancel the request.", ErrorText.For(again.Error, again.Message));

        Assert.True((await svc.CancelHireRequestAsync(kai.RequestId!.Value)).Ok);
        kai = await Card(svc, "Kai Mendoza");
        Assert.False(kai.Requested); Assert.True(kai.CanHire); Assert.Null(kai.HireBadge);
    }

    [Fact]
    public async Task The_coach_sees_coached_members_from_sessions()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var svc = new CoachService(gw);
        Assert.Contains((await svc.TraineesAsync()).Value!, t => t.Name == "Alex Tan");
        Assert.Contains((await svc.TraineesAsync()).Value!, t => t.Name == "Bea Lim");
    }

    [Fact]
    public async Task A_declined_request_puts_Hire_back_and_tells_the_member()
    {
        var (gw, svc) = await NewMember();
        Assert.True((await svc.HireAsync("u-coach2")).Ok);
        await gw.SignInAsync("kai@demo.io", DemoGateway.DemoPassword);
        var request = (await svc.HireRequestsAsync()).Value!.Single(r => r.Name == "Ana Reyes");
        Assert.True((await svc.AnswerHireAsync(request.HireId, false)).Ok);
        Assert.False((await svc.AnswerHireAsync(request.HireId, true)).Ok);   // answered once

        await gw.SignInAsync("phase7@demo.io", "secret12");
        var kai = await Card(svc, "Kai Mendoza");
        Assert.False(kai.Requested); Assert.False(kai.Hired); Assert.True(kai.CanHire);
        Assert.Contains(await gw.ListAsync<UserNotification>(n => n.UserId == gw.CurrentUserId), n => n.Title == "Hire declined");
    }

    [Fact]
    public async Task On_the_coach_Sessions_page_TraineesViewModel_lists_coached_members_newest_first()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var svc = new CoachService(gw);
        var vm = new TraineesViewModel(svc, new ChatService(gw, new NotificationService(gw)), new NamesAndEmailTests.NullNavigator());
        await vm.OnAppearingAsync();

        Assert.NotEmpty(vm.Trainees);
        Assert.DoesNotContain(MemberMenu.Order(true), m => m.Page == "Time requests");
        Assert.DoesNotContain(MemberMenu.Order(false), m => m.Page == "Time requests");
    }
}
