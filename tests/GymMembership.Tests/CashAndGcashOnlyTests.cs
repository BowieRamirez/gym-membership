using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner, 2026-11-11 (before Phase 8): the desk takes Cash or GCash only; the demo refuses a bank transfer as the database does (0054).
// Old bank-transfer rows keep their name in the history and the revenue PDF (PayMethod.Name).
public class CashAndGcashOnlyTests
{
    static async Task<DemoGateway> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Fact]
    public void The_cashier_offers_cash_and_gcash_only()
    {
        var vm = new CashierViewModel(null!, null!);
        Assert.Equal(["Cash", "GCash"], vm.Methods);
    }

    [Fact]
    public async Task A_bank_transfer_at_the_desk_is_refused()
    {
        var gw = await As("employee@demo.io");
        var r = await new PaymentStaffService(gw).RecordCounterAsync("walk_in", PayMethod.BankTransfer, null, null, "Bank Ben", "BT-1", null);
        Assert.Equal(AppErrorKind.Validation, r.Error);
    }

    [Fact]
    public async Task A_refund_cannot_be_repaid_by_bank_transfer_but_gcash_works()
    {
        var (gw, clock, subs, monthly, _) = await SubscriptionTests.Setup();
        clock.Now = DateTimeOffset.UtcNow.AddYears(1);
        var p = (await subs.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var term = Assert.Single((await subs.TermsAsync()).Value!).Term;
        Assert.True((await subs.RequestRefundAsync(term.Id, "Changed plans", "change_of_mind")).Ok);
        var request = Assert.Single((await subs.RefundsAsync()).Value!).Request;
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await subs.DecideAsync(request.Id, true, "Approved")).Ok);
        Assert.Equal("bad_input", (await subs.MarkPaidAsync(request.Id, PayMethod.BankTransfer, "BT-9")).Message);
        Assert.True((await subs.MarkPaidAsync(request.Id, PayMethod.Gcash, "REF-R-1")).Ok);
    }
}
