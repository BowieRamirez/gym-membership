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

    // owner fixes 2026-11-19 Q4 (0101): Transactions has its own filters; the audit log keeps its own, and they don't mix
    [Fact]
    public async Task Transactions_filters_by_customer_what_and_paid_with()
    {
        var (_, vm) = await AfterASale();
        await vm.OpenMoneyCommand.ExecuteAsync(null);
        vm.Customer = "carlo"; vm.WhatIndex = Array.IndexOf(vm.Whats, "Membership"); vm.PaidWithIndex = Array.IndexOf(vm.PaidWiths, "Cash");
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Entries);
        Assert.All(vm.Entries, r => Assert.Equal(("Carlo Diaz", "Cash"), (r.Customer, r.PaidWithText)));
        vm.WhatIndex = Array.IndexOf(vm.Whats, "Amenity");
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(vm.Entries);
    }

    [Fact]   // Review Focus 5
    public async Task Each_tab_keeps_its_own_filters()
    {
        var (_, vm) = await AfterASale();
        await vm.OpenMoneyCommand.ExecuteAsync(null);
        vm.Customer = "nobody by this name";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(vm.Entries);
        await vm.OpenLogsCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Entries);                 // the audit log isn't filtered by the Transactions customer
        vm.User = "Dana";
        await vm.ApplyCommand.ExecuteAsync(null);
        await vm.OpenMoneyCommand.ExecuteAsync(null);
        Assert.Equal("nobody by this name", vm.Customer);
        Assert.Empty(vm.Entries);                    // still its own filter
        await vm.ClearFiltersCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Entries);
        await vm.OpenLogsCommand.ExecuteAsync(null);
        Assert.Equal("Dana", vm.User);               // clearing Transactions left the audit log's filter alone
    }

    [Fact]
    public void Transactions_offers_the_owners_choices()
    {
        var vm = new AuditViewModel(new AdminService(new DemoGateway()));
        Assert.Equal(["All", "Membership", "Amenity", "Day pass", "Coach add-on", "Refund", "Cash shift"], vm.Whats);
        Assert.Equal(["All", "Cash", "GCash", "Maya", "QR Ph", "Card"], vm.PaidWiths);
        Assert.Equal(["All", "Pending", "Paid", "Failed", "Refunded", "Voided"], vm.Statuses);
    }

    [Fact]   // Q11: demo Transactions follows refund_requests rows to their payment, as 0101 does
    public async Task Refund_rows_show_under_Status_Refunded()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Bea", "Cruz", "bea.tx@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        var monthly = (await member.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        var pay = (await member.AvailAsync(monthly.Id)).Value!;
        Assert.True((await member.PayOnlineAsync(pay.Id)).Value!.Paid);
        var term = (await gw.ListAsync<MembershipTerm>(t => t.PaymentId == pay.Id)).Single();
        Assert.True((await new SubscriptionService(gw).RequestRefundAsync(term.Id, "Changed my mind", null)).Ok);
        var req = (await gw.ListAsync<RefundRequest>(r => r.PaymentId == pay.Id)).Single();

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var sub = new SubscriptionService(gw);
        Assert.True((await sub.DecideAsync(req.Id, true, "OK")).Ok);
        await gw.OpenDrawerAsync();
        Assert.True((await sub.MarkPaidAsync(req.Id, PayMethod.Cash, null)).Ok);

        var vm = new AuditViewModel(new AdminService(gw));
        await vm.OpenMoneyCommand.ExecuteAsync(null);
        vm.WhatIndex = Array.IndexOf(vm.Whats, "Refund");
        vm.StatusIndex = Array.IndexOf(vm.Statuses, "Refunded");
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Entries);
        Assert.All(vm.Entries, r => Assert.Equal("Refund", r.AreaText));
    }
}
