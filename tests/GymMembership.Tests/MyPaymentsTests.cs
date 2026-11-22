using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q8: members get their own Payments page: only their payments, newest first, 10 a page,
/// filtered by what they bought (Membership, Day pass included, or Amenity). My membership no longer lists them.</summary>
public class MyPaymentsTests
{
    static async Task<(DemoGateway Gw, MyPaymentsViewModel Vm)> Open(string email = "member@demo.io")
    {
        var gw = new DemoGateway(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new MyPaymentsViewModel(new MembershipService(gw), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task Lists_only_my_payments_newest_first()
    {
        var (gw, vm) = await Open();
        var mine = await gw.ListAsync<Payment>(p => p.MemberId == "u-alex");
        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r => Assert.Contains(mine, p => p.Id == r.Id));
        Assert.Equal(vm.Rows.OrderByDescending(r => r.CreatedAt), vm.Rows);
    }

    [Fact]
    public async Task Someone_elses_payment_never_shows()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var monthly = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        var carlos = (await new PaymentStaffService(gw).RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-carlo", monthly.Id, null, null, null)).Value!;
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MyPaymentsViewModel(new MembershipService(gw), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        Assert.DoesNotContain(vm.Rows, r => r.Id == carlos.Id);
    }

    [Fact]
    public async Task Filters_by_what_was_bought()
    {
        var (_, vm) = await Open();
        vm.SetKindCommand.Execute("Membership");
        Assert.All(vm.Rows, r => Assert.True(r.Kind is PayKind.Membership or PayKind.Renewal));
        vm.SetKindCommand.Execute("Amenity");
        Assert.All(vm.Rows, r => Assert.Equal(PayKind.Amenity, r.Kind));
        Assert.Equal("No amenity payments yet.", vm.EmptyText);
    }

    [Fact]
    public async Task Ten_a_page()
    {
        var (gw, vm) = await Open();
        var count = (await gw.ListAsync<Payment>(p => p.MemberId == "u-alex")).Count;
        Assert.Equal(Math.Min(10, count), vm.Rows.Count);
        Assert.Equal(count > 10, vm.HasMore);
    }

    [Fact]
    public void A_row_says_how_it_was_paid_and_its_status_in_plain_words()
    {
        var row = new PaymentItem(1, "Monthly", "GYM-1", PayKind.Membership, PayMethod.Qr, 1500, 0, Status.Verified, DateTime.UtcNow, null, null, false, "OR-1", null, 1);
        Assert.Equal(("Online", "Paid"), (row.MethodText, row.StatusText));
        Assert.Equal("Cash", (row with { Method = PayMethod.Cash }).MethodText);
        Assert.Equal("Failed", (row with { Status = Status.Expired }).StatusText);
    }

    [Fact]
    public void Payments_is_in_the_menu_with_or_without_a_membership()
    {
        Assert.Contains(MemberMenu.Order(true), i => i.Page == "Payments" && !i.MembersOnly);
        Assert.Contains(MemberMenu.Order(false), i => i.Page == "Payments" && !i.MembersOnly);
    }

    [Fact]   // Q11: a payment whose refund was repaid says Refunded
    public async Task A_repaid_refund_shows_as_Refunded_on_my_payments()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Bea", "Cruz", "bea.ref@demo.io", "secret12", true)).Ok);
        var uid = gw.CurrentUserId!;
        var member = new MembershipService(gw);
        var monthly = (await member.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        var pay = (await member.AvailAsync(monthly.Id)).Value!;
        Assert.True((await member.PayOnlineAsync(pay.Id)).Value!.Paid);
        var term = (await gw.ListAsync<MembershipTerm>(t => t.PaymentId == pay.Id)).Single();
        var sub = new SubscriptionService(gw);
        Assert.True((await sub.RequestRefundAsync(term.Id, "Changed my mind", null)).Ok);
        var req = (await gw.ListAsync<RefundRequest>(r => r.PaymentId == pay.Id)).Single();

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var staff = new SubscriptionService(gw);
        Assert.True((await staff.DecideAsync(req.Id, true, "OK")).Ok);
        await gw.OpenDrawerAsync();
        Assert.True((await staff.MarkPaidAsync(req.Id, PayMethod.Cash, null)).Ok);

        await gw.SignInAsync("bea.ref@demo.io", "secret12");
        var vm = new MyPaymentsViewModel(new MembershipService(gw), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        var row = vm.Rows.Single(r => r.Id == pay.Id);
        Assert.Equal("Refunded", row.StatusText);
    }

    [Fact]
    public async Task Filters_by_paid_with_dropdown()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var sauna = (await gw.ListAsync<Amenity>()).First(a => a.Name == "Sauna");
        var res = await new PaymentStaffService(gw).RecordCounterAsync(PayKind.Amenity, PayMethod.Cash, "u-alex", sauna.Id, null, null, null);
        Assert.True(res.Ok);
        var cashPay = res.Value!;
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);

        var vm = new MyPaymentsViewModel(new MembershipService(gw), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal(["All", "Cash", "Online"], vm.PaidWiths);

        vm.PaidWithIndex = 1;   // Cash
        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r => Assert.Equal(PayMethod.Cash, r.Method));
        Assert.Contains(vm.Rows, r => r.Id == cashPay.Id);

        vm.PaidWithIndex = 2;   // Online
        Assert.All(vm.Rows, r => Assert.NotEqual(PayMethod.Cash, r.Method));
        Assert.DoesNotContain(vm.Rows, r => r.Id == cashPay.Id);

        vm.SetKindCommand.Execute("Amenity");
        Assert.Equal("No online amenity payments yet.", vm.EmptyText);
    }

    [Fact]
    public async Task Coach_addon_payment_is_labelled_Coach_addon_not_Day_pass()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var member = new MembershipService(gw);
        var pay = (await member.BuyCoachAccessAsync()).Value!;
        Assert.True((await member.PayOnlineAsync(pay.Id)).Value!.Paid);

        var vm = new MyPaymentsViewModel(member, new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        var row = vm.Rows.Single(r => r.Id == pay.Id);
        Assert.StartsWith("Coach add-on", row.What);
        Assert.DoesNotContain("Day pass", row.What);
    }
}
