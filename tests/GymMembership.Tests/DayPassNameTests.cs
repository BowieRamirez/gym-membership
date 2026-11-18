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
