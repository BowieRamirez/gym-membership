using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>Owner fixes 2026-11-25, Fix 5 (X2): expense categories, as the database checks them (0159).</summary>
public static class ExpenseCategories
{
    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("electricity", "Electricity"), ("water", "Water"), ("rent", "Rent"), ("internet", "Internet"), ("equipment", "Equipment"),
        ("repairs", "Repairs and maintenance"), ("cleaning", "Cleaning supplies"), ("salaries", "Salaries (not in payroll)"), ("other", "Other"),
    ];
    public static string Name(string code) => All.FirstOrDefault(c => c.Code == code).Name ?? code;
    public static string? Code(string name) => All.FirstOrDefault(c => c.Name == name).Code;
}

public sealed record FinanceLine(string Label, decimal Amount);

public sealed record FinanceMonth(int Month, decimal Revenue, decimal Expenses, decimal Payroll)
{
    public decimal Net => Revenue - Expenses - Payroll;
    public string MonthText => System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(Month);
    public string NetText => FinanceReport.Signed(Net);
}

/// <summary>What the financial report shows for a span of days (X5).</summary>
public sealed record FinanceTotals(decimal Revenue, decimal Expenses, decimal Payroll, IReadOnlyList<FinanceLine> ByMethod, IReadOnlyList<FinanceLine> ByCategory)
{
    public decimal Net => Revenue - Expenses - Payroll;
}

/// <summary>
/// Fix 5 (X4, X5): the financial report from the parts already in the system: revenue is <c>revenue_summary</c> (verified payments minus
/// approved refunds, as the Revenue page), expenses leave out voided ones, payroll is approved or paid-out gross pay (never entered as an expense).
/// </summary>
public static class FinanceReport
{
    /// <summary>A loss reads "-₱500.00", not "₱-500.00".</summary>
    public static string Signed(decimal v) => v < 0 ? "-" + PayTypes.Money(-v) : PayTypes.Money(v);

    public static FinanceTotals Totals(IReadOnlyList<RevenueRow> revenue, IReadOnlyList<Expense> expenses, IReadOnlyList<PayrollCostDay> payroll)
    {
        var spent = expenses.Where(e => !e.IsVoided).ToList();
        return new(revenue.Sum(r => r.Total), spent.Sum(e => e.Amount), payroll.Sum(p => p.Gross),
            revenue.GroupBy(r => r.Method).Select(g => new FinanceLine(PayMethod.Name(g.Key), g.Sum(r => r.Total))).Where(l => l.Amount != 0).OrderByDescending(l => l.Amount).ToList(),
            spent.GroupBy(e => e.Category).Select(g => new FinanceLine(ExpenseCategories.Name(g.Key), g.Sum(e => e.Amount))).OrderByDescending(l => l.Amount).ToList());
    }

    /// <summary>Twelve rows for a year, January first, every month shown even when nothing happened.</summary>
    public static IReadOnlyList<FinanceMonth> Months(IReadOnlyList<RevenueRow> revenue, IReadOnlyList<Expense> expenses, IReadOnlyList<PayrollCostDay> payroll) =>
        Enumerable.Range(1, 12).Select(m => new FinanceMonth(m,
            revenue.Where(r => r.Day.Month == m).Sum(r => r.Total),
            expenses.Where(e => !e.IsVoided && e.SpentOn.Month == m).Sum(e => e.Amount),
            payroll.Where(p => p.Day.Month == m).Sum(p => p.Gross))).ToList();
}
