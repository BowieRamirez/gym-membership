using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Xendit plan (2026-11-16): members pay online through xendit-checkout; demo mode pays at once.</summary>
public class OnlinePaymentTests
{
    static async Task<(DemoGateway Gw, MembershipService Member, Payment Pay)> NewMember(DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        var quarterly = (await member.ListPackagesAsync()).Value!.First(p => p.Name == "Quarterly");
        return (gw, member, (await member.AvailAsync(quarterly.Id)).Value!);
    }

    [Fact]
    public async Task Pay_online_verifies_and_starts_the_membership()
    {
        var (_, member, pay) = await NewMember();
        Assert.True((await member.MyPaymentsAsync()).Value!.Single().NeedsPayment);
        var paid = await member.PayOnlineAsync(pay.Id);
        Assert.True(paid.Ok, paid.Message);
        Assert.True(paid.Value!.Paid);
        var item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.Equal(Status.Verified, item.Status);
        Assert.Matches(@"^OR-\d{4}-\d{6}$", item.ReceiptNo);
        Assert.Equal("active", (await member.MyMembershipsAsync()).Value!.Single().Status);
    }

    [Fact]
    public async Task Pay_online_on_a_paid_payment_just_says_paid()
    {
        var (_, member, pay) = await NewMember();
        await member.PayOnlineAsync(pay.Id);
        var receipt = (await member.MyPaymentsAsync()).Value!.Single().ReceiptNo;
        Assert.True((await member.PayOnlineAsync(pay.Id)).Value!.Paid);
        Assert.Equal(receipt, (await member.MyPaymentsAsync()).Value!.Single().ReceiptNo);
    }

    [Fact]
    public async Task Expired_payment_cannot_be_paid_online()
    {
        var clock = new SubscriptionTests.Clock();
        var (_, member, pay) = await NewMember(new DemoGateway(clock: clock));
        clock.Now = clock.Now.AddHours(49);
        var r = await member.PayOnlineAsync(pay.Id);
        Assert.False(r.Ok);
        Assert.Contains("payment_expired", r.Message);
    }

    [Fact]
    public async Task Returning_checks_the_link_and_activates()
    {
        var (_, member, pay) = await NewMember();
        Assert.False((await member.CheckOnlineAsync(pay.Id)).Value);
        await member.PayOnlineAsync(pay.Id);
        Assert.True((await member.CheckOnlineAsync(pay.Id)).Value);
    }

    [Fact]
    public async Task Someone_elses_payment_is_not_found()
    {
        var (gw, _, pay) = await NewMember();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var r = await new MembershipService(gw).PayOnlineAsync(pay.Id);
        Assert.False(r.Ok);
        Assert.Contains("not_found", r.Message);
    }

    [Fact]
    public async Task Members_can_no_longer_send_proofs()
    {
        var (gw, _, pay) = await NewMember();
        await Assert.ThrowsAnyAsync<Exception>(() => gw.RpcAsync("submit_payment_proof",
            new() { ["p_payment_id"] = pay.Id, ["p_reference_no"] = "GC-1", ["p_proof_path"] = "x.png" }));
    }

    [Fact]
    public void PayMethod_Qr_reads_Online() => Assert.Equal("Online", PayMethod.Name(PayMethod.Qr));

    [Fact]
    public void Staff_row_for_an_online_payment_has_no_approve()
    {
        var online = new PaymentRow(new Payment { Method = PayMethod.Qr }, "Ana Reyes", "Quarterly", HasProof: false);
        Assert.Equal(("Paying online", "Waiting for the member to pay online", false), (online.ProofText, online.WaitText, online.CanApprove));
        var oldProof = new PaymentRow(new Payment { Method = PayMethod.Qr, ReferenceNo = "GC-1" }, "Bea", "Monthly", HasProof: true);
        Assert.Equal(("GC-1", true), (oldProof.ProofText, oldProof.CanApprove));
        var desk = new PaymentRow(new Payment { Method = PayMethod.Cash }, "Carlo", "Pool", HasProof: false, Booking: "Mon 10:00");
        Assert.Equal(("Pays at the desk", true), (desk.ProofText, desk.CanApprove));
    }

