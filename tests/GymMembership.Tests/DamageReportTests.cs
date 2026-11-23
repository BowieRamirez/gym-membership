using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class DamageReportTests
{
    [Fact]
    public async Task Coach_completes_session_gets_prompt_and_submits_damage_report_for_admin_review()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var sessions = new SessionService(gw);
        var vm = new SessionsViewModel(sessions);
        await vm.OnAppearingAsync();

        var past = new TrainingSession
        {
            CoachId = "u-coach1",
            MemberId = "u-alex",
            Title = "Past session",
            ScheduledStart = DateTime.UtcNow.AddHours(-2),
            ScheduledEnd = DateTime.UtcNow.AddHours(-1),
            Status = Status.Scheduled
        };
        gw.OwnerAddSession(past);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Sessions.Single(s => s.Session.Id == past.Id);

        await vm.CompleteCommand.ExecuteAsync(row);
        Assert.True(vm.IsReportingDamage);

        vm.StartDamageReportCommand.Execute(null);
        Assert.True(vm.DamageFound);
        vm.DamageDescription = "Broken dumbbell handle";
        await vm.SubmitDamageCommand.ExecuteAsync(null);
        Assert.False(vm.IsReportingDamage);
        Assert.Null(vm.ErrorMessage);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var adminVm = new DamageReportsViewModel(new AdminService(gw));
        await adminVm.OnAppearingAsync();
        var rep = Assert.Single(adminVm.Reports, r => r.Report.Description == "Broken dumbbell handle");
        Assert.Equal("open", rep.Report.Status);

        adminVm.StartReviewCommand.Execute(rep);
        adminVm.ReviewNote = "Replaced handle";
        await adminVm.CloseReportCommand.ExecuteAsync(null);
        Assert.False(adminVm.IsReviewing);
        adminVm.SetFilterCommand.Execute("closed");
        Assert.Contains(adminVm.Reports, r => r.Report.Id == rep.Report.Id && r.Report.ReviewNote == "Replaced handle");
    }
}
