using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CoachHoursReportTests
{
    [Fact]
    public async Task Coach_hours_counts_completed_sessions_and_hours_only_with_no_shows_and_cancels_apart()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var todayStart = DateTime.SpecifyKind(today.ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Utc).AddHours(-8);

        gw.OwnerAddSession(new TrainingSession
        {
            CoachId = "u-coach2",
            MemberId = "u-alex",
            Title = "Completed 2h",
            ScheduledStart = todayStart,
            ScheduledEnd = todayStart.AddHours(2),
            Status = Status.Completed
        });
        gw.OwnerAddSession(new TrainingSession
        {
            CoachId = "u-coach2",
            MemberId = "u-alex",
            Title = "No-show 1h",
            ScheduledStart = todayStart.AddHours(2),
            ScheduledEnd = todayStart.AddHours(3),
            Status = Status.NoShow
        });
        gw.OwnerAddSession(new TrainingSession
        {
            CoachId = "u-coach2",
            MemberId = "u-alex",
            Title = "Cancelled 1h",
            ScheduledStart = todayStart.AddHours(3),
            ScheduledEnd = todayStart.AddHours(4),
            Status = Status.Cancelled
        });

        var vm = new RevenueViewModel(new AdminService(gw)) { From = today.ToDateTime(TimeOnly.MinValue), To = today.ToDateTime(TimeOnly.MinValue) };
        await vm.LoadCommand.ExecuteAsync(null);
        var kai = Assert.Single(vm.CoachHours, c => c.CoachId == "u-coach2");
        Assert.Equal(1, kai.CompletedSessions);
        Assert.Equal(2m, kai.CompletedHours);
        Assert.Equal(1, kai.NoShows);
        Assert.Equal(1, kai.CoachCancelled);
    }

    [Fact]
    public async Task DiscountsViewModel_can_target_coach_addon_and_AuditViewModel_has_coach_addon_filter()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var discounts = new DiscountsViewModel(new AdminService(gw));
        await discounts.OnAppearingAsync();
        discounts.ForCoachAddon = true;
        var item = Assert.Single(discounts.ChosenItems());
        Assert.Equal("coach", item.Kind);
        Assert.Null(item.ItemId);

        var audit = new AuditViewModel(new AdminService(gw));
        Assert.Contains("Coach add-on", audit.Whats);
    }
}
