using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CoachAccessTests
{
    sealed class NoopNav : IAppNavigator
    {
        public void GoTo(AppRole role, bool memberActive) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMemberHome() { }
        public void GoToStaffHome() { }
        public void GoToCoachHome() { }
        public void GoToPackages() { }
        public void GoToMembership() { }
        public void GoToPayments() { }
        public void GoToMessages() { }
        public void GoToRequests() { }
        public void GoToChatWith(string peerId, string peerName) { }
        public void ApplyTheme(string theme) { }
    }

    sealed class FakeOpener : IUrlOpener
    {
        public Task OpenAsync(string url) => Task.CompletedTask;
    }

    static async Task<(DemoGateway Gw, MembershipService Mem, SubscriptionService Sub, SessionService Ses, MembershipPackage PlainMonthly)> SetupAsync()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        Assert.True((await admin.CreateDiscountAsync(
            new Discount { Code = "COACH50", Percent = 50, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = true },
            [new DiscountItem { Kind = "coach" }])).Ok);
        Assert.True((await admin.CreateDiscountAsync(
            new Discount { Code = "COACH100", Percent = 100, ExpiresAt = DateTime.UtcNow.AddDays(7), MaxUses = 5, ShowOnPackages = true },
            [new DiscountItem { Kind = "coach" }])).Ok);

        var mem = new MembershipService(gw);
        var sub = new SubscriptionService(gw);
        var ses = new SessionService(gw);
        var monthly = (await mem.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        return (gw, mem, sub, ses, monthly);
    }

    [Fact]
    public async Task Monthly_scaled_price_buy_online_activates_term_and_allows_free_booking()
    {
        var (gw, mem, sub, ses, monthly) = await SetupAsync();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);

        // Alex's seeded Monthly term has 5 days left (scaled price = ceil(250 * 5 / 30) = 42)
        var offerRes = await mem.CoachAccessOfferAsync();
        Assert.True(offerRes.Ok);
        var offer = offerRes.Value!;
        Assert.True(offer.Offered);
        Assert.Equal(monthly.Id, offer.PackageId);
        Assert.Equal(Math.Ceiling(monthly.CoachAddonPrice!.Value * offer.DaysLeft!.Value / monthly.DurationDays), offer.Price);
        var expectedPrice = offer.Price!.Value;

        // Cannot book a coach before buying add-on
        var manilaToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var futureDay = manilaToday.AddDays(2);
        var startUtc = new DateTimeOffset(futureDay.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(8)).UtcDateTime;
        gw.OwnerAddCoachShift("u-coach1", startUtc, startUtc.AddHours(4));   // Fix 3 (0151): the schedule writes shifts
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var beforeBook = await ses.BookCoachAsync("u-coach1", startUtc, 1);
        Assert.False(beforeBook.Ok);
        Assert.Equal("coach_not_offered", beforeBook.Message);

        // PackagesViewModel loads offer and buys it, then PayOnline confirms it in demo mode
        var vm = new PackagesViewModel(mem, new NoopNav(), sub, new FakeOpener(), ses);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.HasCoachOffer);
        Assert.Equal($"₱{expectedPrice:N0}", vm.CoachOfferPriceText);

        vm.OpenBuyCoachAccessCommand.Execute(null);
        Assert.True(vm.IsBuyingCoachAccess);
        await vm.PayCoachAccessCommand.ExecuteAsync(null);
        Assert.False(vm.IsBuyingCoachAccess);
        Assert.NotNull(vm.PayNow);
        Assert.Equal(PayKind.CoachAddon, vm.PayNow!.Kind);
        Assert.Equal(expectedPrice, vm.PayNow.Amount);

        // Demo PayOnline confirms at once
        var payRes = await mem.PayOnlineAsync(vm.PayNow.Id);
        Assert.True(payRes.Ok);
        Assert.True(payRes.Value!.Paid);

        var termAfter = (await gw.ListAsync<MembershipTerm>(t => t.MemberId == "u-alex" && t.CancelledAt == null)).First();
        Assert.NotNull(termAfter.CoachAccessAt);
        var afterOffer = await mem.CoachAccessOfferAsync();
        Assert.False(afterOffer.Value!.Offered);
        Assert.Equal("already_bought", afterOffer.Value.Reason);

        // TermCard shows CoachAccessText
        var terms = (await sub.TermsAsync()).Value!;
        var activeCard = terms.First(t => t.State == Status.Active);
        Assert.NotNull(activeCard.CoachAccessText);
        Assert.StartsWith("Coach access until ", activeCard.CoachAccessText);

        // Now booking a coach succeeds as free (cancel Alex's seeded upcoming session first)
        foreach (var s in await gw.ListAsync<TrainingSession>(x => x.MemberId == "u-alex" && x.Status == Status.Scheduled))
            await ses.CancelSessionAsync(s.Id, "Reset");
        var bookRes = await ses.BookCoachAsync("u-coach1", startUtc, 1);
        Assert.True(bookRes.Ok);
        Assert.True(bookRes.Value!.IsFree);
    }

    [Fact]
    public async Task Discount_code_applies_to_scaled_price_and_100_percent_activates_immediately()
    {
        var (gw, mem, _, _, monthly) = await SetupAsync();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);

        var offer = (await mem.CoachAccessOfferAsync()).Value!;
        Assert.True(offer.Offered);
        var expectedHalf = Math.Round(offer.Price!.Value * 0.5m, 2, MidpointRounding.AwayFromZero);

        var chk = await mem.CheckCodeAsync("COACH50", "coach", monthly.Id);
        Assert.True(chk.Ok);
        Assert.True(chk.Value!.Works);
        Assert.Equal(expectedHalf, chk.Value.YouPay);

        var freePay = await mem.BuyCoachAccessAsync("COACH100");
        Assert.True(freePay.Ok);
        Assert.Equal(Status.Verified, freePay.Value!.Status);
        Assert.Equal(0m, freePay.Value.Amount);

        var termAfter = (await gw.ListAsync<MembershipTerm>(t => t.MemberId == "u-alex" && t.CancelledAt == null)).First();
        Assert.NotNull(termAfter.CoachAccessAt);
    }

    [Fact]
    public async Task Day_pass_offers_full_price_only_when_free_coach_hour_exists_today()
    {
        var (gw, mem, sub, _, _) = await SetupAsync();

        // Reject Bea's pending payment so Bea has no active term, then Bea buys a Day pass and pays online
        var beaPay = (await gw.ListAsync<Payment>(p => p.MemberId == "u-bea" && p.Status == Status.Pending)).First();
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(beaPay.Id, false, "Reset for test")).Ok);

        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var dayPass = (await mem.ListPackagesAsync()).Value!.First(p => p.IsDayPass);
        var dpPay = await sub.ExtendAsync(dayPass.Id, null);
        Assert.True(dpPay.Ok);
        var paid = await mem.PayOnlineAsync(dpPay.Value!.Id);
        Assert.True(paid.Ok && paid.Value!.Paid);

        // Cancel any shifts today as coach
        var manilaToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        foreach (var s in (await gw.ListAsync<ShiftPost>(s => DateOnly.FromDateTime(s.Start.ToUniversalTime().AddHours(8).Date) == manilaToday)))
        {
            s.Status = Status.Cancelled;
            await gw.UpdateAsync(s);
        }
        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var noCoachOffer = await mem.CoachAccessOfferAsync();
        Assert.False(noCoachOffer.Value!.Offered);
        Assert.Equal("no_free_coaches", noCoachOffer.Value.Reason);

        // Open gym hours via admin and add a future shift today (if at least 1 full hour remains today in Manila)
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var settings = (await gw.ListAsync<GymSettings>()).First();
        settings.OpensAt = TimeSpan.Zero;
        settings.ClosesAt = TimeSpan.FromHours(23);
        await gw.UpdateAsync(settings);
        var nextHourUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        if (DateOnly.FromDateTime(nextHourUtc.AddHours(8)) == manilaToday && nextHourUtc.AddHours(8).Hour < 23)
        {
            gw.OwnerAddCoachShift("u-coach1", nextHourUtc, nextHourUtc.AddHours(1));
            await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
            var dayOffer = await mem.CoachAccessOfferAsync();
            Assert.True(dayOffer.Value!.Offered);
            Assert.True(dayOffer.Value.IsDayPass);
            Assert.Equal(dayPass.CoachAddonPrice, dayOffer.Value.Price);
        }
    }
}
