using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// 0164 (owner, 2026-11-25): the front desk starts their shift from 5 minutes before it (server time), and sells nothing before.
public class FrontDeskShiftRuleTests
{
    static readonly DateTime Eight = new(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);   // 8:00 AM Manila
    static readonly ShiftWindow Desk = new() { Limited = true, StartsAt = Eight, EndsAt = Eight.AddHours(9) };

    [Theory]
    [InlineData(-6, false)]   // 7:54 refused
    [InlineData(-5, true)]    // 7:55 allowed
    [InlineData(0, true)]     // 8:00 allowed
    [InlineData(1, true)]     // 8:01 allowed, late
    public void Start_shift_opens_five_minutes_before_the_shift(int minutes, bool allowed) =>
        Assert.Equal(allowed, ShiftGate.At(Desk, Eight.AddMinutes(minutes)).CanStart);

    [Fact]
    public void The_wait_says_when_and_counts_down()
    {
        Assert.Equal("Your shift starts at 8:00 AM. You can start it from 7:55 AM, in 12:30.", ShiftGate.At(Desk, Eight.AddMinutes(-17.5)).Text);
        Assert.Equal("Your shift starts at 8:00 AM. You can start it from 7:55 AM, in 2 h 05 min.", ShiftGate.At(Desk, Eight.AddMinutes(-130)).Text);
        Assert.Equal("Your shift started at 8:00 AM; you're late.", ShiftGate.At(Desk, Eight.AddMinutes(3)).Text);
        Assert.Equal((false, "Your shift today ended at 5:00 PM."), ShiftGate.At(Desk, Eight.AddHours(9)));
        Assert.Equal((false, "No shift today. Ask the admin to add one on the Scheduling page."), ShiftGate.At(new ShiftWindow { Limited = true }, Eight));
        Assert.False(ShiftGate.At(new ShiftWindow { Limited = true, ClosedToday = true, StartsAt = Eight, EndsAt = Eight.AddHours(9) }, Eight).CanStart);
        Assert.Equal((true, null), ShiftGate.At(new ShiftWindow { Limited = false }, Eight.AddMinutes(-300)));   // admins
    }

    [Fact]
    public async Task Front_desk_sells_nothing_before_starting_their_shift_and_an_admin_is_not_limited()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        Assert.True(vm.NeedsShift);
        Assert.True(vm.SalesBlocked);
        Assert.False(vm.RecordCommand.CanExecute(null));
        var refused = await new PaymentStaffService(gw).RecordCounterAsync(PayKind.WalkIn, PayMethod.Cash, null, null, "No Shift", null, null);
        Assert.Equal("no_open_shift", refused.Message);

        await vm.StartShiftCommand.ExecuteAsync(null);
        Assert.False(vm.SalesBlocked);

        var admin = new DemoGateway();
        await admin.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var avm = new CashierViewModel(new PaymentStaffService(admin), new AttendanceService(admin));
        await avm.OnAppearingAsync();
        Assert.True(avm.NeedsShift);
        Assert.False(avm.SalesBlocked);
    }
}
