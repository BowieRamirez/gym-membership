using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner fixes 2026-11-25, Fix 3 Phase 3.5 (S9): the desk moves a booked session to another coach who is free for all of it.
public class ReassignSessionTests
{
    sealed class FakeClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    static readonly DateTime TenAm = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8).Date.AddHours(10 - 8), DateTimeKind.Utc);   // 10:00 AM Manila today
    static DateTime Manila(int h) => TenAm.AddHours(h - 10);

    static async Task<(DemoGateway Gw, HomeViewModel Vm, int SessionId)> Open()
    {
        var gw = new DemoGateway(clock: new FakeClock(TenAm));
        // Lena (u-coach1) has Alex at 2-3 PM; Kai (u-coach2) works 1-5 PM and is free then
        gw.OwnerAddCoachShift("u-coach1", Manila(13), Manila(17));
        gw.OwnerAddCoachShift("u-coach2", Manila(13), Manila(17));
        var s = gw.OwnerAddSession(new TrainingSession { CoachId = "u-coach1", MemberId = "u-alex", ScheduledStart = Manila(14), ScheduledEnd = Manila(15), Status = Status.Scheduled });
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();   // 0164: the front desk sells only during a started shift
        var sessions = new SessionService(gw);
        var vm = new HomeViewModel(new DeskHomeService(gw, new ChatService(gw, new NotificationService(gw))), new PaymentStaffService(gw), new CoachService(gw), sessions);
        await vm.OnAppearingAsync();
        return (gw, vm, s.Id);
    }

    [Fact]
    public async Task The_desk_picks_a_free_coach_and_the_session_moves_to_them()
    {
        var (gw, vm, id) = await Open();
        var row = vm.TodaySessions.Single(r => r.SessionId == id);
        Assert.True(row.CanChangeCoach);
        await vm.BeginChangeCoachCommand.ExecuteAsync(row);
        var kai = Assert.Single(vm.CoachChoices);   // Lena herself is not offered
        Assert.Equal("u-coach2", kai.CoachId);
        vm.ChosenCoach = kai; vm.ChangeCoachReason = "Lena is sick";
        await vm.SaveChangeCoachCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.ChangingSession);
        Assert.Equal("u-coach2", (await gw.ListAsync<TrainingSession>(t => t.Id == id)).Single().CoachId);
        Assert.Contains("Kai Mendoza", vm.Notice);
    }

    [Fact]
    public async Task A_reason_is_needed_and_no_free_coach_says_so()
    {
        var (gw, vm, id) = await Open();
        await vm.BeginChangeCoachCommand.ExecuteAsync(vm.TodaySessions.Single(r => r.SessionId == id));
        vm.ChosenCoach = vm.CoachChoices.Single();
        await vm.SaveChangeCoachCommand.ExecuteAsync(null);
        Assert.Equal("Say why the session is moving, like Lena is sick.", vm.ErrorMessage);

        gw.OwnerAddSession(new TrainingSession { CoachId = "u-coach2", MemberId = "u-bea", ScheduledStart = Manila(14), ScheduledEnd = Manila(15), Status = Status.Scheduled });
        await vm.BeginChangeCoachCommand.ExecuteAsync(vm.TodaySessions.Single(r => r.SessionId == id));
        Assert.Empty(vm.CoachChoices);
        Assert.Equal("No other coach is free for all of this session.", vm.NoCoachText);
    }
}
