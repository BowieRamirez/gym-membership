using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 9 (D10): the coach dashboard posts a shift from the calendar (day tapped, start and 4 hours filled in, Post shift),
// cut short at closing time; rates are peso amounts above 0 that never depend on the device's decimal separator.
public class CoachDashboardTests
{
    static readonly TimeZoneInfo Manila = TimeZoneInfo.CreateCustomTimeZone("Manila", TimeSpan.FromHours(8), "Manila", "Manila");
    static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(3);
    static DateTime At(DateOnly d, int h, int m = 0) => DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(h, m)).AddHours(-8), DateTimeKind.Utc);

    [Theory]
    [InlineData("350", 350.00)]
    [InlineData(" 350.5 ", 350.50)]
    [InlineData("350,50", 350.50)]
    public void A_rate_is_read_without_the_device_separator(string text, double expected)
    {
        Assert.True(CoachRate.TryParse(text, out var rate));
        Assert.Equal((decimal)expected, rate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("350.505")]
    [InlineData("1,000.00")]
    [InlineData("abc")]
    public void Bad_rates_are_refused(string text) => Assert.False(CoachRate.TryParse(text, out _));

    [Fact]
    public void Rates_show_as_pesos_with_two_decimals_or_not_set()
    {
        Assert.Equal("₱1,250.50 per hour", CoachRate.Text(1250.5m));
        Assert.Equal("Rate not set", CoachRate.Text(null));
        Assert.Equal("Rate not set", CoachRate.Text(0));
        Assert.Equal("30.00", CoachRate.Input(30));
    }

    [Fact]
    public void The_default_start_is_opening_or_the_next_half_hour_today_and_the_end_stops_at_closing()
    {
        TimeSpan opens = TimeSpan.FromHours(6), closes = TimeSpan.FromHours(22);
        Assert.Equal(At(Day, 6), ShiftSlot.DefaultStart(Day, opens, closes, At(Day, 0)));
        Assert.Equal(At(Day, 14, 30), ShiftSlot.DefaultStart(Day, opens, closes, At(Day, 14, 5)));
        Assert.Equal(At(Day, 14), ShiftSlot.DefaultStart(Day, opens, closes, At(Day, 14)));
        Assert.Null(ShiftSlot.DefaultStart(Day, opens, closes, At(Day, 21, 40)));
        Assert.Equal(At(Day, 22), ShiftSlot.End(At(Day, 20), 4, closes));
        Assert.Equal(At(Day, 12), ShiftSlot.End(At(Day, 8), 4, closes));
    }

    static async Task<(AttendanceViewModel Vm, DemoGateway Gw)> Dashboard()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new AttendanceViewModel(new SessionService(gw), new AttendanceService(gw), new CoachService(gw), new MembershipService(gw)) { Zone = Manila, Focus = Day };
        await vm.OnAppearingAsync();
        vm.SelectDayCommand.Execute(vm.Days.Single(d => d.Date == Day));
        return (vm, gw);
    }

    [Fact]
    public async Task Tapping_a_day_fills_in_opening_time_and_four_hours_and_Post_shift_posts_it()
    {
        var (vm, gw) = await Dashboard();
        Assert.True(vm.CanPost);
        Assert.Equal(TimeSpan.FromHours(6), vm.PostTime);
        Assert.Equal(4, vm.PostHours);

        await vm.PostShiftCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var shift = (await gw.ListAsync<ShiftPost>(s => s.CoachId == "u-coach1" && s.Status == "scheduled")).Single(s => s.Start == At(Day, 6));
        Assert.Equal(At(Day, 10), shift.End);
        Assert.Single(vm.DayShifts, s => s.Id == shift.Id);

        await vm.PostShiftCommand.ExecuteAsync(null);   // the same start again overlaps
        Assert.Equal("You already have a shift at that time. Pick another start or fewer hours.", vm.ErrorMessage);
    }

    [Fact]
    public async Task A_shift_that_would_run_past_closing_ends_at_closing()
    {
        var (vm, gw) = await Dashboard();
        vm.PostTime = TimeSpan.FromHours(20);
        await vm.PostShiftCommand.ExecuteAsync(null);
        Assert.Equal("Shift posted until closing at 10:00 PM.", vm.Notice);
        Assert.Contains(await gw.ListAsync<ShiftPost>(s => s.CoachId == "u-coach1"), s => s.Start == At(Day, 20) && s.End == At(Day, 22));
    }

    [Fact]
    public async Task A_shift_cannot_start_in_the_past()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var yesterday = Day.AddDays(-4);
        var r = await new CoachService(gw).PostShiftAsync(At(yesterday, 8), At(yesterday, 10), null);
        Assert.Equal("That time has already started. Pick a later slot.", ErrorText.For(r.Error, r.Message));
    }

    [Fact]
    public async Task The_profile_needs_a_valid_rate_keeps_what_was_typed_and_shows_On_or_Off()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new CoachProfileViewModel(new CoachService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal("800.00", vm.RateInput);
        Assert.Equal("On", vm.HiringText);

        vm.RateInput = "0";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter an hourly rate above ₱0 with at most two decimals, for example 350 or 350.50.", vm.ErrorMessage);
        Assert.Equal("0", vm.RateInput);
        Assert.Null(vm.Notice);

        vm.RateInput = "950,5"; vm.IsAvailable = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Profile saved.", vm.Notice);
        Assert.Equal("950.50", vm.RateInput);
        Assert.Equal("Off", vm.HiringText);
        Assert.Equal(950.5m, (await gw.ListAsync<Coach>(c => c.UserId == "u-coach1")).Single().HourlyRate);
    }
}
