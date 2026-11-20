using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class PayAgainTests
{
    sealed class FakeOpener : IUrlOpener
    {
        public readonly List<string> Opened = new();
        public Task OpenAsync(string url) { Opened.Add(url); return Task.CompletedTask; }
    }

    sealed class SpyMembership(IMembershipService inner, bool checkoutPaid = false) : IMembershipService
    {
        public int PayOnlineCalls;
        public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() => inner.ListPackagesAsync();
        public Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync() => inner.MyMembershipsAsync();
        public Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync() => inner.MyPaymentsAsync();
        public Task<AppResult<IReadOnlyList<AvailableCode>>> AvailableCodesAsync() => inner.AvailableCodesAsync();
        public Task<AppResult<CodeCheck>> CheckCodeAsync(string code, string kind, int itemId) => inner.CheckCodeAsync(code, kind, itemId);
        public Task<AppResult<Access>> AccessAsync() => inner.AccessAsync();
        public Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr) => inner.AvailAsync(packageId, discountCode, method);
        public Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null, string method = PayMethod.Qr) => inner.RenewAsync(membershipId, discountCode, method);
        public Task<AppResult<Payment>> PayAgainAsync(PaymentItem item, string? code = null) => inner.PayAgainAsync(item, code);
        public Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId)
        {
            PayOnlineCalls++;
            return checkoutPaid
                ? inner.PayOnlineAsync(paymentId)
                : Task.FromResult(AppResult<OnlineCheckout>.Success(new OnlineCheckout($"https://xendit.test/{paymentId}", false)));
        }
        public Task<AppResult<bool>> CheckOnlineAsync(int paymentId) => inner.CheckOnlineAsync(paymentId);
        public Task<AppResult<IReadOnlyDictionary<DateOnly, string>>> ClosedDaysAsync() => inner.ClosedDaysAsync();
        public Task<AppResult<GymSettings>> GymAsync() => inner.GymAsync();
    }

    static async Task<(DemoGateway Gw, MyPaymentsViewModel Vm, PaymentItem Expired, FakeOpener Opener, SpyMembership Spy)> SetupAsync(bool checkoutPaid = false)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        Assert.True((await admin.CreateDiscountAsync(new Discount { Code = "SAVE10", Percent = 10, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = true }, [new DiscountItem { Kind = "package" }])).Ok);
        Assert.True((await admin.CreateDiscountAsync(new Discount { Code = "FREE100", Percent = 100, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = true }, [new DiscountItem { Kind = "package" }])).Ok);

        var beas = (await gw.ListAsync<Payment>(p => p.Status == Status.Pending)).Single(p => p.MemberId == "u-bea");
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(beas.Id, false, "Amount does not match")).Ok);

        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var sub = new SubscriptionService(gw);
        var mem = new MembershipService(gw);
        var spy = new SpyMembership(mem, checkoutPaid);
        var opener = new FakeOpener();
        var vm = new MyPaymentsViewModel(spy, sub, opener) { CodeCheckDelay = TimeSpan.Zero };
        await vm.OnAppearingAsync();
        var expired = vm.Rows.Single(r => r.Id == beas.Id);
        return (gw, vm, expired, opener, spy);
    }

    [Fact]
    public async Task Pay_again_with_a_good_code_creates_discounted_payment_and_opens_checkout_once()
    {
        var (gw, vm, expired, opener, _) = await SetupAsync();
        vm.PayAgainCommand.Execute(expired);
        Assert.True(vm.IsPayingAgain);

        vm.PayAgainCode = "SAVE10";
        await vm.CheckPayAgainCodeNowAsync();
        Assert.True(vm.PayAgainCodeWorks);
        Assert.Contains("10% off", vm.PayAgainCodeText);

        await vm.ConfirmPayAgainCommand.ExecuteAsync(null);
        Assert.False(vm.IsPayingAgain);
        Assert.Single(opener.Opened);
        var latest = (await gw.ListAsync<Payment>()).OrderByDescending(p => p.Id).First();
        Assert.Equal(3600m, latest.Amount);
    }

    [Fact]
    public async Task Bad_code_shows_reason_and_Pay_online_still_proceeds_without_it()
    {
        var (gw, vm, expired, opener, _) = await SetupAsync();
        vm.PayAgainCommand.Execute(expired);

        vm.PayAgainCode = "NOPE";
        await vm.CheckPayAgainCodeNowAsync();
        Assert.False(vm.PayAgainCodeWorks);
        Assert.True(vm.HasPayAgainCodeCheck);

        await vm.ConfirmPayAgainCommand.ExecuteAsync(null);
        Assert.False(vm.IsPayingAgain);
        Assert.Single(opener.Opened);
        var latest = (await gw.ListAsync<Payment>()).OrderByDescending(p => p.Id).First();
        Assert.Equal(4000m, latest.Amount);
    }

    [Fact]
    public async Task Cancel_makes_no_payment()
    {
        var (gw, vm, expired, opener, _) = await SetupAsync();
        var before = (await gw.ListAsync<Payment>()).Count;
        vm.PayAgainCommand.Execute(expired);
        vm.PayAgainCode = "SAVE10";
        vm.ClosePayAgainCommand.Execute(null);

        Assert.False(vm.IsPayingAgain);
        Assert.Empty(opener.Opened);
        Assert.Equal(before, (await gw.ListAsync<Payment>()).Count);
    }

    [Fact]
    public async Task Hundred_percent_code_verifies_immediately_with_no_checkout()
    {
        var (gw, vm, expired, opener, _) = await SetupAsync(checkoutPaid: true);
        vm.PayAgainCommand.Execute(expired);
        vm.PayAgainCode = "FREE100";
        await vm.CheckPayAgainCodeNowAsync();
        Assert.True(vm.PayAgainCodeWorks);

        await vm.ConfirmPayAgainCommand.ExecuteAsync(null);
        Assert.False(vm.IsPayingAgain);
        Assert.Empty(opener.Opened);
        var latest = (await gw.ListAsync<Payment>()).OrderByDescending(p => p.Id).First();
        Assert.Equal((0m, Status.Verified), (latest.Amount, latest.Status));
    }
}
