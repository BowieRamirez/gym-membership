using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Cashier plan Y1 (2026-11-16): the member search shows its results, matches name, email or phone, and says when nobody matches.
/// Demo: Bea Lim (bea@demo.io) has the phone 0917 555 0142.</summary>
public class CashierSearchTests
{
    static async Task<CashierViewModel> Open()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        return vm;
    }

    static async Task<CashierViewModel> Find(string text) { var vm = await Open(); vm.Search = text; await vm.FindCommand.ExecuteAsync(null); return vm; }

    [Fact]
    public async Task Find_by_part_of_a_name_lists_matches()
    {
        var vm = await Find("bea");
        Assert.Equal("Bea Lim", Assert.Single(vm.Hits).Username);
        Assert.False(vm.NoMatch);
    }

    [Fact]
    public async Task Find_by_email_lists_the_member() => Assert.Equal("Carlo Diaz", Assert.Single((await Find("carlo@demo")).Hits).Username);

    [Fact]
    public async Task Find_by_phone_ignores_spaces_and_dashes()
    {
        var hit = Assert.Single((await Find("0917-555-0142")).Hits);
        Assert.Equal(("Bea Lim", "0917 555 0142"), (hit.Username, hit.Phone));
    }

    [Fact]
    public async Task Find_with_no_match_says_so()
    {
        var vm = await Find("zzz-nobody");
        Assert.Empty(vm.Hits);
        Assert.True(vm.NoMatch);
    }

    [Fact]
    public async Task Picking_a_result_sets_the_member_and_marks_it()
    {
        var vm = await Find("bea");
        vm.PickMemberCommand.Execute(vm.Hits.Single());
        Assert.Equal("Bea Lim", vm.Member?.Username);
        Assert.Equal("Bea Lim", vm.OrderWho);
    }
}
