using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

// 0131 (owner, 2026-11-21): a member check-in outside opening hours (6 am to 10 pm by default) is refused, like a coach's.
public class CheckInHoursTests
{
    sealed class Clock(int manilaHour) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.UtcNow.AddHours(8).Date.AddHours(manilaHour - 8), TimeSpan.Zero);
    }

    static async Task<AppResult<bool>> CheckInAt(int manilaHour)
    {
        var gw = new DemoGateway(clock: new Clock(manilaHour));
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        return await new AttendanceService(gw).CheckInAsync("u-alex", "manual", pin: "1234");
    }

    [Fact]
    public async Task Inside_opening_hours_the_member_is_checked_in() => Assert.True((await CheckInAt(12)).Ok);

    [Theory]
    [InlineData(5)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task Outside_opening_hours_it_is_refused_with_a_clear_message(int hour)
    {
        var r = await CheckInAt(hour);
        Assert.False(r.Ok);
        Assert.Contains("gym_closed", r.Message);
        Assert.Equal("The gym is closed at that time. Check-ins work during opening hours.", ErrorText.For(r.Error, r.Message));
    }
}
