using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

// Owner fixes 2026-11-25, Fix 3 Phase 3.4 (S2-S4): coaches no longer post shifts; check-in follows the shift and says why when refused;
// the desk's coach list shows each coach's shift today and who is late.
public class ShiftCheckInTests
{
    sealed class FakeClock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    static readonly DateTime TenAm = new(2026, 11, 26, 2, 0, 0, DateTimeKind.Utc);   // 10:00 AM Manila
    static readonly DateOnly Day = new(2026, 11, 26);

    // the System admin gives Lena (u-coach1) a 10:30 AM - 2:00 PM shift today and Marco (front desk) none; both coaches start checked out
    static async Task<(DemoGateway Gw, FakeClock Clock, CoachService Coaches)> Open(bool enforced = true)
    {
        var clock = new FakeClock(TenAm);
        var gw = new DemoGateway(clock: clock);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var schedule = new ScheduleService(gw);
        Assert.True((await schedule.AddOverrideAsync("u-coach1", Day, Day, new TimeSpan(10, 30, 0), new TimeSpan(14, 0, 0), "Cover")).Ok);
        Assert.True((await schedule.SetEnforcedAsync(enforced)).Ok);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var coaches = new CoachService(gw);
        await coaches.CheckOutAsync("u-coach1"); await coaches.CheckOutAsync("u-coach2");
        return (gw, clock, coaches);
    }

    [Fact]
    public void The_refusals_read_as_sentences()
    {
        Assert.Equal("No shift today. Ask the admin to add one on the Scheduling page.", ErrorText.For(AppErrorKind.Validation, "no_shift_today"));
        Assert.Equal("The shift starts at 8:00 AM. Check in from 7:55 AM.", ErrorText.For(AppErrorKind.Validation, "too_early:8:00 AM"));
        Assert.Equal(AppErrorKind.Validation, ErrorMapper.Map(new Exception("too_early:8:00 AM")));
    }

    [Fact]
    public async Task A_coach_checks_in_from_5_minutes_before_the_shift_and_not_on_a_day_without_one()
    {
        var (_, clock, coaches) = await Open();
        Assert.Equal("too_early:10:30 AM", (await coaches.CheckInAsync("u-coach1")).Message);
        clock.Now = TenAm.AddMinutes(25);   // 10:25
        Assert.True((await coaches.CheckInAsync("u-coach1")).Ok);
        Assert.Equal("no_shift_today", (await coaches.CheckInAsync("u-coach2")).Message);
    }

    [Fact]
    public async Task With_the_switch_off_anyone_checks_in_and_front_desk_opens_a_drawer()
    {
        var (gw, _, coaches) = await Open(enforced: false);
        Assert.True((await coaches.CheckInAsync("u-coach2")).Ok);
        Assert.True((await new PaymentStaffService(gw).OpenShiftAsync(5000, null)).Ok);
    }

    [Fact]
    public async Task Front_desk_cannot_open_a_drawer_without_a_shift_but_an_admin_can()
    {
        var (gw, _, _) = await Open();
        Assert.Equal("no_shift_today", (await new PaymentStaffService(gw).OpenShiftAsync(5000, null)).Message);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).OpenShiftAsync(5000, null)).Ok);
    }

    [Fact]
    public async Task The_desk_sees_each_coach_shift_today_and_who_is_late()
    {
        var (_, clock, coaches) = await Open();
        var lena = (await coaches.PresenceAsync()).Value!.Single(r => r.CoachId == "u-coach1");
        Assert.Equal(("Shift 10:30 AM – 2:00 PM", false), (lena.ShiftText, lena.IsLate));
        Assert.Equal("No shift today", (await coaches.PresenceAsync()).Value!.Single(r => r.CoachId == "u-coach2").ShiftText);

        clock.Now = TenAm.AddMinutes(40);   // 10:40, not in yet
        Assert.True((await coaches.PresenceAsync()).Value!.Single(r => r.CoachId == "u-coach1").IsLate);
        await coaches.CheckInAsync("u-coach1");
        lena = (await coaches.PresenceAsync()).Value!.Single(r => r.CoachId == "u-coach1");
        Assert.True(lena.IsLate);   // checked in after the start
    }

    [Fact]
    public async Task A_coach_cannot_post_a_shift_any_more()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(1);
        await Assert.ThrowsAnyAsync<Exception>(() => gw.InsertAsync(new ShiftPost { CoachId = "u-coach1", Start = start, End = start.AddHours(2) }));
    }
}
