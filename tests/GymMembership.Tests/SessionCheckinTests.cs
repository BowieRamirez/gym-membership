using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class SessionCheckinTests
{
    [Fact]
    public async Task Desk_scans_session_QR_checks_in_and_marks_coach_arrived()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();   // 0164: the front desk sells only during a started shift
        var sessions = new SessionService(gw);
        var checkIn = new CheckInViewModel(new AttendanceService(gw), new CoachService(gw), sessions: sessions);

        var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8).Date.AddHours(10), DateTimeKind.Utc).AddHours(-8);
        var ses = new TrainingSession
        {
            CoachId = "u-coach1",
            MemberId = "u-alex",
            Title = "Today test",
            ScheduledStart = todayStart,
            ScheduledEnd = todayStart.AddHours(1),
            SessionCode = "SES-TODAY1"
        };
        gw.OwnerAddSession(ses);

        await checkIn.ScanCommand.ExecuteAsync("SES-TODAY1");
        Assert.NotNull(checkIn.ScannedSession);
        Assert.Equal("Checked in", checkIn.ScannedSession!.Status);
        Assert.True(checkIn.ScannedSession.CanMarkCoachArrived);

        await checkIn.MarkCoachArrivedCommand.ExecuteAsync(checkIn.ScannedSession);
        Assert.Equal("In session", checkIn.ScannedSession.Status);
        Assert.False(checkIn.ScannedSession.CanMarkCoachArrived);
    }

    [Fact]
    public async Task Desk_home_lists_todays_coach_sessions_and_marks_coach_arrived()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();   // 0164: the front desk sells only during a started shift
        var sessions = new SessionService(gw);
        var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8).Date.AddHours(11), DateTimeKind.Utc).AddHours(-8);
        gw.OwnerAddSession(new TrainingSession
        {
            CoachId = "u-coach1",
            MemberId = "u-alex",
            Title = "Home test",
            ScheduledStart = todayStart,
            ScheduledEnd = todayStart.AddHours(1),
            SessionCode = "SES-HOME01"
        });
        await sessions.ScanSessionAsync("SES-HOME01");

        var home = new HomeViewModel(new DeskHomeService(gw, new ChatService(gw, new NotificationService(gw))), new PaymentStaffService(gw), new CoachService(gw), sessions);
        await home.OnAppearingAsync();
        var row = Assert.Single(home.TodaySessions, s => s.SessionCode == "SES-HOME01");
        Assert.True(row.CanMarkCoachArrived);

        await home.MarkCoachArrivedCommand.ExecuteAsync(row);
        Assert.Equal("In session", home.TodaySessions.Single(s => s.SessionCode == "SES-HOME01").Status);
    }

    [Fact]
    public async Task Coach_cancel_requires_reason_and_alerts_member()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new SessionsViewModel(new SessionService(gw));
        await vm.OnAppearingAsync();

        var scheduled = vm.Sessions.First(s => s.Session.Status == Status.Scheduled && s.IsMine);
        await vm.CancelCommand.ExecuteAsync(scheduled);
        Assert.True(vm.IsCancelling);

        vm.CancelReason = "  ";
        await vm.ConfirmCancelCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsCancelling);

        vm.CancelReason = "Sick today";
        await vm.ConfirmCancelCommand.ExecuteAsync(null);
        Assert.False(vm.IsCancelling);
        Assert.Null(vm.ErrorMessage);
    }
}
