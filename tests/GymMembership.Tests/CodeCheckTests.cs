using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q12, Q13 (0107): discount codes are checked as they're typed, on both Packages and Book an amenity.</summary>
public class CodeCheckTests
{
    static async Task<(DemoGateway Gw, PackagesViewModel Vm, MembershipPackage Monthly)> OpenPackages(string email = "member@demo.io")
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var monthly = (await gw.ListAsync<MembershipPackage>()).Single(p => p.Name == "Monthly" && p.PublishedAt is not null);
        var pool = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Pool" && a.PublishedAt is not null);
        Assert.True((await admin.CreateDiscountAsync(new Discount { Code = "SAVE10", Percent = 10, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = true },
            [new DiscountItem { Kind = "package" }])).Ok);
        Assert.True((await admin.CreateDiscountAsync(new Discount { Code = "POOL20", Percent = 20, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = false },
            [new DiscountItem { Kind = "amenity", ItemId = pool.Id }])).Ok);

        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new PackagesViewModel(new MembershipService(gw), new ConfirmationTests.Nav(), new SubscriptionService(gw))
        {
            CodeCheckDelay = TimeSpan.Zero
        };
        await vm.OnAppearingAsync();
        vm.OpenBuyCommand.Execute(monthly);
        return (gw, vm, monthly);
    }

    [Fact]
    public async Task Typing_a_good_code_shows_percent_and_price_and_wrong_code_shows_reason()
    {
        var (_, vm, _) = await OpenPackages();
        vm.DiscountCode = "save10";
        await vm.CheckCodeNowAsync();
        Assert.True(vm.CodeWorks);
        Assert.True(vm.HasCodeCheck);
        Assert.Equal("Code works: 10% off. You pay ₱1,350.00.", vm.CodeCheckText);

        vm.DiscountCode = "NOPE";
        await vm.CheckCodeNowAsync();
        Assert.False(vm.CodeWorks);
        Assert.Equal("There's no code with that name.", vm.CodeCheckText);

        vm.DiscountCode = "POOL20";
        await vm.CheckCodeNowAsync();
        Assert.False(vm.CodeWorks);
        Assert.Equal("That code doesn't work for this package.", vm.CodeCheckText);
    }

    [Fact]
    public async Task Clearing_the_box_or_closing_the_popup_clears_the_line()
    {
        var (_, vm, _) = await OpenPackages();
        vm.DiscountCode = "SAVE10";
        await vm.CheckCodeNowAsync();
        Assert.True(vm.HasCodeCheck);

        vm.DiscountCode = "";
        await vm.CheckCodeNowAsync();
        Assert.False(vm.HasCodeCheck);
        Assert.Null(vm.CodeCheckText);

        vm.DiscountCode = "SAVE10";
        await vm.CheckCodeNowAsync();
        Assert.True(vm.HasCodeCheck);
        vm.CloseBuyCommand.Execute(null);
        Assert.False(vm.HasCodeCheck);
        Assert.Null(vm.CodeCheckText);
    }

    sealed class SlowCheckService(IMembershipService inner, Func<string, Task<AppResult<CodeCheck>>> check) : IMembershipService
    {
        public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() => inner.ListPackagesAsync();
        public Task<AppResult<IReadOnlyList<AvailableCode>>> AvailableCodesAsync() => inner.AvailableCodesAsync();
        public Task<AppResult<CodeCheck>> CheckCodeAsync(string code, string kind, int itemId) => check(code);
        public Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync() => inner.MyMembershipsAsync();
        public Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync() => inner.MyPaymentsAsync();
        public Task<AppResult<Access>> AccessAsync() => inner.AccessAsync();
        public Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr) => inner.AvailAsync(packageId, discountCode, method);
        public Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null, string method = PayMethod.Qr) => inner.RenewAsync(membershipId, discountCode, method);
        public Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId) => inner.PayOnlineAsync(paymentId);
        public Task<AppResult<bool>> CheckOnlineAsync(int paymentId) => inner.CheckOnlineAsync(paymentId);
        public Task<AppResult<Payment>> PayAgainAsync(PaymentItem item, string? code = null) => inner.PayAgainAsync(item, code);
        public Task<AppResult<IReadOnlyDictionary<DateOnly, string>>> ClosedDaysAsync() => inner.ClosedDaysAsync();
        public Task<AppResult<GymSettings>> GymAsync() => inner.GymAsync();
    }

    [Fact]
    public async Task A_slow_answer_for_an_old_code_never_overwrites_the_newer_one()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var real = new MembershipService(gw);
        var monthly = (await real.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        var slowFirst = new TaskCompletionSource<AppResult<CodeCheck>>();
        var svc = new SlowCheckService(real, code => code == "FIRST"
            ? slowFirst.Task
            : Task.FromResult(AppResult<CodeCheck>.Success(new CodeCheck { Percent = 10, AmountOff = 150, YouPay = 1350 })));

        var vm = new PackagesViewModel(svc, new ConfirmationTests.Nav(), new SubscriptionService(gw)) { CodeCheckDelay = TimeSpan.Zero };
        vm.OpenBuyCommand.Execute(monthly);

        vm.DiscountCode = "FIRST";
        var first = vm.CheckCodeNowAsync();
        vm.DiscountCode = "SECOND";
        await vm.CheckCodeNowAsync();
        Assert.Equal("Code works: 10% off. You pay ₱1,350.00.", vm.CodeCheckText);

        // old slow response arrives late -> must be ignored
        slowFirst.SetResult(AppResult<CodeCheck>.Success(new CodeCheck { Reason = "invalid_code" }));
        await first;
        Assert.True(vm.CodeWorks);
        Assert.Equal("Code works: 10% off. You pay ₱1,350.00.", vm.CodeCheckText);
    }

    [Fact]
    public async Task Book_an_amenity_checks_unlisted_codes_and_updates_the_price_split()
    {
        var (gw, _, _) = await OpenPackages();
        var res = new ReservationService(gw);
        var am = new AmenityService(gw);
        var mem = new MembershipService(gw);
        var vm = new BookAmenityViewModel(res, am, new ConfirmationTests.Nav(), mem) { CodeCheckDelay = TimeSpan.Zero };
        await vm.OnAppearingAsync();
        var paid = vm.Cards.First(c => !c.HasPass);
        // add an unlisted code specifically for this amenity
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).CreateDiscountAsync(new Discount { Code = "AMEN20", Percent = 20, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = false },
            [new DiscountItem { Kind = "amenity", ItemId = paid.Amenity.Id }])).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        await vm.OpenCommand.ExecuteAsync(paid);

        // AMEN20 is hidden (show_on_packages = false), so it's not in vm.Codes, yet live check finds it and Split uses 20%
        Assert.DoesNotContain(vm.Codes, c => c.Code == "AMEN20");
        var before = vm.Split.Due;
        vm.Code = "AMEN20";
        await vm.CheckCodeNowAsync();
        Assert.True(vm.CodeWorks);
        Assert.Equal(Math.Round(before * 0.80m, 2), vm.Split.Due);
        Assert.Equal($"Code works: 20% off. You pay ₱{vm.Split.Due:N2}.", vm.CodeCheckText);

        vm.Code = "BADCODE";
        await vm.CheckCodeNowAsync();
        Assert.False(vm.CodeWorks);
        Assert.Equal("There's no code with that name.", vm.CodeCheckText);

        vm.CloseCommand.Execute(null);
        Assert.False(vm.HasCodeCheck);
    }

    [Fact]
    public async Task Twenty_first_check_in_ten_minutes_shows_too_many_checks()
    {
        var (_, vm, _) = await OpenPackages();
        for (int i = 0; i < 20; i++)
        {
            vm.DiscountCode = $"TRY{i}";
            await vm.CheckCodeNowAsync();
            Assert.Equal("There's no code with that name.", vm.CodeCheckText);
        }
        vm.DiscountCode = "SAVE10";
        await vm.CheckCodeNowAsync();
        Assert.False(vm.CodeWorks);
        Assert.Equal("Too many checks. Wait a few minutes.", vm.CodeCheckText);
    }
}
