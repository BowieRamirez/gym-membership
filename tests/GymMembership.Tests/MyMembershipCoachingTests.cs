using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MyMembershipCoachingTests
{
    [Fact]
    public async Task Shows_next_coach_session_and_package_coach_status_on_my_membership()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MyMembershipViewModel(new MembershipService(gw), new SubscriptionService(gw), new AttendanceService(gw), new SessionService(gw));
        await vm.OnAppearingAsync();

        // Alex has an upcoming scheduled session in DemoGateway
        Assert.NotEqual("None scheduled", vm.CurrentSessionText);
        Assert.False(string.IsNullOrWhiteSpace(vm.CoachIncludedNote));
    }

    [Fact]
    public async Task Shows_defaults_when_member_has_no_session()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Bea", "Cruz", "bea.coach@demo.io", "secret12", true)).Ok);
        var vm = new MyMembershipViewModel(new MembershipService(gw), new SubscriptionService(gw), new AttendanceService(gw), new SessionService(gw));
        await vm.OnAppearingAsync();

        Assert.Equal("None scheduled", vm.CurrentSessionText);
        Assert.Equal("Book a coach on Sessions", vm.CurrentSessionNote);
    }
}
