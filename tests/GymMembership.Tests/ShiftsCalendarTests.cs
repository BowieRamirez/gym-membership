using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class ShiftsCalendarTests
{
    [Fact]
    public async Task My_shifts_loads_the_calendar_tracking_booked_sessions_and_shifts()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new ShiftsViewModel(new CoachService(gw), new SessionService(gw), new AttendanceService(gw), new MembershipService(gw));
        await vm.OnAppearingAsync();

        Assert.NotNull(vm.Calendar);
        Assert.Equal(42, vm.Calendar!.Days.Count);
        Assert.Contains(vm.Calendar.Days, d => d.Entry1 is not null);
    }

    [Fact]
    public async Task Dashboard_loads_the_amenities_the_coach_runs()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new AttendanceViewModel(new SessionService(gw), new AttendanceService(gw), new CoachService(gw), new MembershipService(gw), new AmenityService(gw));
        await vm.OnAppearingAsync();

        Assert.True(vm.HasAmenities);
        Assert.Contains("Group classes", vm.MyAmenities);
    }
}