    // ---------- the screens ----------
    sealed class Opener : IUrlOpener { public string? Opened; public Task OpenAsync(string url) { Opened = url; return Task.CompletedTask; } }

    /// <summary>The real service, but Xendit's answers are scripted: a link that isn't paid yet, and a check that says paid.</summary>
    sealed class ScriptedXendit(IMembershipService real, bool paidOnCheck = false) : IMembershipService
    {
        public int Checks;
        public Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId) => Task.FromResult(AppResult<OnlineCheckout>.Success(new("https://checkout.test/ps-1", false)));
        public Task<AppResult<bool>> CheckOnlineAsync(int paymentId) { Checks++; return paidOnCheck ? real.PayOnlineAsync(paymentId).ContinueWith(_ => AppResult<bool>.Success(true)) : Task.FromResult(AppResult<bool>.Success(false)); }
        public async Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync()
        {
            var r = await real.MyPaymentsAsync();
            return AppResult<IReadOnlyList<PaymentItem>>.Success(r.Value!.Select(p => p with { HasCheckout = p.Status == Status.Pending }).ToList());
        }
        public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() => real.ListPackagesAsync();
        public Task<AppResult<IReadOnlyList<AvailableCode>>> AvailableCodesAsync() => real.AvailableCodesAsync();
        public Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync() => real.MyMembershipsAsync();
        public Task<AppResult<Access>> AccessAsync() => real.AccessAsync();
        public Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null) => real.AvailAsync(packageId, discountCode);
        public Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null) => real.RenewAsync(membershipId, discountCode);
        public Task<AppResult<Payment>> PayAgainAsync(PaymentItem item) => real.PayAgainAsync(item);
        public Task<AppResult<GymSettings>> GymAsync() => real.GymAsync();
        public Task<AppResult<IReadOnlyDictionary<DateOnly, string>>> ClosedDaysAsync() => real.ClosedDaysAsync();
        public Task<AppResult<string?>> QrUrlAsync() => real.QrUrlAsync();
    }

    [Fact]
    public async Task My_membership_pay_online_in_demo_starts_the_membership()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var (gw, member, _) = await NewMember();
        var opener = new Opener();
        var vm = new MyMembershipViewModel(member, new SubscriptionService(gw), opener: opener);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.NotNull(vm.PayNow);
        await vm.PayOnlineCommand.ExecuteAsync(null);
        Assert.Null(opener.Opened);           // demo pays at once: no browser
        Assert.Null(vm.PayNow);
        Assert.True(vm.HasActiveTerm);
    }

    [Fact]
    public async Task My_membership_pay_online_opens_the_xendit_link()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var (gw, member, _) = await NewMember();
        var opener = new Opener();
        var vm = new MyMembershipViewModel(new ScriptedXendit(member), new SubscriptionService(gw), opener: opener);
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.PayOnlineCommand.ExecuteAsync(null);
        Assert.Equal("https://checkout.test/ps-1", opener.Opened);
    }

    [Fact]
    public async Task Coming_back_checks_the_checkout_and_shows_the_membership_when_paid()
    {
        var (gw, member, _) = await NewMember();
        var xendit = new ScriptedXendit(member, paidOnCheck: true);
        var vm = new MyMembershipViewModel(xendit, new SubscriptionService(gw), opener: new Opener());
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(1, xendit.Checks);
        Assert.False(vm.Checking);
        Assert.True(vm.HasActiveTerm);
    }

    [Fact]
    public async Task Coming_back_unpaid_checks_once_and_keeps_the_pay_card()
    {
        var (gw, member, _) = await NewMember();
        var xendit = new ScriptedXendit(member);
        var vm = new MyMembershipViewModel(xendit, new SubscriptionService(gw), opener: new Opener());
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(1, xendit.Checks);
        Assert.NotNull(vm.PayNow);
    }
}
