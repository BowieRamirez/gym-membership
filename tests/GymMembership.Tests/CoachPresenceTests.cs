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
    public async Task A_coach_who_checks_out_leaves_the_list_and_returns_after_checking_in()
    {
        var (gw, svc, _) = await Setup("kai@demo.io");
        Assert.True((await svc.CheckOutAsync()).Ok);
        Assert.DoesNotContain(await List(gw, svc), c => c.Name == "Kai Mendoza");

        await gw.SignInAsync("kai@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.CheckInAsync()).Ok);
        Assert.True((await svc.CheckInAsync()).Ok);   // twice is harmless
        Assert.Single(await gw.ListAsync<CoachVisit>(v => v.CoachId == "u-coach2" && v.CheckedOutAt == null));
        Assert.True((await List(gw, svc)).Single(c => c.Name == "Kai Mendoza").InGym);
    }

    [Fact]
    public async Task Your_own_coach_stays_listed_when_away_and_a_new_hire_needs_the_coach_in()
    {
        var (gw, svc, _) = await Setup("coach@demo.io");
        Assert.True((await svc.CheckOutAsync()).Ok);
        Assert.True((await svc.CheckOutAsync()).Ok);   // twice is harmless
        var lena = (await List(gw, svc)).Single(c => c.Name == "Lena Cruz");
        Assert.True(lena.Hired); Assert.False(lena.InGym);

        await gw.SignInAsync("kai@demo.io", DemoGateway.DemoPassword);
        Assert.True((await svc.CheckOutAsync()).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var hire = await svc.HireAsync("u-coach2");   // a stale list still showing Kai
        Assert.Equal("This coach just left the gym. You can hire them when they are back.", ErrorText.For(hire.Error, hire.Message));
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
        var (_, svc, clock) = await Setup("kai@demo.io");
        Assert.True((await svc.CheckOutAsync()).Ok);
        clock.Now = clock.Now.AddHours(11);   // 23:00 Manila, after the 22:00 closing
        var r = await svc.CheckInAsync();
        Assert.Equal(AppErrorKind.Validation, r.Error);
        Assert.Equal("The gym is closed right now. Coaches can check in during opening hours.", ErrorText.For(r.Error, r.Message));
    }

    [Fact]
    public async Task The_coach_profile_shows_and_toggles_the_visit()
    {
        var (_, svc, _) = await Setup("coach@demo.io");
        var vm = new CoachProfileViewModel(svc);
        await vm.OnAppearingAsync();
        Assert.True(vm.InGym); Assert.Equal("Check out", vm.VisitButton);

        await vm.ToggleVisitCommand.ExecuteAsync(null);
        Assert.False(vm.InGym); Assert.Equal("Check in", vm.VisitButton);
        Assert.Equal("You are checked out.", vm.Notice);
    }
}
