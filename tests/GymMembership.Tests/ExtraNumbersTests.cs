using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 4c: the extra number cards on My membership, Time requests and the coach Dashboard
public class ExtraNumbersTests
{
    static async Task<DemoGateway> As(string role)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync($"{role}@demo.io", DemoGateway.DemoPassword);
        return gw;
    }

    sealed class NoPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string FileName, byte[] Data)?>(null); }

    static async Task<MyMembershipViewModel> Membership(string role)
    {
        var gw = await As(role);
        var vm = new MyMembershipViewModel(new MembershipService(gw), new NoPicker(), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        return vm;
    }

    [Fact]
    public async Task My_membership_shows_when_the_current_term_ends_and_how_many_payments_wait()
    {
        var alex = await Membership("member");
        var current = alex.Terms.Single(t => t.State == Status.Active);
        Assert.Equal($"{current.Term.EndsAt.AddHours(8):d MMM yyyy}", alex.TermEndsText);
        Assert.Equal("0", alex.PaymentsWaitingText);

        var bea = await Membership("bea");          // paid by QR, waiting for the front desk, no term yet
        Assert.Equal("None", bea.TermEndsText);
        Assert.Equal("1", bea.PaymentsWaitingText);
    }

    [Fact]
    public async Task Time_requests_count_what_waits_and_show_the_next_session()
    {
        var gw = await As("coach");
        var vm = new RequestsViewModel(new SessionService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal(vm.Requests.Count.ToString(), vm.WaitingText);
        Assert.NotEqual("None booked", vm.NextSessionText);   // the demo coach has upcoming sessions
    }

    [Fact]
    public async Task The_dashboard_counts_the_coachs_trainees()
    {
        var gw = await As("coach");
        var vm = new AttendanceViewModel(new SessionService(gw), new AttendanceService(gw), new CoachService(gw), new MembershipService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal((await new CoachService(gw).TraineesAsync()).Value!.Count.ToString(), vm.TraineesText);
        Assert.NotEqual("0", vm.TraineesText);
    }
}
