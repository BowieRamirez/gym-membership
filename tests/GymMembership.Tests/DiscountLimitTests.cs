using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 3 (D4/D5): pending, verified and voided payments hold a place on a code; a used-up code is archived for good
public class DiscountLimitTests
{
    static async Task<DemoGateway> Admin()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        return gw;
    }

    static async Task<AppResult<Payment>> Buy(DemoGateway gw, string email, string code, string password = DemoGateway.DemoPassword)
    {
        await gw.SignInAsync(email, password);
        var svc = new MembershipService(gw);
        var monthly = (await svc.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        return await svc.AvailAsync(monthly.Id, code);
    }

    /// <summary>A code for every package, hidden from members unless shown (G4).</summary>
    static async Task<DiscountsViewModel> AddCode(DemoGateway gw, string code, int limit, bool shown = false, int percent = 10)
    {
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new DiscountsViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        (vm.NewCode, vm.NewLimit, vm.NewShowToMembers, vm.NewPercent, vm.AllPackages) = (code, limit, shown, percent, true);
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    [Fact]
    public async Task New_codes_need_a_limit_and_old_codes_stay_unlimited()
    {
        var gw = await Admin(); var admin = new AdminService(gw);
        Assert.Equal(AppErrorKind.Validation, (await admin.CreateDiscountAsync(new Discount { Code = "NOLIMIT", Percent = 10, ExpiresAt = DateTime.UtcNow.AddDays(1) }, DiscountItem.Everything)).Error);
        Assert.Equal(AppErrorKind.Validation, (await admin.CreateDiscountAsync(new Discount { Code = "ZERO", Percent = 10, MaxUses = 0, ExpiresAt = DateTime.UtcNow.AddDays(1) }, DiscountItem.Everything)).Error);
        var welcome = (await admin.DiscountsAsync()).Value!.Single(r => r.Discount.Code == "WELCOME10");
        Assert.Null(welcome.Discount.MaxUses);
        Assert.Equal("0 used, no limit", welcome.Usage);
    }

    [Fact]
    public async Task A_full_code_turns_people_away_while_pending_and_archives_for_good_once_verified()
    {
        var gw = await Admin();
        await new AdminService(gw).CreateUserAsync("Ana", "Cruz", "ana@demo.io", "member", "temp1234");
        await AddCode(gw, "ONCE", 1);

        var held = (await Buy(gw, "carlo@demo.io", "ONCE")).Value!;
        Assert.Equal("code_limit_reached", (await Buy(gw, "ana@demo.io", "ONCE", "temp1234")).Message);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new DiscountsViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("0 of 1 used, 1 pending", vm.Discounts.Single(r => r.Discount.Code == "ONCE").Usage);

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(held.Id, true)).Ok);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Discounts, r => r.Discount.Code == "ONCE");
        var archive = new ArchiveService(gw);
        var row = (await archive.ListAsync(new("code"), 0)).Value!.Rows.Single(r => r.Name == "ONCE");
        Assert.Equal("Limit reached", row.Reason);
        Assert.False(row.CanRestore);
        Assert.Equal("record_not_restorable", (await archive.RestoreAsync(row)).Message);
    }

    [Fact]
    public async Task A_rejected_payment_gives_its_place_back()
    {
        var gw = await Admin();
        await new AdminService(gw).CreateUserAsync("Ana", "Cruz", "ana@demo.io", "member", "temp1234");
        await AddCode(gw, "ONCE", 1);
        var held = (await Buy(gw, "carlo@demo.io", "ONCE")).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(held.Id, false, "Wrong reference")).Ok);
        Assert.True((await Buy(gw, "ana@demo.io", "ONCE", "temp1234")).Ok);
    }

    [Fact]
    public async Task A_voided_use_stays_counted_and_the_member_cannot_reuse_the_code()
    {
        var gw = await Admin();
        await AddCode(gw, "THREE", 3);
        var pay = (await Buy(gw, "carlo@demo.io", "THREE")).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await new PaymentStaffService(gw).VerifyAsync(pay.Id, true);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VoidAsync(pay.Id, "Wrong payment")).Ok);

        Assert.Equal(AppErrorKind.Validation, (await Buy(gw, "carlo@demo.io", "THREE")).Error);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.Equal("1 of 3 used", (await new AdminService(gw).DiscountsAsync()).Value!.Single(r => r.Discount.Code == "THREE").Usage);
    }

    // F7's edit rules (a used code can't be edited, an unused one can) gave way to G2 (0060): no code can be edited at all.
    // See CrudTests.A_code_cannot_be_edited_after_it_is_made.

    /// <summary>The demo's WELCOME10 is shown on Packages; these tests turn it off (G1) so they see only their own codes.</summary>
    static async Task HideWelcome(DemoGateway gw)
    {
        var admin = new AdminService(gw);
        var welcome = (await admin.DiscountsAsync()).Value!.Single(r => r.Discount.Code == "WELCOME10").Discount;
        Assert.True(welcome.ShowOnPackages);
        Assert.True((await admin.SetDiscountPausedAsync(welcome, true)).Ok);
    }

    static async Task<PackagesViewModel> Packages(DemoGateway gw, string email)
    {
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new PackagesViewModel(new MembershipService(gw), new ConfirmationTests.Nav(),new SubscriptionService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    [Fact]
    public async Task F5_Packages_lists_only_codes_switched_on_that_this_member_can_still_use()
    {
        var gw = await Admin();
        await new AdminService(gw).CreateUserAsync("Ana", "Cruz", "ana@demo.io", "member", "temp1234");
        await HideWelcome(gw);
        Assert.True(new DiscountsViewModel(new AdminService(gw)).NewShowToMembers);   // G4: shown by default; off keeps a code private
        await AddCode(gw, "shown15", 5, shown: true, percent: 15);
        await AddCode(gw, "SECRET", 5);
        await AddCode(gw, "FULL1", 1, shown: true);
        Assert.True((await Buy(gw, "ana@demo.io", "FULL1", "temp1234")).Ok);   // someone else took the only place

        var vm = await Packages(gw, "carlo@demo.io");
        var code = Assert.Single(vm.Codes);
        Assert.Equal("SHOWN15", code.Code);
        Assert.Equal(($"Last day {DateTime.Today.AddDays(30):d MMM yyyy}", "For all packages", 15, true), (code.LastDayText, code.ForText, code.Percent, code.IsNew));
        Assert.True(vm.HasCodes);
        vm.UseCodeCommand.Execute(code);                         // fills the code box on the same page
        Assert.Equal("SHOWN15", vm.DiscountCode);

        Assert.True((await Buy(gw, "carlo@demo.io", "SHOWN15")).Ok);   // once per member: gone from their list
        vm = await Packages(gw, "carlo@demo.io");
        Assert.Empty(vm.Codes);
        Assert.False(vm.HasCodes);
    }
}
