using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class PackageCoachTests
{
    [Fact]
    public async Task Package_coach_included_clears_addon_and_carries_on_new_version()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var adminSvc = new AdminService(gw);
        var vm = new CatalogAdminViewModel(adminSvc);
        await vm.OnAppearingAsync();

        // 1. Add a package with Coach add-on price
        vm.AddPackageCommand.Execute(null);
        vm.FormName = "Starter Coach";
        vm.FormDescription = "Monthly with add-on";
        vm.FormPrice = 1800m;
        vm.FormDays = 30;
        vm.FormCoachAddonPrice = "250";
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);

        var saved = (await gw.ListAsync<MembershipPackage>(p => p.Name == "Starter Coach")).Single();
        Assert.False(saved.IncludesCoach);
        Assert.Equal(250m, saved.CoachAddonPrice);
        Assert.Equal("Add a coach: ₱250", saved.CoachText);

        // Publish and create a new version: add-on price carries over
        await adminSvc.PublishAsync("plan", saved.Id);
        var v2Id = (await adminSvc.NewPackageVersionAsync(saved.Id)).Value;
        var v2 = (await gw.ListAsync<MembershipPackage>(p => p.Id == v2Id)).Single();
        Assert.False(v2.IncludesCoach);
        Assert.Equal(250m, v2.CoachAddonPrice);

        // 2. Toggle Coach included on: clears add-on price
        vm.AddPackageCommand.Execute(null);
        vm.FormName = "VIP Coach";
        vm.FormDescription = "All-in";
        vm.FormPrice = 4000m;
        vm.FormDays = 30;
        vm.FormCoachAddonPrice = "300";
        vm.FormIncludesCoach = true;
        Assert.Equal("", vm.FormCoachAddonPrice);
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);

        var vip = (await gw.ListAsync<MembershipPackage>(p => p.Name == "VIP Coach")).Single();
        Assert.True(vip.IncludesCoach);
        Assert.Null(vip.CoachAddonPrice);
        Assert.Equal("Coach included", vip.CoachText);
    }

    [Fact]
    public async Task GymSettings_saves_coach_limits_and_blank_daily_max()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new GymSettingsViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();

        Assert.Equal(1, vm.MaxCoachSessionsPerDay);
        Assert.Equal(1, vm.MaxUpcomingCoachSessions);
        Assert.Equal("6", vm.MaxCoachHoursPerDay);

        vm.MaxCoachSessionsPerDay = 2;
        vm.MaxUpcomingCoachSessions = 4;
        vm.MaxCoachHoursPerDay = "";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Saved.", vm.Notice);

        var updated = (await gw.ListAsync<GymSettings>()).Single();
        Assert.Equal(2, updated.MaxCoachSessionsPerDay);
        Assert.Equal(4, updated.MaxUpcomingCoachSessions);
        Assert.Null(updated.MaxCoachHoursPerDay);
    }
}
