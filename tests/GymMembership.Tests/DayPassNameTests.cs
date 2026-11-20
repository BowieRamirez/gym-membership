using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Plan 2026-11-17 Z3: the Day pass name box is the one source; a picked member only fills it.</summary>
public class DayPassNameTests
{
    static async Task<(DemoGateway Gw, CashierViewModel Vm)> Open()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        vm.Search = "carlo"; await vm.FindCommand.ExecuteAsync(null);
        vm.PickMemberCommand.Execute(vm.Hits.Single());
        return (gw, vm);
    }

    static async Task<Payment> Last(DemoGateway gw) => (await gw.ListAsync<Payment>()).OrderByDescending(p => p.Id).First();

    [Fact]
    public async Task Typed_name_wins_over_an_old_pick()
    {
        var (gw, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        vm.WalkInName = "Juan Cruz";
        Assert.Equal("Juan Cruz", vm.OrderWho);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Equal("Juan Cruz", (await Last(gw)).WalkInName);
    }

    [Fact]
    public async Task Picking_a_member_fills_the_name_box()
    {
        var (_, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        Assert.Equal("Carlo Diaz", vm.WalkInName);
    }

    [Fact]
    public async Task Clearing_the_name_box_makes_it_anonymous()
    {
        var (gw, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        vm.WalkInName = "";
        Assert.Equal("Day pass, no name", vm.OrderWho);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Equal("Day pass", (await Last(gw)).WalkInName);
    }

    [Fact]   // owner fixes 2026-11-19 Q9 (0098): a picked member's Day pass is the Day pass package, so it makes them a member until midnight
    public async Task A_picked_members_day_pass_is_the_package_and_makes_them_a_member()
    {
        var (gw, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        await vm.RecordCommand.ExecuteAsync(null);
        var pay = await Last(gw);
        Assert.Equal((PayKind.Membership, "u-carlo"), (pay.Kind, pay.MemberId));
        var term = (await gw.ListAsync<MembershipTerm>(t => t.PaymentId == pay.Id)).Single();
        Assert.Equal(term.StartsAt.AddHours(8).Date.AddDays(1).AddHours(-8), term.EndsAt);
    }

    [Fact]   // Q9: the desk reads out the Day pass package's price
    public async Task The_day_pass_total_is_the_packages_price()
    {
        var (gw, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        var price = (await gw.ListAsync<MembershipPackage>(p => p.IsDayPass)).Single().Price;
        Assert.Equal($"₱{price:N2}", vm.TotalText);
    }

    [Fact]   // final review: a name typed for a Day pass survives switching to another kind and back
    public async Task A_typed_name_survives_switching_kind()
    {
        var (_, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        vm.WalkInName = "Juan Cruz";
        vm.PickKindCommand.Execute("Membership");
        vm.PickKindCommand.Execute("Day pass");
        Assert.Equal("Juan Cruz", vm.OrderWho);
    }
}
