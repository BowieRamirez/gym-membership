using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 7 of the 2026-11-12 plan (0065, G16-G18): Admin closes the gym for a whole Manila day with a reason. Nothing can be set up that
// day, what is already set up is cancelled (paid bookings to Refunds), everyone is told, and the calendars show the day as closed.
public class GymClosureTests
{
    static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
    static readonly DateOnly Closed = Today.AddDays(2);
    static DateTime At(DateOnly d, int hour) => DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(hour, 0)).AddHours(-8), DateTimeKind.Utc);

    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static async Task CloseAsync(DemoGateway gw, string reason = "Holiday")
    {
        await As("admin@demo.io", gw);
        Assert.True((await new AdminService(gw).CloseDayAsync(Closed, reason)).Ok);
    }

    [Fact]
    public async Task A_closed_days_refund_of_down_payment_and_balance_can_be_recorded_as_repaid()   // 0095
    {
        var gw = await As("member@demo.io");
        var classes = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Group classes");
        var made = (await new ReservationService(gw).BookAsync(classes.Id, At(Closed, 10), 60, PayMethod.Cash)).Value!;
        await As("employee@demo.io", gw);
        await gw.OpenDrawerAsync();
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(made.PaymentId!.Value, true)).Ok);
        var balance = (await new ReservationService(gw).TakeBalanceAsync(made.Id, PayMethod.Cash, null)).Value!;

        await CloseAsync(gw);
        await gw.OpenDrawerAsync();
        var subs = new SubscriptionService(gw);
        var card = (await subs.RefundsAsync()).Value!.Single(c => c.Request.ReservationId == made.Id);
        Assert.Equal(made.DownPayment + balance.Amount, card.Request.Amount);
        Assert.True((await subs.MarkPaidAsync(card.Request.Id, PayMethod.Cash, null)).Ok);
    }

    [Fact]
    public void Online_money_that_could_not_be_applied_reads_as_its_own_refund_to_repay()   // 0095
    {
        var request = new RefundRequest { Kind = "online", Status = "approved", Amount = 1000, MemberId = null!, RequestedAt = new DateTime(2026, 11, 18, 2, 0, 0, DateTimeKind.Utc),
            DecisionReason = "Paid online after this payment was no longer open" };
        var card = RefundCard.ForOnline(request, new Payment { PaymentCode = "GYM-1", OnlineChannel = "GCASH", WalkInName = "Ana" }, null);
        Assert.Equal(("Ana", "Online payment", "GYM-1 · GCASH", "18 Nov, 10:00 AM"), (card.Member, card.PackageName, card.Dates, card.Period));
        Assert.Equal(("To repay", "Repay", true, false), (card.Status, card.ActionText, card.CanRepay, card.CanDecide));
        Assert.Equal("Paid online after this payment was no longer open.", card.ReviewText);
        Assert.Equal("Member", RefundCard.ForOnline(request, null, null).Member);
    }

    [Fact]
    public async Task Nothing_can_be_set_up_on_a_closed_day_and_the_refusal_says_why()
    {
        var gw = new DemoGateway();
        await CloseAsync(gw);
        await As("member@demo.io", gw);
        var pool = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Pool");
        var booked = await new ReservationService(gw).BookAsync(pool.Id, At(Closed, 10), 60, PayMethod.Qr);
        Assert.Equal("day_closed", booked.Message);
        Assert.Equal("The gym is closed that day. See Announcements for why, and choose another day.", ErrorText.For(booked.Error, booked.Message));
        Assert.True((await new ReservationService(gw).BookAsync(pool.Id, At(Closed.AddDays(1), 10), 60, PayMethod.Qr)).Ok);

        await As("coach@demo.io", gw);
        Assert.False((await new CoachService(gw).PostShiftAsync(At(Closed, 8), At(Closed, 12), null)).Ok);
    }

    [Fact]
    public async Task Closing_cancels_the_days_bookings_refunds_what_was_paid_and_tells_everyone()
    {
        var gw = await As("member@demo.io");   // Alex has no Group classes pass: booking it is a request with a down payment
        var classes = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Group classes");
        var made = (await new ReservationService(gw).BookAsync(classes.Id, At(Closed, 10), 60, PayMethod.Cash)).Value!;
        await As("employee@demo.io", gw);
        await gw.OpenDrawerAsync();
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(made.PaymentId!.Value, true)).Ok);

        await As("admin@demo.io", gw);
        var admin = new AdminService(gw);
        var preview = (await admin.ClosurePreviewAsync(Closed)).Value!;
        Assert.Equal((1, 1), (preview.Bookings, preview.Paid));   // the demo also has a session request that day
        Assert.StartsWith("1 booking (1 paid, refunded), ", preview.Text);
        Assert.True((await admin.CloseDayAsync(Closed, "Holiday")).Ok);
        Assert.False((await admin.CloseDayAsync(Closed, "Again")).Ok);

        var r = (await gw.ListAsync<Reservation>()).Single(x => x.Id == made.Id);
        Assert.Equal(("cancelled", "Gym closed: Holiday"), (r.Status, r.CancelReason));
        Assert.Contains(await gw.ListAsync<RefundRequest>(), f => f.ReservationId == made.Id && f.Status == "approved" && f.Amount == made.DownPayment);

        await As("member@demo.io", gw);
        var notes = await gw.ListAsync<UserNotification>();
        Assert.Contains(notes, n => n.Title == "Booking cancelled" && n.Body == "Gym closed: Holiday");
        Assert.Contains(notes, n => n.Title == "Announcement" && n.Body!.StartsWith("The gym is closed on " + Closed.ToString("dddd, d MMM", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task The_booking_sheet_greys_a_closed_day_with_its_reason()
    {
        var gw = new DemoGateway();
        await CloseAsync(gw, "Repairs");
        await As("member@demo.io", gw);
        var vm = new BookAmenityViewModel(new ReservationService(gw), new AmenityService(gw), new ConfirmationTests.Nav());
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.OpenCommand.ExecuteAsync(vm.Cards.Single(c => c.Amenity.Name == "Pool"));
        if (vm.Days.All(d => d.Date != Closed)) vm.NextMonthCommand.Execute(null);
        var day = vm.Days.Single(d => d.Date == Closed);
        Assert.False(day.CanPick);
        Assert.Equal("Repairs", day.ClosedReason);
        Assert.EndsWith(", gym closed: Repairs", day.Description);
        await vm.PickDayCommand.ExecuteAsync(day);   // a closed day opens to say why, with no times
        Assert.True(vm.IsDayClosed);
        Assert.Empty(vm.Times);
        Assert.Equal("The gym is closed this day: Repairs.", vm.TimesNote);
    }

    [Fact]
    public async Task Gym_settings_closes_and_reopens_a_day()
    {
        var gw = await As("admin@demo.io");
        var asked = new List<string>();
        Confirm.Answer = (title, message, action) => { asked.Add($"{title}|{message}"); return Task.FromResult(true); };
        try
        {
            var vm = new GymSettingsViewModel(new AdminService(gw));
            await vm.OnAppearingAsync();
            vm.ClosedDay = Closed.ToDateTime(TimeOnly.MinValue);
            vm.ClosedReason = " ";
            await vm.CloseDayCommand.ExecuteAsync(null);
            Assert.Equal("Type why the gym is closed, for example Holiday.", vm.ErrorMessage);
            Assert.Empty(asked);

            vm.ClosedReason = "Holiday";
            var preview = (await new AdminService(gw).ClosurePreviewAsync(Closed)).Value!.Text;   // what the confirm reads back, before anything is cancelled
            await vm.CloseDayCommand.ExecuteAsync(null);
            Assert.Null(vm.ErrorMessage);
            Assert.Contains(preview, Assert.Single(asked));
            var row = Assert.Single(vm.Closures);
            Assert.Equal((Closed, "Holiday", true), (row.Day, row.Reason, row.CanReopen));

            await vm.ReopenCommand.ExecuteAsync(row);
            Assert.Empty(vm.Closures);
            Assert.Empty(await gw.ListAsync<GymClosure>());
        }
        finally { Confirm.Answer = null; }
    }

    [Fact]
    public async Task The_member_calendar_shows_a_closed_day_with_its_reason()
    {
        var gw = new DemoGateway();
        await CloseAsync(gw);
        await As("member@demo.io", gw);
        var data = (await new MemberCalendarService(gw).RangeAsync(At(Closed, 0), At(Closed.AddDays(1), 0))).Value!;
        var closed = Assert.Single(data.Entries, e => e.Kind == "closed");
        Assert.Equal(("Gym closed: Holiday", "Gym closed", "Closed"), (closed.Title, closed.KindText, closed.Short));
    }

    [Fact]
    public async Task The_coach_dashboard_offers_no_shift_on_a_closed_day()
    {
        var gw = new DemoGateway();
        await CloseAsync(gw);
        await As("coach@demo.io", gw);
        var vm = new AttendanceViewModel(new SessionService(gw), new AttendanceService(gw), new CoachService(gw), new MembershipService(gw))
        { Zone = TimeZoneInfo.CreateCustomTimeZone("Manila", TimeSpan.FromHours(8), "Manila", "Manila") };
        await vm.OnAppearingAsync();
        vm.SelectDayCommand.Execute(new CalendarDay(Closed, true, false, false, 0, 0, 0, 0));
        Assert.False(vm.CanPost);
        Assert.Equal("The gym is closed this day: Holiday.", vm.ClosedNote);
        vm.SelectDayCommand.Execute(new CalendarDay(Closed.AddDays(1), true, false, false, 0, 0, 0, 0));
        Assert.True(vm.CanPost);
        Assert.Equal("", vm.ClosedNote);
    }
}
