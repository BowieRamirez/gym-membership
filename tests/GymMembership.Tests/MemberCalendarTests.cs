using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 6 of the 2026-11-09 plan: the member Calendar (F15) and the menu order by membership state (F16).
// Alex (member@demo.io) is active and has hired Lena (u-coach1), who has an open slot tomorrow at 10:00 Manila; Carlo's membership expired.
public class MemberCalendarTests
{
    // ---------- F16: menu order ----------
    [Fact]
    public void An_active_member_sees_their_membership_first_and_packages_near_the_end_as_renew_or_change()
    {
        Assert.Equal(["My membership", "Calendar", "Sessions", "Book an amenity", "Coaches", "Time requests", "Messages", "Renew or change", "Settings"],
            MemberMenu.Order(active: true).Select(i => i.Title));
        Assert.Equal(["My membership", "Calendar", "Sessions", "Messages"], MemberMenu.Tabs(active: true).Select(i => i.Title));
        Assert.Equal("Packages", MemberMenu.Order(true).Single(i => i.Title == "Renew or change").Page);
    }

    [Fact]
    public void Without_a_membership_packages_come_first_and_members_only_pages_stay_listed()
    {
        Assert.Equal(["Packages", "My membership", "Calendar", "Sessions", "Book an amenity", "Coaches", "Time requests", "Messages", "Settings"],
            MemberMenu.Order(active: false).Select(i => i.Title));
        Assert.Equal(["Packages", "My membership", "Calendar", "Messages"], MemberMenu.Tabs(active: false).Select(i => i.Title));
        Assert.True(MemberMenu.Order(false).Single(i => i.Title == "Calendar").MembersOnly);
        Assert.False(MemberMenu.Order(false).Single(i => i.Title == "Coaches").MembersOnly);
    }

    // ---------- F15: the calendar ----------
    static async Task<(DemoGateway Gw, MemberCalendarViewModel Vm, ConfirmationTests.Nav Nav)> Calendar(string email = "member@demo.io")
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var nav = new ConfirmationTests.Nav();
        var vm = new MemberCalendarViewModel(new MemberCalendarService(gw), new SessionService(gw), nav);
        await vm.LoadCommand.ExecuteAsync(null);
        return (gw, vm, nav);
    }
    static DateOnly Tomorrow => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(1);

    [Fact]
    public void Each_day_names_its_entries_in_time_order_two_at_most_and_dots_by_kind()
    {
        DateTime At(int day, int hour) => new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-8);   // Manila
        var entries = new[]
        {
            new CalendarEntry(At(12, 17), At(12, 18), "booking", "Pool"),
            new CalendarEntry(At(12, 9), At(12, 10), "session", "Session with Lena C."),
            new CalendarEntry(At(12, 6), At(12, 10), "shift", "Lena on the floor"),
            new CalendarEntry(At(13, 10), At(13, 11), "open", "Open with Lena C."),
        };
        var days = MemberCalendar.Days(new DateOnly(2026, 11, 1), entries, today: new DateOnly(2026, 11, 10), selected: new DateOnly(2026, 11, 12));
        Assert.Equal(42, days.Count);
        var d12 = days.Single(d => d.Date == new DateOnly(2026, 11, 12));
        Assert.Equal(("Shift", "Session", "+1 more"), (d12.Entry1, d12.Entry2, d12.More));
        Assert.Equal(("shift", "session"), (d12.Kind1, d12.Kind2));
        Assert.Equal((true, true, true, false), (d12.IsSelected, d12.HasSession, d12.HasBooking, d12.HasOpen));
        Assert.True(days.Single(d => d.Date == new DateOnly(2026, 11, 13)).HasOpen);
        Assert.True(days.Single(d => d.Date == new DateOnly(2026, 11, 10)).IsToday);
        Assert.True(days.Single(d => d.Date == new DateOnly(2026, 10, 31)).Dimmed);   // the grid's days from the month before
    }

    [Fact]
    public async Task A_member_sees_their_coachs_open_time_and_requesting_it_sends_a_time_request()
    {
        var (gw, vm, _) = await Calendar();
        await vm.PickDayCommand.ExecuteAsync(vm.Days.Single(d => d.Date == Tomorrow));
        var open = Assert.Single(vm.DayEntries, e => e.Kind == "open");
        Assert.True(open.CanRequest);
        Assert.StartsWith("Open with ", open.Title);
        await vm.RequestCommand.ExecuteAsync(open);
        Assert.StartsWith("You already have an active session together", vm.ErrorMessage);   // the same rules as Time requests
        foreach (var s in await gw.ListAsync<TrainingSession>(s => s.MemberId == "u-alex" && s.Status == Status.Scheduled))
            Assert.True((await new SessionService(gw).CancelSessionAsync(s.Id)).Ok);
        foreach (var r in await gw.ListAsync<TimeRequest>(r => r.MemberId == "u-alex" && r.RequestedBy == "member" && r.Status == "pending"))
            Assert.True((await new SessionService(gw).CancelRequestAsync(r.Id)).Ok);
        await vm.RequestCommand.ExecuteAsync(open);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Request sent. Your coach answers it in Time requests.", vm.Notice);
        var sent = (await gw.ListAsync<TimeRequest>(r => r.MemberId == "u-alex" && r.RequestedBy == "member")).OrderBy(r => r.Id).Last();
        Assert.Equal((open.StartUtc, open.EndUtc), (sent.RequestedStart.ToUniversalTime(), sent.RequestedEnd.ToUniversalTime()));
    }

    [Fact]
    public async Task Sessions_and_amenity_bookings_show_and_a_booking_opens_book_an_amenity()
    {
        var (gw, vm, nav) = await Calendar();
        var pool = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Pool");
        var start = DateTime.SpecifyKind(Tomorrow.ToDateTime(new TimeOnly(15, 0)).AddHours(-8), DateTimeKind.Utc);
        Assert.True((await new ReservationService(gw).BookAsync(pool.Id, start, 60, PayMethod.Qr)).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.PickDayCommand.ExecuteAsync(vm.Days.Single(d => d.Date == Tomorrow));
        var booking = Assert.Single(vm.DayEntries, e => e.Kind == "booking");
        Assert.Equal(("Pool", "3:00 PM to 4:00 PM"), (booking.Title, booking.Time));
        vm.OpenCommand.Execute(booking);
        Assert.Equal(1, nav.Bookings);
        Assert.True(vm.Days.Single(d => d.Date == Tomorrow).HasBooking);
        Assert.True(vm.HasCoach);
    }

    [Fact]
    public async Task A_member_without_a_coach_sees_the_gym_hours_and_no_coach_times()
    {
        var (_, vm, _) = await Calendar("carlo@demo.io");
        Assert.False(vm.HasCoach);
        Assert.StartsWith("The gym is open ", vm.HoursText);
        Assert.DoesNotContain(vm.Days, d => d.HasOpen);
    }
}
