using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Plan 2026-11-17 Z4-Z6 (0086): Transactions is its own tab with the customer, amount and how they paid; the audit log keeps the rest.</summary>
public class TransactionsLogTests
{
    static readonly string[] Money = ["Payment", "Refund", "Cash shift"];

    static async Task<(DemoGateway Gw, AuditViewModel Vm)> AfterASale()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var desk = new PaymentStaffService(gw);
        var item = (await desk.ItemsAsync(PayKind.Membership, "u-carlo")).Value![0];
        Assert.True((await desk.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-carlo", item.Id, null, null, null)).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        return (gw, new AuditViewModel(new AdminService(gw)));
    }

    [Fact]
    public async Task Transactions_lists_only_money_rows_with_customer_amount_and_paid_with()
    {
        var (_, vm) = await AfterASale();
        await vm.OpenMoneyCommand.ExecuteAsync(null);
        Assert.True(vm.ShowMoney);
        Assert.NotEmpty(vm.Entries);
        Assert.All(vm.Entries, r => Assert.Contains(r.AreaText, Money));
        var sale = vm.Entries.First(r => r.Action == "payment.record");
        Assert.Equal(("Carlo Diaz", "Cash"), (sale.Customer, sale.PaidWithText));
        Assert.StartsWith("₱", sale.AmountText);
    }

    [Fact]
    public async Task Audit_log_no_longer_lists_payments()
    {
        var (_, vm) = await AfterASale();
        await vm.OnAppearingAsync();
        Assert.NotEmpty(vm.Entries);
        Assert.All(vm.Entries, r => Assert.DoesNotContain(r.AreaText, Money));
    }

    [Fact]
    public void A_Xendit_channel_wins_over_the_payment_method() =>
        Assert.Equal("GCash", new AuditRecord { PaidWith = "GCASH" }.PaidWithText);

    [Fact]
    public void Export_has_the_customer_amount_and_paid_with_columns()
    {
        var csv = TransactionsCsv.Build([new AuditRecord { Action = "payment.record", ActorName = "Marco", Customer = "Ana", Amount = 1500, PaidWith = "cash" }]);
        Assert.StartsWith("\"Date and time (Manila)\",\"Customer\",\"What\",\"Amount\",\"Paid with\",\"Recorded by\",\"Object\"", csv);
        Assert.Contains("\"Ana\"", csv);
        Assert.Contains("\"Cash\"", csv);
    }
}
