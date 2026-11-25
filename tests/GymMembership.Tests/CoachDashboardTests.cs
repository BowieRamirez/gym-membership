using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 9 (D10): the coach profile; rates are peso amounts above 0 that never depend on the device's decimal separator.
// (Posting a shift from the calendar went with Fix 3, 0151: the gym sets coaches' shifts.)
public class CoachDashboardTests
{
    static readonly TimeZoneInfo Manila = TimeZoneInfo.CreateCustomTimeZone("Manila", TimeSpan.FromHours(8), "Manila", "Manila");
    static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(3);
    static DateTime At(DateOnly d, int h, int m = 0) => DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(h, m)).AddHours(-8), DateTimeKind.Utc);

    [Fact]
    public async Task The_profile_saves_bio_specialty_and_shows_On_or_Off()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new CoachProfileViewModel(new CoachService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal("On", vm.HiringText);

        vm.IsAvailable = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Profile saved.", vm.Notice);
        Assert.Equal("Off", vm.HiringText);
    }
}
