using System.Globalization;

namespace GymMembership.Core.Services;

/// <summary>One Manila day from revenue_days (0058): net after refunds, payments verified, refunds approved, gym check-ins.</summary>
public sealed record RevenueDay(DateTime Day, decimal Net, long Payments, decimal Refunds, long Visits);

public enum Trend { Flat, Up, Down }

/// <summary>A figure at the top of the Windows Revenue page: the value, how it compares, and one line of context.</summary>
public sealed record RevenueFigure(string Label, string Value, string Compare, Trend Trend, string Sub)
{
    public bool IsUp => Trend == Trend.Up;
    public bool IsDown => Trend == Trend.Down;
}

/// <summary>One line of "Where it came from": an item or a method, its amount and its part of the period's total.</summary>
public sealed record RevenueShare(string Name, decimal Amount, double Fraction)
{
    public string AmountText => RevenueDashboard.Peso(Amount);
    public string PercentText => $"{Fraction * 100:0}%";
}

/// <summary>A row of the Windows page's "Day by day" table.</summary>
public sealed record RevenueDayRow(DateTime Day, long Payments, long Visits, decimal Refunds, decimal Net, string Caption)
{
    public string RefundsText => Refunds > 0 ? "-" + RevenueDashboard.Peso(Refunds) : "–";
    public bool HasRefunds => Refunds > 0;
}

/// <summary>The sums behind the Windows Revenue page (mockup docs/mockups/revenue.html, owner 2026-11-12).</summary>
public static class RevenueDashboard
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-PH");
    public static string Peso(decimal v) => (v < 0 ? "-₱" : "₱") + Math.Abs(v).ToString("N0", En);

    /// <summary>The first day the figures need: the start of last month, or 60 days back for the two check-in averages.</summary>
    public static DateOnly FiguresFrom(DateOnly today)
    {
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var sixty = today.AddDays(-59);
        return sixty < lastMonth ? sixty : lastMonth;
    }

    public static IReadOnlyList<RevenueFigure> Figures(IReadOnlyList<RevenueDay> days, DateOnly today)
    {
        var byDay = days.ToDictionary(d => DateOnly.FromDateTime(d.Day));
        RevenueDay On(DateOnly d) => byDay.GetValueOrDefault(d) ?? new RevenueDay(d.ToDateTime(TimeOnly.MinValue), 0, 0, 0, 0);
        IEnumerable<RevenueDay> Span(DateOnly from, DateOnly to) { for (var d = from; d <= to; d = d.AddDays(1)) yield return On(d); }
        decimal Net(DateOnly from, DateOnly to) => Span(from, to).Sum(d => d.Net);

        // today, against the same weekday last week
        var now = On(today); var weekAgo = On(today.AddDays(-7)).Net; var dayName = today.ToString("dddd", En);
        var diff = now.Net - weekAgo;
        var todayFigure = new RevenueFigure("Today", Peso(now.Net),
            diff > 0 ? $"{Peso(diff)} more than last {dayName}" : diff < 0 ? $"{Peso(-diff)} less than last {dayName}" : $"Same as last {dayName}",
            diff > 0 ? Trend.Up : diff < 0 ? Trend.Down : Trend.Flat,
            $"{now.Payments} {(now.Payments == 1 ? "payment" : "payments")} · {now.Visits} {(now.Visits == 1 ? "check-in" : "check-ins")} so far");

        // this week from Monday, against the same days last week
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = Net(monday, today);
        var (weekText, weekTrend) = Against(week, Net(monday.AddDays(-7), today.AddDays(-7)), "the same days last week");
        var weekSub = monday == today ? today.ToString("ddd d MMM", En)
            : $"{monday.ToString(monday.Month == today.Month ? "ddd d" : "ddd d MMM", En)} – {today.ToString("ddd d MMM", En)}";

        // this month from the 1st, against the same dates last month (as far as that month goes)
        var first = new DateOnly(today.Year, today.Month, 1);
        var lastFirst = first.AddMonths(-1);
        var lastTo = lastFirst.AddDays(Math.Min(today.Day, DateTime.DaysInMonth(lastFirst.Year, lastFirst.Month)) - 1);
        var month = Net(first, today);
        var lastLabel = $"1–{lastTo.Day} {lastTo.ToString("MMM", En)}";
        var (monthText, monthTrend) = Against(month, Net(lastFirst, lastTo), lastLabel);
        var refunded = Span(first, today).Sum(d => d.Refunds);

        // check-ins a day: the last 30 days against the 30 before
        var recent = Span(today.AddDays(-29), today).ToList();
        var avg = recent.Average(d => (double)d.Visits);
        var before = Span(today.AddDays(-59), today.AddDays(-30)).Average(d => (double)d.Visits);
        var change = before > 0 ? (avg - before) / before * 100 : 0;
        var (visitText, visitTrend) = before <= 0 ? ("No check-ins in the 30 days before", Trend.Flat)
            : Math.Abs(change) < 5 ? ("About the same as the 30 days before", Trend.Flat)
            : ($"{Math.Abs(change):0}% {(change > 0 ? "up" : "down")} on the 30 days before", change > 0 ? Trend.Up : Trend.Down);
        DayOfWeek? busiest = recent.Sum(d => d.Visits) == 0 ? null
            : recent.GroupBy(d => d.Day.DayOfWeek).OrderByDescending(g => g.Average(d => d.Visits)).First().Key;

        return
        [
            todayFigure,
            new("This week", Peso(week), weekText, weekTrend, weekSub),
            new("This month", Peso(month), monthText, monthTrend, refunded > 0 ? $"{Peso(refunded)} refunded" : "No refunds"),
            new("Check-ins a day", avg < 10 ? avg.ToString("0.#", En) : avg.ToString("N0", En), visitText, visitTrend,
                "Average, last 30 days" + (busiest is { } b ? $" · busiest {CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(b)}s" : "")),
        ];
    }

    public static (string Text, Trend Trend) Against(decimal now, decimal then, string what)
    {
        if (then <= 0) return ($"Nothing on {what} to compare with", Trend.Flat);
        var pct = Math.Round((now - then) / then * 100);
        // a jump of more than double reads better as money than as a four-figure percentage
        if (pct > 200) return ($"{Peso(now - then)} more than {what}", Trend.Up);
        return pct == 0 ? ($"Same as {what}", Trend.Flat) : ($"{Math.Abs(pct)}% {(pct > 0 ? "up" : "down")} on {what}", pct > 0 ? Trend.Up : Trend.Down);
    }

    /// <summary>Guide-line spacing for a chart whose highest value is <paramref name="max"/>: 1, 2, 2.5 or 5 times a power of ten, three steps reaching it.</summary>
    public static decimal NiceStep(decimal max)
    {
        if (max <= 0) return 1;
        var raw = (double)max / 3;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * power).First(s => s >= raw);
        return (decimal)step;
    }

    /// <summary>Amounts grouped by name, largest first, each with its part of the total (no bar when the total is nothing).</summary>
    public static IReadOnlyList<RevenueShare> Shares(IEnumerable<(string Name, decimal Amount)> amounts)
    {
        var grouped = amounts.GroupBy(a => a.Name).Select(g => (Name: g.Key, Amount: g.Sum(a => a.Amount))).OrderByDescending(g => g.Amount).ToList();
        var total = grouped.Sum(g => g.Amount);
        return grouped.Select(g => new RevenueShare(g.Name, g.Amount, total > 0 && g.Amount > 0 ? (double)(g.Amount / total) : 0)).ToList();
    }
}
