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

    static async Task<CashierViewModel> Find(string text) { var vm = await Open(); vm.Search = text; await vm.SuggestNowAsync(); return vm; }

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

    [Fact]   // owner, 2026-11-16: the results are a table, 10 to a page; every match is listed, no longer cut at 20
    public async Task Every_match_is_listed_for_the_table_to_page()
    {
        var gw = new DemoGateway();
        for (var i = 0; i < 25; i++) await gw.SignUpAsync($"zed{i}@demo.io", "secret12", "Zed", $"Walker{i}", TermsInfo.Version);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(25, (await new AttendanceService(gw).FindMembersAsync("zed")).Value!.Count);
    }

    [Fact]
    public async Task A_row_shows_the_phone_and_the_email_to_tell_two_people_apart()
    {
        var hit = Assert.Single((await Find("bea")).Hits);
        Assert.Equal(("bea@demo.io", "0917 555 0142 · bea@demo.io"), (hit.Email, hit.ContactText));
        Assert.Equal("carlo@demo.io", Assert.Single((await Find("carlo")).Hits).ContactText);   // no phone: just the email
    }

    [Fact]   // owner, 2026-11-16: the Cashier lists only active accounts, with or without a membership
    public async Task Deactivated_accounts_are_left_out_of_the_cashiers_table()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).SetActiveAsync("u-carlo", false)).Ok);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        vm.PickKindCommand.Execute("Amenity");
        vm.Search = "demo"; await vm.SuggestNowAsync();   // every demo account's email
        Assert.DoesNotContain(vm.Hits, h => h.Username == "Carlo Diaz");                 // deactivated
        Assert.Contains(vm.Hits, h => h.Username == "Joy");                              // active, no membership
        Assert.Contains(vm.Hits, h => h.Username == "Alex Tan");                         // active, with one
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
