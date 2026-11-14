using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 6 of the 2026-11-12 plan (G13, G14, G4; 0064; approved mockup docs/mockups/amenity-down-payment.html) in demo mode.
// Alex has no Group classes pass; Lena (u-coach1) runs Group classes (coach fee ₱100) and has a shift tomorrow 6:00 to 14:00.
public class DownPaymentTests
{
    static readonly DateOnly Tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(1);
    static DateTime At(string hm) => DateTime.SpecifyKind(Tomorrow.ToDateTime(TimeOnly.Parse(hm)).AddHours(-8), DateTimeKind.Utc);

    static async Task<(DemoGateway Gw, BookAmenityViewModel Vm)> Member(string email = "member@demo.io", DemoGateway? gw = null)
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new BookAmenityViewModel(new ReservationService(gw), new AmenityService(gw), new ConfirmationTests.Nav(), new MembershipService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        return (gw, vm);
    }

    static async Task Pick(BookAmenityViewModel vm, string amenity, string hm, int minutes = 60)
    {
        await vm.OpenCommand.ExecuteAsync(vm.Cards.Single(c => c.Amenity.Name == amenity));
        await vm.PickDayCommand.ExecuteAsync(vm.Days.Single(d => d.Date == Tomorrow));
        vm.PickTimeCommand.Execute(vm.Times.Single(t => t.StartUtc == At(hm)));
        vm.PickLengthCommand.Execute(vm.Lengths.Single(l => l.Minutes == minutes));
    }

    static async Task MakeCarloActive(DemoGateway gw)
    {
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var monthly = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        Assert.True((await new PaymentStaffService(gw).RecordCounterAsync("membership", "cash", "u-carlo", monthly.Id, null, null, null)).Ok);
    }

    [Fact]
    public void The_split_is_price_plus_coach_fee_less_the_code_then_the_percent_now_and_the_rest_at_the_desk()
    {
        Assert.Equal(new PriceSplit(250, 37.50m, 212.50m, 106.25m, 106.25m), BookingPrice.Split(150, 100, true, 50, 15));
        Assert.Equal(new PriceSplit(150, 0, 150, 75, 75), BookingPrice.Split(150, 100, false, 50));
        Assert.Equal(new PriceSplit(200, 0, 200, 60, 140), BookingPrice.Split(200, 0, true, 30));
        Assert.Equal(0.01m, BookingPrice.Split(0.01m, 0, false, 50).Down + BookingPrice.Split(0.01m, 0, false, 50).Balance);
    }

    [Fact]
    public async Task With_a_coach_lists_the_free_coaches_and_books_with_the_fee_and_the_down_payment()
    {
        var (gw, vm) = await Member();
        await Pick(vm, "Group classes", "10:00");
        Assert.Equal(50, vm.Percent);
        Assert.Equal((500m, 250m), (vm.Split.Due, vm.Split.Down));
        vm.WithCoachCommand.Execute(null);
        await vm.LoadCoachesCommand.ExecuteAsync(null);
        var lena = Assert.Single(vm.Coaches);
        Assert.Equal("Lena Cruz", lena.Name);
        Assert.False(vm.CanBook);                                // with a coach needs one picked
        vm.PickCoachCommand.Execute(lena);
        Assert.True(vm.CanBook);
        Assert.Equal((600m, 300m, 300m), (vm.Split.Due, vm.Split.Down, vm.Split.Balance));
        Assert.Equal("Book and pay ₱300.00", vm.BookText);
        await vm.BookCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        var row = vm.Bookings.Single(b => b.Amenity == "Group classes");
        Assert.Equal(("awaiting_payment", "u-coach1", 600m, 300m, 300m), (row.Booking.Status, row.Booking.CoachId, row.Booking.TotalPrice, row.Booking.DownPayment, row.Payment!.Amount));
        Assert.Equal("Waiting for down payment", row.StatusText);

        await vm.OpenCommand.ExecuteAsync(vm.Cards.Single(c => c.Amenity.Name == "Group classes"));   // a request holds nothing...
        await vm.PickDayCommand.ExecuteAsync(vm.Days.Single(d => d.Date == Tomorrow));
        Assert.True(vm.Times.Single(t => t.StartUtc == At("12:00")).CanPick);
    }

    [Fact]
    public async Task Of_two_paid_requests_for_one_slot_the_first_verified_wins_and_the_other_is_refunded()
    {
        var gw = new DemoGateway();
        await MakeCarloActive(gw);
        var (_, alex) = await Member(gw: gw);
        await Pick(alex, "Group classes", "10:00");
        await alex.BookCommand.ExecuteAsync(null);
        var (_, carlo) = await Member("carlo@demo.io", gw);
        await Pick(carlo, "Group classes", "10:00");
        await carlo.BookCommand.ExecuteAsync(null);
        Assert.Null(carlo.ErrorMessage);

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new PaymentStaffService(gw);
        var pays = (await gw.ListAsync<Payment>(p => p.Status == Status.Pending && p.Kind == PayKind.Amenity)).OrderBy(p => p.Id).ToList();
        Assert.True((await desk.VerifyAsync(pays[1].Id, true)).Ok);   // Carlo's first
        Assert.True((await desk.VerifyAsync(pays[0].Id, true)).Ok);   // Alex paid, but the slot is gone

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var row = (await new ReservationService(gw).MyBookingsAsync()).Value!.Single(b => b.Amenity == "Group classes");
        Assert.Equal(("cancelled", "Slot taken", "approved"), (row.Booking.Status, row.Booking.CancelReason, row.Refund!.Status));
        Assert.Equal("Slot taken · refund approved", row.StatusText);
    }

    [Fact]
    public async Task The_desk_takes_the_balance_once_from_Amenity_status()
    {
        var (gw, vm) = await Member();
        await Pick(vm, "Group classes", "10:00");
        await vm.BookCommand.ExecuteAsync(null);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var pay = (await gw.ListAsync<Payment>(p => p.Status == Status.Pending && p.Kind == PayKind.Amenity)).Single();
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);

        var status = new AmenityStatusViewModel(new AmenityService(gw), new ReservationService(gw));
        await status.LoadBalancesAsync(Tomorrow);
        var due = Assert.Single(status.Balances);
        Assert.Equal(("Alex Tan", "Group classes", "₱250.00"), (due.Member, due.Amenity, due.BalanceText));
        await status.TakeBalanceCommand.ExecuteAsync(due);
        Assert.Null(status.ErrorMessage);
        Assert.Empty(status.Balances);
        var booking = (await gw.ListAsync<Reservation>(r => r.MemberId == "u-alex")).Single(r => r.TotalPrice == 500);
        Assert.NotNull(booking.BalancePaymentId);
        Assert.Equal("already_processed", (await new ReservationService(gw).TakeBalanceAsync(booking.Id, PayMethod.Cash, null)).Message);
    }

    [Fact]
    public async Task Cancelling_within_24_hours_says_the_down_payment_is_kept()
    {
        var (gw, vm) = await Member();
        await Pick(vm, "Group classes", "10:00");
        await vm.BookCommand.ExecuteAsync(null);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var pay = (await gw.ListAsync<Payment>(p => p.Status == Status.Pending && p.Kind == PayKind.Amenity)).Single();
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Bookings.Single(b => b.Amenity == "Group classes");
        var refundable = row.Booking.StartsAt.ToUniversalTime().AddHours(-24) >= DateTime.UtcNow;
        Assert.Contains(refundable ? "to get the ₱250.00 down payment back" : "keeps the ₱250.00 down payment", row.Detail);
        Assert.True(row.CanCancel);
    }
}
