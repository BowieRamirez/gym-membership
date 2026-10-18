using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class CalendarServiceTests
{
    static async Task<(DemoGateway Gw, SessionService Svc)> As(string email)
    {
        var gw = new DemoGateway(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new SessionService(gw));
    }

    [Fact]
    public async Task A_coach_gets_their_sessions_in_the_range_with_the_marks_and_names()
    {
        var (_, svc) = await As("coach@demo.io");
        var rows = (await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!;
        Assert.True(rows.Count >= 9);
        Assert.Equal(rows.OrderBy(r => r.Session.ScheduledStart).Select(r => r.Session.Id), rows.Select(r => r.Session.Id));   // oldest first
        Assert.Contains(rows, r => r.Mark == "present"); Assert.Contains(rows, r => r.Mark == "late");
        Assert.Contains(rows, r => r.Mark == "absent"); Assert.Contains(rows, r => r.Mark == "excused");
        Assert.Contains(rows, r => r.Cancelled);
        Assert.All(rows, r => Assert.NotEqual("Unknown", r.With));
        Assert.Contains(rows, r => r.With == "Alex Tan");
    }

    [Fact]
    public async Task The_range_is_start_inclusive_and_end_exclusive()
    {
        var (_, svc) = await As("coach@demo.io");
        var all = (await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!;
        var one = all.First(r => r.Mark == "present");
        var from = one.Session.ScheduledStart;
        Assert.Contains((await svc.CalendarAsync(from, from.AddMinutes(1))).Value!, r => r.Session.Id == one.Session.Id);          // starts exactly at the start
        Assert.DoesNotContain((await svc.CalendarAsync(from.AddMinutes(-1), from)).Value!, r => r.Session.Id == one.Session.Id);   // the end is exclusive
    }

    [Fact]
    public async Task Someone_who_is_not_a_coach_gets_nothing()
    {
        var (_, svc) = await As("member@demo.io");
        Assert.Empty((await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!);
        Assert.Empty((await svc.UpcomingAsync()).Value!);
    }

    [Fact]
    public async Task Another_coach_does_not_see_this_coachs_sessions()
    {
        var (_, svc) = await As("kai@demo.io");
        Assert.Empty((await svc.CalendarAsync(DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(10))).Value!);
    }

    [Fact]
    public async Task Upcoming_is_only_scheduled_sessions_from_now_on_nearest_first()
    {
        var (_, svc) = await As("coach@demo.io");
        var up = (await svc.UpcomingAsync()).Value!;
        Assert.NotEmpty(up);
        Assert.All(up, r => { Assert.Equal("scheduled", r.Session.Status); Assert.True(r.Session.ScheduledStart >= DateTime.UtcNow.AddMinutes(-1)); Assert.Null(r.Mark); });
        Assert.Equal(up.OrderBy(r => r.Session.ScheduledStart).Select(r => r.Session.Id), up.Select(r => r.Session.Id));
    }

    [Fact]
    public async Task Marking_a_session_shows_up_in_the_next_load()
    {
        var (gw, svc) = await As("coach@demo.io");
        var target = (await svc.UpcomingAsync()).Value!.First().Session;
        Assert.True((await new AttendanceService(gw).RecordAsync(target.Id, "late")).Ok);
        var rows = (await svc.CalendarAsync(target.ScheduledStart.AddMinutes(-1), target.ScheduledStart.AddMinutes(1))).Value!;
        Assert.Equal("late", rows.Single(r => r.Session.Id == target.Id).Mark);
    }
}
