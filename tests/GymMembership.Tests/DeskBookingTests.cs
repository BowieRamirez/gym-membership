using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Plan 2026-11-17 Z1, Z2 (0087): on Amenity status the desk finds or registers a customer, books a slot and takes the full price in cash.</summary>
public class DeskBookingTests
{
    static async Task<(DemoGateway Gw, DeskBookingViewModel Vm)> Open(bool shift = true)
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        if (shift) await gw.OpenDrawerAsync();
        var vm = new DeskBookingViewModel(new AttendanceService(gw), new ReservationService(gw), new PaymentStaffService(gw));
        vm.Day = DateTime.Today.AddDays(2);
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task Pick(DeskBookingViewModel vm, string who)
    {
        vm.Search = who; await vm.FindCommand.ExecuteAsync(null);
        vm.PickMemberCommand.Execute(vm.Hits.First());
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        vm.Start = vm.Starts.First();
        vm.Minutes = vm.Lengths.First();
    }

    [Fact]
    public async Task Desk_finds_a_member_and_books_a_slot_paid_in_full_in_cash()
    {
        var (gw, vm) = await Open();
        await Pick(vm, "carlo");
        var start = vm.Start!.Utc;
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.StartsWith("Booked Pool for Carlo Diaz", vm.Notice);
        var r = (await gw.ListAsync<Reservation>(x => x.MemberId == "u-carlo")).Single(x => x.StartsAt == start);
        Assert.Equal("confirmed", r.Status);
        Assert.Equal(r.TotalPrice, r.DownPayment);
        var pay = (await gw.ListAsync<Payment>(p => p.Id == r.PaymentId)).Single();
        Assert.Equal((Status.Verified, PayMethod.Cash), (pay.Status, pay.Method));
    }

    [Fact]
    public async Task A_booked_slot_is_no_longer_offered()
    {
        var (_, vm) = await Open();
        await Pick(vm, "carlo");
        var start = vm.Start!.Utc;
        await vm.BookCommand.ExecuteAsync(null);
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        Assert.DoesNotContain(vm.Starts, s => s.Utc == start);
    }

    [Fact]
    public async Task Cash_without_a_shift_says_start_one_and_books_nothing()
    {
        var (gw, vm) = await Open(shift: false);
        var before = (await gw.ListAsync<Reservation>()).Count;
        await Pick(vm, "carlo");
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Contains("cash shift", vm.ErrorMessage);
        Assert.Equal(before, (await gw.ListAsync<Reservation>()).Count);
    }

    [Fact]
    public async Task Book_waits_for_a_customer_amenity_time_and_length()
    {
        var (_, vm) = await Open();
        Assert.False(vm.BookCommand.CanExecute(null));
        await Pick(vm, "carlo");
        Assert.True(vm.BookCommand.CanExecute(null));
    }

    [Fact]   // Z1 (owner, 2026-11-17): someone with no account is registered here first, then booked
    public async Task Register_new_member_from_amenity_status_then_book()
    {
        var (gw, vm) = await Open();
        vm.Search = "nobody-here"; await vm.FindCommand.ExecuteAsync(null);
        Assert.True(vm.NoMatch);
        vm.StartRegisterCommand.Execute(null);
        (vm.NewFirstName, vm.NewLastName, vm.NewEmail, vm.NewPhone) = ("Lia", "Santos", "lia@demo.io", "0917 555 0199");
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.Equal("Lia Santos", vm.Member!.Username);
        Assert.Contains("Temporary password", vm.Notice);
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        vm.Start = vm.Starts.First(); vm.Minutes = vm.Lengths.First();
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Single(await gw.ListAsync<Reservation>(r => r.MemberId == vm.LastBooked!.MemberId && r.Status == "confirmed"));
    }

    [Fact]   // final review: a new search drops the customer picked before, so Book can't go to the wrong person
    public async Task A_new_search_drops_the_old_pick()
    {
        var (_, vm) = await Open();
        await Pick(vm, "carlo");
        vm.Search = "alex"; await vm.FindCommand.ExecuteAsync(null);
        Assert.Null(vm.Member);
        Assert.False(vm.BookCommand.CanExecute(null));
    }
}
