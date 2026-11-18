using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner's live test, 2026-11-18: the live server's times arrive as local time and the demo's as UTC. Treating local time as UTC hid a new
// member's term for 8 hours and moved dates on Refunds and Check-in by 8 hours.
public class LiveTimeTests
{
    static DateTime LocalOf(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    [Fact]
    public void A_term_bought_just_now_counts_at_once_however_the_time_arrives()
    {
        var now = DateTime.UtcNow;
        var start = now.AddMinutes(-5);
        var term = new MembershipTerm { PackageName = "Monthly", StartsAt = LocalOf(start), EndsAt = LocalOf(start.AddDays(30)), DurationDays = 30 };
        var row = Assert.Single(MembershipService.MembershipRows([term], now));
        Assert.Equal((Status.Active, 30), (row.Status, row.DaysLeft));
    }

    [Fact]
    public void Dates_show_in_manila_once_for_local_and_utc_times()
    {
        var utc = new DateTime(2026, 11, 18, 8, 32, 0, DateTimeKind.Utc);
        foreach (var t in new[] { utc, LocalOf(utc) })
        {
            Assert.Equal(new DateTime(2026, 11, 18, 16, 32, 0), ManilaTime.Of(t));
            Assert.Equal("18 Nov 2026, 16:32", RevenuePdf.Manila(t));
            Assert.Equal("Paid until 18 Nov 2026", new MemberHit("m", "Ana", true, t).PlanText);
            Assert.Contains("Repaid 18 Nov 2026, 4:32 PM (Manila) · Cash", RefundCard.StateText(new RefundRequest { Status = "paid", PaidAt = t, RepaymentMethod = PayMethod.Cash }));
        }
    }

    [Fact]
    public void A_payment_past_its_deadline_is_not_offered_however_the_time_arrives()
    {
        var item = new PaymentItem(1, "Monthly", "GYM-1", PayKind.Membership, PayMethod.Qr, 1000, 0, Status.Pending, DateTime.UtcNow, LocalOf(DateTime.UtcNow.AddMinutes(-5)), null, false, null, null, null);
        Assert.False(item.NeedsPayment);
        Assert.False((item with { Method = PayMethod.Cash }).PaysAtDesk);
        Assert.True((item with { ExpiresAt = LocalOf(DateTime.UtcNow.AddMinutes(5)) }).NeedsPayment);
    }

    [Fact]
    public async Task The_cashier_shows_the_day_pass_price()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw), gym: new MembershipService(gw));
        await vm.OnAppearingAsync();
        vm.PickKindCommand.Execute("Day pass");
        Assert.Equal("₱150.00", vm.TotalText);
    }

    [Fact]
    public void A_booking_paid_in_full_is_not_called_a_down_payment()
    {
        var row = new BookingRow(new Reservation { Status = "confirmed", StartsAt = DateTime.UtcNow.AddDays(3), EndsAt = DateTime.UtcNow.AddDays(3).AddHours(1), TotalPrice = 100, DownPayment = 100 },
            "Boxing ring", DateTime.UtcNow, new Payment { Amount = 100, Status = Status.Verified });
        Assert.Equal("₱100.00", row.PaidWords);
        Assert.Equal("₱50.00 down payment", new BookingRow(new Reservation { TotalPrice = 100, DownPayment = 50 }, "Boxing ring", DateTime.UtcNow, new Payment { Amount = 50 }).PaidWords);
    }
}
