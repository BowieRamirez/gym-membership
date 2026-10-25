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

    static async Task<DiscountsViewModel> AddCode(DemoGateway gw, string code, int limit)
    {
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new DiscountsViewModel(new AdminService(gw)) { NewCode = code, NewLimit = limit };
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    [Fact]
    public async Task New_codes_need_a_limit_and_old_codes_stay_unlimited()
    {
        var gw = await Admin(); var admin = new AdminService(gw);
        Assert.Equal(AppErrorKind.Validation, (await admin.SaveDiscountAsync(new Discount { Code = "NOLIMIT", Percent = 10, ExpiresAt = DateTime.UtcNow.AddDays(1) })).Error);
        Assert.Equal(AppErrorKind.Validation, (await admin.SaveDiscountAsync(new Discount { Code = "ZERO", Percent = 10, MaxUses = 0, ExpiresAt = DateTime.UtcNow.AddDays(1) })).Error);
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

    [Fact]
    public async Task The_limit_can_change_but_never_below_places_in_use_or_back_to_unlimited()
    {
        var gw = await Admin();
        await new AdminService(gw).CreateUserAsync("Ana", "Cruz", "ana@demo.io", "member", "temp1234");
        var vm = await AddCode(gw, "FIVE", 5);
        await Buy(gw, "carlo@demo.io", "FIVE");
        await Buy(gw, "ana@demo.io", "FIVE", "temp1234");
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Discounts.Single(r => r.Discount.Code == "FIVE");

        vm.ChangeLimitCommand.Execute(row);
        Assert.Equal(5, vm.EditLimit);
        vm.EditLimit = 1;                                   // two places are held
        await vm.SaveLimitCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Same(row, vm.Editing);
        Assert.Equal(5, (await new AdminService(gw).DiscountsAsync()).Value!.Single(r => r.Discount.Code == "FIVE").Discount.MaxUses);

        vm.EditLimit = 2;
        await vm.SaveLimitCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Editing);
        Assert.Equal(2, vm.Discounts.Single(r => r.Discount.Code == "FIVE").Discount.MaxUses);

        var welcome = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10");
        welcome.Discount.MaxUses = null;
        Assert.True((await new AdminService(gw).SaveDiscountAsync(welcome.Discount)).Ok);   // other edits keep an old code unlimited
        vm.ChangeLimitCommand.Execute(welcome);
        vm.EditLimit = 50;
        await vm.SaveLimitCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Discounts.Single(r => r.Discount.Code == "WELCOME10").Discount.MaxUses);
        var limited = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10").Discount;
        limited.MaxUses = null;
        Assert.Equal(AppErrorKind.Validation, (await new AdminService(gw).SaveDiscountAsync(limited)).Error);
    }
}
