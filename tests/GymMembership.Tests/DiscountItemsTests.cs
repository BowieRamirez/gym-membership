using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 2 of the 2026-11-12 plan (G1 to G4, 0060) in demo mode: what a code is for, turning it off, deleting an unused one, and what
// members see on Packages.
public class DiscountItemsTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static async Task<DiscountsViewModel> Discounts(DemoGateway gw)
    {
        var vm = new DiscountsViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        return vm;
    }

    static async Task<int> PackageId(DemoGateway gw, string name) => (await gw.ListAsync<MembershipPackage>()).Single(p => p.Name == name).Id;

    [Fact]
    public async Task A_code_needs_something_to_be_for_and_All_covers_the_single_ticks()
    {
        var vm = await Discounts(await As("admin@demo.io"));
        vm.NewCode = "NOTHING";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal(ErrorText.For(AppErrorKind.Validation, "no_items"), vm.ErrorMessage);
        Assert.DoesNotContain(vm.Discounts, r => r.Discount.Code == "NOTHING");

        vm.PackageChoices.Single(c => c.Name == "Monthly").IsChosen = true;
        vm.AllAmenities = true;
        vm.AmenityChoices.First().IsChosen = true;   // greyed under "All": ignored
        Assert.Equal([("package", (int?)vm.PackageChoices.Single(c => c.Name == "Monthly").Id), ("amenity", null)],
            vm.ChosenItems().Select(i => (i.Kind, i.ItemId)));
    }

    [Fact]
    public async Task A_code_for_one_package_works_only_for_that_package_and_says_so()
    {
        var gw = await As("admin@demo.io");
        var vm = await Discounts(gw);
        (vm.NewCode, vm.NewPercent) = ("MONTHONLY", 15);
        vm.PackageChoices.Single(c => c.Name == "Monthly").IsChosen = true;
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Monthly", vm.Discounts.Single(r => r.Discount.Code == "MONTHONLY").FitsText);

        var annual = await PackageId(gw, "Annual"); var monthly = await PackageId(gw, "Monthly");
        await As("carlo@demo.io", gw);
        var svc = new MembershipService(gw);
        var refused = await svc.AvailAsync(annual, "MONTHONLY");
        Assert.Equal("code_not_for_item", refused.Message);
        Assert.Equal("That code doesn't work for this. Check what the code is for.", ErrorText.For(refused.Error, refused.Message));
        Assert.Equal(1275m, (await svc.AvailAsync(monthly, "MONTHONLY")).Value!.Amount);
    }

    [Fact]
    public async Task At_the_Cashier_a_package_code_is_refused_for_an_amenity()
    {
        var gw = await As("admin@demo.io");
        Assert.True((await new AdminService(gw).CreateDiscountAsync(new Discount { Code = "PKGS", Percent = 10, MaxUses = 5, ExpiresAt = DateTime.UtcNow.AddDays(5) },
            [DiscountItem.Package(null)])).Ok);
        var classes = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Group classes").Id;   // Alex has no pass for it yet
        await As("employee@demo.io", gw); await gw.OpenDrawerAsync();
        var alex = (await gw.ListAsync<Profile>()).Single(p => p.Username == "member@demo.io").Id;
        var result = await new PaymentStaffService(gw).RecordCounterAsync(PayKind.Amenity, PayMethod.Cash, alex, classes, null, null, "PKGS");
        Assert.Equal("code_not_for_item", result.Message);
    }

    [Fact]
    public async Task Members_see_shown_codes_for_packages_with_what_each_is_for_and_a_New_mark()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var expires = DateTime.UtcNow.AddDays(9);
        var monthly = await PackageId(gw, "Monthly"); var annual = await PackageId(gw, "Annual");
        await admin.CreateDiscountAsync(new Discount { Code = "TWOPKG", Percent = 20, MaxUses = 5, ExpiresAt = expires, ShowOnPackages = true },
            [DiscountItem.Package(monthly), DiscountItem.Package(annual)]);
        await admin.CreateDiscountAsync(new Discount { Code = "POOLONLY", Percent = 20, MaxUses = 5, ExpiresAt = expires, ShowOnPackages = true }, [DiscountItem.Amenity(null)]);
        await admin.CreateDiscountAsync(new Discount { Code = "PRIVATE", Percent = 20, MaxUses = 5, ExpiresAt = expires, ShowOnPackages = false }, [DiscountItem.Package(null)]);

        await As("carlo@demo.io", gw);
        var vm = new PackagesViewModel(new MembershipService(gw), new ConfirmationTests.Nav(), new SubscriptionService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(["TWOPKG", "WELCOME10"], vm.Codes.Select(c => c.Code));   // newest first; amenity-only and private codes stay off Packages
        var two = vm.Codes[0];
        Assert.Equal(("For Monthly, Annual", true, $"Last day {expires.AddTicks(-1).ToLocalTime():d MMM yyyy}"), (two.ForText, two.IsNew, two.LastDayText));
        Assert.False(vm.Codes[1].IsNewAt(DateTime.UtcNow.AddDays(7)));
    }

    [Fact]
    public async Task A_code_turned_off_stops_working_and_leaves_the_members_list()
    {
        var gw = await As("admin@demo.io");
        var welcome = (await new AdminService(gw).DiscountsAsync()).Value!.Single(r => r.Discount.Code == "WELCOME10").Discount;
        Assert.True((await new AdminService(gw).SetDiscountPausedAsync(welcome, true)).Ok);
        await As("carlo@demo.io", gw);
        Assert.Empty((await new MembershipService(gw).AvailableCodesAsync()).Value!);
        Assert.Equal("invalid_code", (await new MembershipService(gw).AvailAsync(await PackageId(gw, "Monthly"), "WELCOME10")).Message);
    }

    [Fact]
    public async Task Delete_is_offered_only_for_an_unused_code_and_only_to_a_System_admin()
    {
        var gw = await As("admin@demo.io");
        var vm = await Discounts(gw);
        var welcome = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10");
        await vm.RunActionCommand.ExecuteAsync((welcome, "Delete"));
        Assert.Null(vm.ErrorMessage);
        Assert.DoesNotContain(vm.Discounts, r => r.Discount.Code == "WELCOME10");
        Assert.Empty(await gw.ListAsync<Discount>(d => d.Code == "WELCOME10"));

        Assert.True((await new AdminService(gw).CreateDiscountAsync(new Discount { Code = "USEDONE", Percent = 10, MaxUses = 5, ExpiresAt = DateTime.UtcNow.AddDays(5) },
            DiscountItem.Everything)).Ok);
        var monthly = await PackageId(gw, "Monthly");
        await As("carlo@demo.io", gw);
        Assert.True((await new MembershipService(gw).AvailAsync(monthly, "USEDONE")).Ok);
        await As("admin@demo.io", gw);
        await vm.LoadCommand.ExecuteAsync(null);
        var used = vm.Discounts.Single(r => r.Discount.Code == "USEDONE");
        Assert.Equal(["Turn off", "Archive"], vm.ActionsFor(used));
        Assert.Equal("discount_used", (await new AdminService(gw).DeleteDiscountAsync(used.Discount)).Message);

        var subGw = await As("subadmin@demo.io");   // a fresh demo: its WELCOME10 is unused
        var sub = await Discounts(subGw);           // an Admin turns codes off and on, nothing more (D15)
        var unused = sub.Discounts.Single(r => r.Discount.Code == "WELCOME10");
        Assert.Equal(["Turn off"], sub.ActionsFor(unused));
        Assert.Equal(AppErrorKind.Unauthorized, (await new AdminService(subGw).DeleteDiscountAsync(unused.Discount)).Error);
    }

    [Fact]
    public void What_a_code_is_for_reads_the_same_for_staff_and_members()
    {
        var packages = new Dictionary<int, string> { [1] = "Monthly", [2] = "Annual" };
        var amenities = new Dictionary<int, string> { [7] = "Pool" };
        Assert.Equal("Monthly, Annual · Pool", DiscountFits.Text(false, [1, 2], false, [7], packages, amenities));
        Assert.Equal("All packages · All amenities", DiscountFits.Text(true, [], true, [], packages, amenities));
        Assert.Equal("Nothing on sale now", DiscountFits.Text(false, [9], false, [], packages, amenities));   // only an archived package
        Assert.Equal("For all packages · Pool", new AvailableCode { FitsText = DiscountFits.Text(true, [], false, [7], packages, amenities) }.ForText);
    }
}
