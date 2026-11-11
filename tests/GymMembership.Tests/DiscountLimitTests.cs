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
    public async Task F7_a_used_code_cannot_be_edited_only_turned_off()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = await Admin();
        var vm = await AddCode(gw, "FIVE", 5);
        Assert.True(vm.Discounts.Single(r => r.Discount.Code == "FIVE").CanEdit);   // unused: still editable
        await Buy(gw, "carlo@demo.io", "FIVE");             // one pending payment is enough
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Discounts.Single(r => r.Discount.Code == "FIVE");
        Assert.False(row.CanEdit);

        vm.EditCodeCommand.Execute(row);
        Assert.Null(vm.Editing);

        var admin = new AdminService(gw);
        var copy = new Discount { Id = row.Discount.Id, Code = row.Discount.Code, Percent = row.Discount.Percent, StartsAt = row.Discount.StartsAt,
            ExpiresAt = row.Discount.ExpiresAt, MaxUses = 9, IsActive = true };
        var refused = await admin.SaveDiscountAsync(copy);       // the demo refuses it as the database does
        Assert.Equal("discount_locked", refused.Message);
        Assert.Equal("This code has been used, so it can't be changed. Turn it off and add a new code if you need different terms.",
            ErrorText.For(refused.Error, refused.Message));
        foreach (var change in new Action<Discount>[] { d => d.ExpiresAt = d.ExpiresAt.AddDays(5), d => d.ShowOnPackages = true, d => d.StartsAt = d.StartsAt.AddHours(-1) })
        {
            var d = new Discount { Id = row.Discount.Id, Code = row.Discount.Code, Percent = row.Discount.Percent, StartsAt = row.Discount.StartsAt,
                ExpiresAt = row.Discount.ExpiresAt, MaxUses = row.Discount.MaxUses, IsActive = true };
            change(d);
            Assert.Equal("discount_locked", (await admin.SaveDiscountAsync(d)).Message);
        }

        await vm.ToggleCommand.ExecuteAsync(row);           // turning it off still works
        Assert.Null(vm.ErrorMessage);
        Assert.False((await gw.ListAsync<Discount>(d => d.Code == "FIVE")).Single().IsActive);
    }

    [Fact]
    public async Task An_unused_code_can_change_its_limit_but_never_back_to_unlimited()
    {
        var gw = await Admin();
        var vm = await AddCode(gw, "FIVE", 5);
        var row = vm.Discounts.Single(r => r.Discount.Code == "FIVE");
        vm.EditCodeCommand.Execute(row);
        Assert.Equal(5, vm.EditLimit);
        vm.EditLimit = 2;
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Editing);
        Assert.Equal(2, vm.Discounts.Single(r => r.Discount.Code == "FIVE").Discount.MaxUses);

        var welcome = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10");
        welcome.Discount.MaxUses = null;
        Assert.True((await new AdminService(gw).SaveDiscountAsync(welcome.Discount)).Ok);   // other edits keep an old code unlimited
        vm.EditCodeCommand.Execute(welcome);
        vm.EditLimit = 50;
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Discounts.Single(r => r.Discount.Code == "WELCOME10").Discount.MaxUses);
        var limited = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10").Discount;
        limited.MaxUses = null;
        Assert.Equal(AppErrorKind.Validation, (await new AdminService(gw).SaveDiscountAsync(limited)).Error);
    }

    /// <summary>The demo's WELCOME10 is shown on Packages; these tests start with it hidden so they see only their own codes.</summary>
    static async Task HideWelcome(DemoGateway gw)
    {
        var admin = new AdminService(gw);
        var welcome = (await admin.DiscountsAsync()).Value!.Single(r => r.Discount.Code == "WELCOME10").Discount;
        Assert.True(welcome.ShowOnPackages);
        welcome.ShowOnPackages = false;
        Assert.True((await admin.SaveDiscountAsync(welcome)).Ok);
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
        Assert.False(new DiscountsViewModel(new AdminService(gw)).NewShowOnPackages);   // off by default: codes stay private
        var admin = new DiscountsViewModel(new AdminService(gw)) { NewCode = "shown15", NewPercent = 15, NewLimit = 5, NewShowOnPackages = true };
        await admin.AddCommand.ExecuteAsync(null);
        Assert.Null(admin.ErrorMessage);
        await AddCode(gw, "SECRET", 5);
        var full = new DiscountsViewModel(new AdminService(gw)) { NewCode = "FULL1", NewLimit = 1, NewShowOnPackages = true };
        await full.AddCommand.ExecuteAsync(null);
        Assert.True((await Buy(gw, "ana@demo.io", "FULL1", "temp1234")).Ok);   // someone else took the only place

        var vm = await Packages(gw, "carlo@demo.io");
        var code = Assert.Single(vm.Codes);
        Assert.Equal("SHOWN15", code.Code);
        Assert.Equal($"15% off. Until {DateTime.Today.AddDays(30):d MMM yyyy}", code.Summary);
        Assert.True(vm.HasCodes);
        vm.UseCodeCommand.Execute(code);                         // fills the code box on the same page
        Assert.Equal("SHOWN15", vm.DiscountCode);

        Assert.True((await Buy(gw, "carlo@demo.io", "SHOWN15")).Ok);   // once per member: gone from their list
        vm = await Packages(gw, "carlo@demo.io");
        Assert.Empty(vm.Codes);
        Assert.False(vm.HasCodes);
    }

    [Fact]
    public async Task F5_the_switch_can_be_changed_while_a_code_is_unused()
    {
        var gw = await Admin();
        await HideWelcome(gw);
        var vm = await AddCode(gw, "LATER", 5);
        var row = vm.Discounts.Single(r => r.Discount.Code == "LATER");
        Assert.False(row.Discount.ShowOnPackages);
        vm.EditCodeCommand.Execute(row);
        Assert.False(vm.EditShowOnPackages);
        vm.EditShowOnPackages = true;
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True((await gw.ListAsync<Discount>(d => d.Code == "LATER")).Single().ShowOnPackages);
        Assert.Equal("LATER", Assert.Single((await Packages(gw, "carlo@demo.io")).Codes).Code);
    }
}
