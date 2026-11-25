using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner fixes 2026-11-25, Fix 5 (X1-X6): Expenses, and Financial reports from revenue, expenses and approved payroll.
public class FinanceTests
{
    static async Task<(DemoGateway Gw, AdminService Admin)> Admin()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        return (gw, new AdminService(gw));
    }

    [Fact]
    public void Report_nets_revenue_against_expenses_and_payroll_and_leaves_voided_expenses_out()
    {
        RevenueRow Rev(int month, string method, decimal total) => new(new DateTime(2026, month, 3), PayKind.Membership, method, 1, total);
        Expense Exp(int month, string cat, decimal amount, bool voided = false) =>
            new() { SpentOn = new DateOnly(2026, month, 5), Category = cat, Amount = amount, VoidedAt = voided ? DateTime.UtcNow : null };
        var revenue = new[] { Rev(1, PayMethod.Cash, 10000), Rev(1, PayMethod.Qr, 5000), Rev(2, PayMethod.Cash, 2000) };
        var expenses = new[] { Exp(1, "rent", 6000), Exp(1, "electricity", 1500), Exp(1, "rent", 6000, voided: true), Exp(2, "water", 300) };
        var payroll = new[] { new PayrollCostDay { Day = new DateOnly(2026, 1, 15), Gross = 4000 }, new PayrollCostDay { Day = new DateOnly(2026, 1, 31), Gross = 4000 } };

        var t = FinanceReport.Totals(revenue, expenses, payroll);
        Assert.Equal((17000m, 7800m, 8000m, 1200m), (t.Revenue, t.Expenses, t.Payroll, t.Net));
        Assert.Equal(new[] { ("Cash", 12000m), ("Online", 5000m) }, t.ByMethod.Select(l => (l.Label, l.Amount)));
        Assert.Equal(new[] { ("Rent", 6000m), ("Electricity", 1500m), ("Water", 300m) }, t.ByCategory.Select(l => (l.Label, l.Amount)));

        var months = FinanceReport.Months(revenue, expenses, payroll);
        Assert.Equal(12, months.Count);
        Assert.Equal((15000m, 7500m, 8000m, -500m), (months[0].Revenue, months[0].Expenses, months[0].Payroll, months[0].Net));
        Assert.Equal((2000m, 300m, 0m, 1700m), (months[1].Revenue, months[1].Expenses, months[1].Payroll, months[1].Net));
        Assert.All(months.Skip(2), m => Assert.Equal(0m, m.Net));
    }

    [Fact]
    public async Task Expenses_are_added_checked_listed_and_voided_with_a_reason()
    {
        var (_, admin) = await Admin();
        var vm = new ExpensesViewModel(admin) { From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 31) };
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains("Rent", vm.CategoryOptions);

        vm.SpentOn = new DateTime(2026, 10, 5); vm.Category = "Rent"; vm.AmountText = "abc"; vm.Note = "October rent";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal("Enter an amount above ₱0, like 1500 or 1500.50.", vm.AddError);
        vm.AmountText = "15,000"; vm.Note = " ";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal("Say what it was for (1–200 characters).", vm.AddError);
        vm.Note = "October rent";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Null(vm.AddError);
        Assert.Equal("", vm.AmountText);
        var item = Assert.Single(vm.Items);
        Assert.Equal(("Rent", "₱15,000.00", "October rent", "October 5, 2026"), (item.CategoryText, item.AmountText, item.Note, item.DayText));
        Assert.Equal("₱15,000.00", vm.TotalText);

        item.BeginVoidCommand.Execute(null);
        await item.VoidCommand.ExecuteAsync(null);
        Assert.Equal("Say why it's voided (1–200 characters).", item.ErrorText);
        item.VoidReason = "Entered twice";
        await item.VoidCommand.ExecuteAsync(null);
        item = Assert.Single(vm.Items);
        Assert.True(item.IsVoided);
        Assert.Contains("Entered twice", item.VoidText);
        Assert.Equal("₱0.00", vm.TotalText);   // a voided expense isn't counted
    }

    [Fact]
    public async Task Financial_report_counts_approved_payroll_once_and_shows_a_year_by_month()
    {
        var (gw, admin) = await Admin();
        var desk = (await admin.PayrollStaffAsync()).Value!.First(s => s.Role == "employee");
        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 9000m, "fixed")).Ok);
        var aug = (await admin.PayrollPeriodAsync(new DateOnly(2026, 8, 1))).Value!;
        Assert.True((await admin.MarkPayrollPaidAsync(aug.PeriodId)).Ok);   // approved: counted
        Assert.True((await admin.PayrollPeriodAsync(new DateOnly(2026, 8, 16))).Ok);   // a draft: not counted yet
        Assert.True((await admin.AddExpenseAsync(new DateOnly(2026, 8, 3), "internet", 1999m, "PLDT August")).Ok);

        var vm = new FinanceViewModel(admin) { Year = 2026, Month = 8 };
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("August 2026", vm.SpanText);
        Assert.Equal(PayTypes.Money(9000m), vm.PayrollText);
        Assert.Equal(PayTypes.Money(1999m), vm.ExpensesText);
        var net = vm.Totals!.Revenue - 1999m - 9000m;
        Assert.Equal(net < 0 ? "-" + PayTypes.Money(-net) : PayTypes.Money(net), vm.NetText);   // a loss reads "-₱10,999.00"
        Assert.Equal(net < 0, vm.IsLoss);
        Assert.Contains(vm.ByCategory, l => l.Label == "Internet" && l.Amount == 1999m);

        vm.ShowYearCommand.Execute(null);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("2026", vm.SpanText);
        Assert.Equal(12, vm.Months.Count);
        Assert.Equal(9000m, vm.Months[7].Payroll);
        Assert.Equal(PayTypes.Money(9000m), vm.PayrollText);
    }
}
