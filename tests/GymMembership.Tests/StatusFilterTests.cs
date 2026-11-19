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
    public async Task Sessions_list_what_needs_doing_first()
    {
        // 9.6: scheduled sessions soonest first, then history newest first (the list used to start with October's history)
        var vm = new SessionsViewModel(new SessionService(await As("coach")));
        await vm.OnAppearingAsync();
        var rows = vm.Sessions.ToList();
        var scheduled = rows.TakeWhile(r => r.Session.Status == Status.Scheduled).ToList();
        Assert.NotEmpty(scheduled);
        Assert.DoesNotContain(rows.Skip(scheduled.Count), r => r.Session.Status == Status.Scheduled);
        Assert.Equal(scheduled.OrderBy(r => r.Session.ScheduledStart), scheduled);
        var past = rows.Skip(scheduled.Count).ToList();
        Assert.Equal(past.OrderByDescending(r => r.Session.ScheduledStart), past);
    }

    // owner fixes 2026-11-19 Q8: the payment list moved to Payments, filtered by what was bought (MyPaymentsTests)

    [Fact]
    public async Task A_payment_waiting_for_review_shows_under_pending()
    {
        var gw = await As("bea");
        var vm = new MyPaymentsViewModel(new MembershipService(gw), new SubscriptionService(gw));   // Q8: on Payments now
        await vm.OnAppearingAsync();
        Assert.Equal("Pending", vm.Rows.Single(p => p.PaymentCode == "GYM-7F3K2").StatusText);
    }
}
