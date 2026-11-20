using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MyMembershipCoachingTests
{
    [Fact]
    public async Task Shows_hired_coach_and_current_or_next_session_on_my_membership()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MyMembershipViewModel(new MembershipService(gw), new SubscriptionService(gw), new AttendanceService(gw), new SessionService(gw));
        await vm.OnAppearingAsync();

        // Alex has an active hire with Lena Cruz and an upcoming scheduled session in DemoGateway
        Assert.Contains("Lena", vm.HiredCoachName);
        Assert.NotEqual("None scheduled", vm.CurrentSessionText);
        Assert.Contains("Lena", vm.CurrentSessionNote);
    }

    [Fact]
    public async Task Shows_defaults_when_member_has_no_hired_coach_or_session()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Bea", "Cruz", "bea.coach@demo.io", "secret12", true)).Ok);
        var vm = new MyMembershipViewModel(new MembershipService(gw), new SubscriptionService(gw), new AttendanceService(gw), new SessionService(gw));
        await vm.OnAppearingAsync();

        Assert.Equal("None yet", vm.HiredCoachName);
        Assert.Equal("None scheduled", vm.CurrentSessionText);
    }
}
