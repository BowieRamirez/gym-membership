using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner, 2026-11-12: the Windows Revenue page (mockup docs/mockups/revenue.html), from revenue_days (0058) and revenue_summary.
public class RevenueDashboardTests
{
    static readonly DateOnly Thu12Nov = new(2026, 11, 12);

    // one row a day from 1 Sep to 12 Nov; every day ₱1,000 and 10 check-ins unless changed
    static List<RevenueDay> Days(Action<Dictionary<DateOnly, RevenueDay>>? change = null)
    {
        var map = new Dictionary<DateOnly, RevenueDay>();
        for (var d = new DateOnly(2026, 9, 1); d <= Thu12Nov; d = d.AddDays(1))
            map[d] = new RevenueDay(d.ToDateTime(TimeOnly.MinValue), 1000, 1, 0, 10);
        change?.Invoke(map);
        return map.Values.OrderBy(x => x.Day).ToList();
    }

    [Fact]
    public void The_figures_are_today_this_week_this_month_and_check_ins_a_day()
    {
        var f = RevenueDashboard.Figures(Days(m =>
        {
            m[Thu12Nov] = new(Thu12Nov.ToDateTime(TimeOnly.MinValue), 4350, 6, 0, 23);
            m[new(2026, 11, 5)] = new(new DateTime(2026, 11, 5), 3200, 3, 0, 10);   // last Thursday
            m[new(2026, 11, 3)] = new(new DateTime(2026, 11, 3), 600, 1, 400, 10);  // a refund this month
        }), Thu12Nov);

        Assert.Equal(["Today", "This week", "This month", "Check-ins a day"], f.Select(x => x.Label));
        Assert.Equal(("₱4,350", "₱1,150 more than last Thursday", Trend.Up, "6 payments · 23 check-ins so far"), (f[0].Value, f[0].Compare, f[0].Trend, f[0].Sub));
        // Mon 9 to Thu 12: 3,000 + 4,350 against Mon 2 to Thu 5: 1,000 + 600 + 1,000 + 3,200
        Assert.Equal(("₱7,350", "27% up on the same days last week", Trend.Up, "Mon 9 – Thu 12 Nov"), (f[1].Value, f[1].Compare, f[1].Trend, f[1].Sub));
        // 1 to 12 Nov (12,000 + 3,350 + 2,200 - 400) against 1 to 12 Oct (12,000)
        Assert.Equal(("₱17,150", "43% up on 1–12 Oct", Trend.Up, "₱400 refunded"), (f[2].Value, f[2].Compare, f[2].Trend, f[2].Sub));
        Assert.Equal(("10", "About the same as the 30 days before", Trend.Flat), (f[3].Value, f[3].Compare, f[3].Trend));
        Assert.StartsWith("Average, last 30 days · busiest ", f[3].Sub);
    }

    [Fact]
    public void Less_than_last_week_reads_down_and_nothing_to_compare_with_is_said_plainly()
    {
        var f = RevenueDashboard.Figures(Days(m =>
        {
            foreach (var d in m.Keys.Where(d => d < new DateOnly(2026, 11, 1)).ToList()) m[d] = m[d] with { Net = 0, Payments = 0, Visits = 0 };
            m[Thu12Nov] = m[Thu12Nov] with { Net = 500 };
            m[new(2026, 11, 5)] = m[new(2026, 11, 5)] with { Net = 900 };
        }), Thu12Nov);
        Assert.Equal(("₱400 less than last Thursday", Trend.Down), (f[0].Compare, f[0].Trend));
        Assert.Equal(("Nothing on 1–12 Oct to compare with", Trend.Flat), (f[2].Compare, f[2].Trend));
        Assert.Equal(("No check-ins in the 30 days before", Trend.Flat), (f[3].Compare, f[3].Trend));
        Assert.Equal("No refunds", f[2].Sub);
    }

    [Fact]
    public void On_a_Monday_the_week_is_one_day_and_the_month_compares_with_the_last_day_that_month_had()
    {
        var mon = new DateOnly(2026, 11, 9);
        Assert.Equal("Mon 9 Nov", RevenueDashboard.Figures(Days(), mon)[1].Sub);
        var mar31 = new DateOnly(2026, 3, 31);
        var days = Enumerable.Range(0, 120).Select(i => mar31.AddDays(-i)).Select(d => new RevenueDay(d.ToDateTime(TimeOnly.MinValue), 100, 1, 0, 1)).OrderBy(x => x.Day).ToList();
        Assert.Equal("11% up on 1–28 Feb", RevenueDashboard.Figures(days, mar31)[2].Compare);   // 31 days against 28
        Assert.Equal(new DateOnly(2026, 1, 31), RevenueDashboard.FiguresFrom(mar31));   // 60 days back (for check-ins) reaches before 1 Feb
        Assert.Equal(new DateOnly(2026, 9, 14), RevenueDashboard.FiguresFrom(Thu12Nov));
    }

    [Fact]
    public void A_big_jump_reads_as_money_and_the_chart_steps_are_round()
    {
        Assert.Equal(("₱22,150 more than 1–12 Oct", Trend.Up), RevenueDashboard.Against(23650, 1500, "1–12 Oct"));
        Assert.Equal(("100% up on 1–12 Oct", Trend.Up), RevenueDashboard.Against(3000, 1500, "1–12 Oct"));
        Assert.Equal([5000m, 2.5m, 1m, 1m], new[] { 14000m, 7m, 2m, 0m }.Select(RevenueDashboard.NiceStep));
    }

    [Fact]
    public void Shares_are_sorted_with_their_part_of_the_total()
    {
        var s = RevenueDashboard.Shares([("Membership", 600m), ("Walk-in", 100m), ("Membership", 200m), ("Amenity", 100m)]);
        Assert.Equal([("Membership", "₱800", "80%"), ("Walk-in", "₱100", "10%"), ("Amenity", "₱100", "10%")], s.Select(x => (x.Name, x.AmountText, x.PercentText)));
        Assert.Equal(0.8, s[0].Fraction, 3);
        Assert.Equal(0, RevenueDashboard.Shares([("Membership", -50m)])[0].Fraction);   // all refunded: no bar, no division by zero
    }

    [Fact]
    public async Task The_page_loads_the_figures_the_chart_the_shares_and_the_days_from_the_demo()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new RevenueViewModel(new AdminService(gw));
        await vm.ShowRangeCommand.ExecuteAsync("7");
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(4, vm.Figures.Count);
        Assert.Equal(7, vm.Chart.Count);
        Assert.Equal(DateTime.Today, vm.To.Date);
        Assert.Equal(vm.Chart.Sum(d => d.Net), vm.Rows.Sum(r => r.Total));   // revenue_days agrees with revenue_summary
        Assert.Equal(vm.Chart.Count, vm.DayRows.Count);
        Assert.Equal(vm.Chart.Last().Day, vm.DayRows.First().Day);             // newest first

        vm.ShowMetricCommand.Execute("visits");
        Assert.Equal(("visits", $"{vm.Chart.Sum(d => d.Visits):N0} check-ins"), (vm.Metric, vm.ChartTotalText));
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.False((await new AdminService(gw).RevenueDaysAsync(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today))).Ok);
    }
}
