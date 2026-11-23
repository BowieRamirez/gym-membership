using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 8 (D9) in demo mode: Lena (coach@demo.io, hired by Alex) and Kai (kai@demo.io) start checked in.
// Absent coaches leave the "find a coach" list unless they are your coach; a new hire needs the coach in the gym.
public class CoachPresenceTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(DateTime.UtcNow.AddHours(8).Date.AddHours(12 - 8), TimeSpan.Zero);   // noon in Manila, gym open
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static async Task<(DemoGateway Gw, CoachService Svc, Clock Clock)> Setup(string email)
    {
        var clock = new Clock();
        var gw = new DemoGateway(clock: clock);
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new CoachService(gw), clock);
    }

    static async Task<IReadOnlyList<CoachCard>> List(DemoGateway gw, CoachService svc)
    {
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        return (await svc.ListCoachesAsync()).Value!;
    }

    [Fact]
    public async Task A_coach_who_checks_out_stays_listed_as_away_and_can_be_hired_again_once_in()
    {
        // owner, 2026-11-15 (replaces D9's hiding): every active coach is listed, the ones in the gym first, so members can plan around shifts
        var (gw, svc, _) = await Setup("employee@demo.io");   // 0140: the front desk checks coaches in and out
        Assert.True((await svc.CheckOutAsync("u-coach2")).Ok);
        var away = await List(gw, svc);
        var kai = away.Single(c => c.Name == "Kai Mendoza");
        Assert.False(kai.InGym); Assert.True(kai.CanHire);   // G29: hire any time
        Assert.True(away.TakeWhile(c => c.InGym).Count() == away.Count(c => c.InGym));   // in the gym first

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.CheckInAsync("u-coach2")).Ok);
        Assert.True((await svc.CheckInAsync("u-coach2")).Ok);   // twice is harmless
        Assert.Single(await gw.ListAsync<CoachVisit>(v => v.CoachId == "u-coach2" && v.CheckedOutAt == null));
        Assert.True((await List(gw, svc)).Single(c => c.Name == "Kai Mendoza").InGym);
    }

    [Fact]
    public async Task Your_own_coach_stays_listed_when_away_and_an_away_coach_can_still_be_hired()
    {
        var (gw, svc, _) = await Setup("employee@demo.io");
        Assert.True((await svc.CheckOutAsync("u-coach1")).Ok);
        Assert.True((await svc.CheckOutAsync("u-coach1")).Ok);   // twice is harmless
        var lena = (await List(gw, svc)).Single(c => c.Name == "Lena Cruz");
        Assert.True(lena.Hired); Assert.False(lena.InGym);

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.CheckOutAsync("u-coach2")).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.HireAsync("u-coach2")).Ok);   // G29 (0072): the deal is between member and coach, any time
    }

    [Fact]
    public async Task The_front_desk_checks_coaches_in_and_out_and_members_cannot()
    {
        var (gw, svc, _) = await Setup("employee@demo.io");
        var vm = new CheckInViewModel(new AttendanceService(gw), svc);
        await vm.OnAppearingAsync();
        var kai = vm.Coaches.Single(c => c.Name == "Kai Mendoza");
        Assert.True(kai.InGym); Assert.Equal("Check out", kai.ActionText);

        await vm.ToggleCoachCommand.ExecuteAsync(kai);
        Assert.Equal("Kai Mendoza is checked out.", vm.Notice);
        Assert.False(vm.Coaches.Single(c => c.Name == "Kai Mendoza").InGym);
        Assert.Equal("u-emp", (await gw.ListAsync<CoachVisit>(v => v.CoachId == "u-coach2")).Single().CheckedOutBy);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.CheckInAsync("u-coach2")).Error);
    }

    [Fact]
    public async Task Check_in_is_refused_while_the_gym_is_closed()
    {
        var (_, svc, clock) = await Setup("employee@demo.io");
        Assert.True((await svc.CheckOutAsync("u-coach2")).Ok);
        clock.Now = clock.Now.AddHours(11);   // 23:00 Manila, after the 22:00 closing
        var r = await svc.CheckInAsync("u-coach2");
        Assert.Equal(AppErrorKind.Validation, r.Error);
        Assert.Equal("The gym is closed at that time. Check-ins work during opening hours.", ErrorText.For(r.Error, r.Message));
    }

    [Fact]
    public async Task A_coach_cannot_check_themselves_in_or_out()
    {
        // 0140 (live test 2026-11-23): the coach Settings card went on 2026-11-22; the server now refuses the coach's own call too
        var (gw, svc, _) = await Setup("coach@demo.io");
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.CheckOutAsync()).Error);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.CheckInAsync()).Error);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.CheckOutAsync("u-coach1")).Error);
        Assert.Single(await gw.ListAsync<CoachVisit>(v => v.CoachId == "u-coach1" && v.CheckedOutAt == null));   // still in
    }
}

// G28 (owner, 2026-11-15; 0071): the Coaches page tags a hired coach; a coach can still take more members.
public class CoachOneMemberTests
{
    [Fact]
    public async Task A_coach_hired_by_someone_else_is_not_tagged_and_can_still_be_hired()
    {
        var (gw, _, subs, monthly, _) = await SubscriptionTests.Setup();   // a new member, not Alex who hired Lena
        var p = (await subs.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var svc = new CoachService(gw);
        var cards = (await svc.ListCoachesAsync()).Value!;
        var lena = cards.Single(c => c.Name == "Lena Cruz");
        Assert.True(lena.Taken); Assert.False(lena.Hired); Assert.Null(lena.HireBadge);   // someone else's hire: no tag for this member
        var kai = cards.Single(c => c.Name == "Kai Mendoza");
        Assert.False(kai.Taken); Assert.Null(kai.HireBadge); Assert.True(kai.CanHire);

        Assert.True((await svc.HireAsync(lena.Coach.UserId)).Ok);   // many members per coach
        await HireRequestTests.Accept(gw, "coach@demo.io", "phase7@demo.io", "secret12");   // G30: the coach accepts
        var mine = (await svc.ListCoachesAsync()).Value!.Single(c => c.Name == "Lena Cruz");
        Assert.True(mine.Hired); Assert.False(mine.Taken);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex sees their own coach as hired, not taken
        var own = (await svc.ListCoachesAsync()).Value!.Single(c => c.Name == "Lena Cruz");
        Assert.True(own.Hired); Assert.False(own.Taken); Assert.Equal("hired", own.HireBadge);
    }
}
