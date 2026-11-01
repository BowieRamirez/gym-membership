using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 4a: the segmented status filters above the Sessions and My membership tables
public class StatusFilterTests
{
    static async Task<DemoGateway> As(string role)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync($"{role}@demo.io", DemoGateway.DemoPassword);
        return gw;
    }

    sealed class NoPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string FileName, byte[] Data)?>(null); }

    [Fact]
    public async Task Sessions_filter_by_status_and_keep_the_choice_across_a_reload()
    {
        var vm = new SessionsViewModel(new SessionService(await As("coach")));
        await vm.OnAppearingAsync();
        var all = vm.Sessions.Count;
        Assert.Equal("All", vm.Filter);

        vm.SetFilterCommand.Execute("Scheduled");
        Assert.NotEmpty(vm.Sessions);
        Assert.All(vm.Sessions, r => Assert.Equal(Status.Scheduled, r.Session.Status));

        vm.SetFilterCommand.Execute("Completed");
        Assert.NotEmpty(vm.Sessions);
        Assert.All(vm.Sessions, r => Assert.Equal(Status.Completed, r.Session.Status));

        vm.SetFilterCommand.Execute("Cancelled");   // cancelled and no-shows together
        Assert.Contains(vm.Sessions, r => r.Session.Status == Status.Cancelled);
        Assert.Contains(vm.Sessions, r => r.Session.Status == Status.NoShow);
        Assert.All(vm.Sessions, r => Assert.Contains(r.Session.Status, new[] { Status.Cancelled, Status.NoShow }));

        var cancelled = vm.Sessions.Count;
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Cancelled", vm.Filter);
        Assert.Equal(cancelled, vm.Sessions.Count);

        vm.SetFilterCommand.Execute("All");
        Assert.Equal(all, vm.Sessions.Count);
    }

    [Fact]
    public async Task Membership_payments_filter_by_status()
    {
        var gw = await As("member");
        var vm = new MyMembershipViewModel(new MembershipService(gw), new NoPicker(), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        var all = vm.Payments.Count;
        Assert.NotEqual(0, all);

        vm.SetFilterCommand.Execute("Verified");
        Assert.Equal(all, vm.Payments.Count);    // the demo member's payments are all verified
        vm.SetFilterCommand.Execute("Pending");
        Assert.Empty(vm.Payments);
        Assert.StartsWith("No payments with this status", vm.PaymentsEmptyText);   // not "No payments yet"

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Pending", vm.Filter);
        Assert.Empty(vm.Payments);

        vm.SetFilterCommand.Execute("All");
        Assert.Equal(all, vm.Payments.Count);
    }

    [Fact]
    public async Task A_payment_waiting_for_review_shows_under_pending()
    {
        var gw = await As("bea");
        var vm = new MyMembershipViewModel(new MembershipService(gw), new NoPicker(), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        vm.SetFilterCommand.Execute("Pending");
        Assert.Contains(vm.Payments, p => p.PaymentCode == "GYM-7F3K2");
        vm.SetFilterCommand.Execute("Expired");
        Assert.Empty(vm.Payments);
    }
}
