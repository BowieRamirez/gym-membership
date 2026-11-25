using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// 0165 (owner, 2026-11-25): a coach reports damage any time from their own page, for an amenity or something else.
public class ReportDamageTests
{
    static async Task<(DemoGateway Gw, ReportDamageViewModel Vm)> Coach()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new ReportDamageViewModel(new SessionService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task A_coach_reports_damage_with_no_session_and_sees_it_listed_as_open()
    {
        var (gw, vm) = await Coach();
        Assert.Equal(ReportDamageViewModel.SomethingElse, vm.Places[^1].Name);
        Assert.False(vm.SendCommand.CanExecute(null));   // where and what first
        vm.Where = vm.Places[0];
        vm.Description = "Ring rope is frayed near the corner";
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("", vm.Description);
        var sent = Assert.Single(vm.Reports);
        Assert.Equal((vm.Places[0].Name, "Ring rope is frayed near the corner", "Open"), (sent.Where, sent.What, sent.Status));
        var row = (await gw.ListAsync<DamageReport>()).Single(r => r.Description.StartsWith("Ring rope"));
        Assert.Equal(("u-coach1", (int?)null), (row.CoachId, row.TrainingSessionId));
    }

    [Fact]
    public async Task Something_else_needs_no_amenity_and_drafts_are_not_offered()
    {
        var (gw, vm) = await Coach();
        Assert.All(vm.Places.Where(p => p.AmenityId is { }), p =>
            Assert.Equal(CatalogState.Published, gw.ListAsync<Amenity>().Result.Single(a => a.Id == p.AmenityId).State));
        vm.Where = vm.Places[^1];
        vm.Description = "Dumbbell rack wobbles";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("Something else", Assert.Single(vm.Reports).Where);
    }

    [Fact]
    public async Task A_member_cannot_report_damage()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.Equal("forbidden", (await new SessionService(gw).ReportDamageAsync(null, null, "x")).Message);
    }
}
