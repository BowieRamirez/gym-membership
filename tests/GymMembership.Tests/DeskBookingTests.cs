using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Plan 2026-11-17 Z1, Z2 (0087): on Amenity status the desk finds a member or books a walk-in (0091), books a slot and takes the full price in cash.</summary>
public class DeskBookingTests
{
    static async Task<(DemoGateway Gw, DeskBookingViewModel Vm)> Open(bool shift = true)
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        if (shift) await gw.OpenDrawerAsync();
        var vm = new DeskBookingViewModel(new AttendanceService(gw), new ReservationService(gw));
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
        vm.Length = vm.Lengths.First();
    }

    [Fact]
    public async Task Desk_finds_a_member_and_books_a_slot_paid_in_full_in_cash()
    {
        var (gw, vm) = await Open();
        await Pick(vm, "carlo");
        var start = vm.Start!.Utc;
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(("Carlo Diaz", "Pool"), (vm.Receipt!.Customer, vm.Receipt.Amenity));
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

    [Fact]   // 0091 (owner, 2026-11-17): no account, no registering; booked by name, paid in cash, with a receipt to photograph
    public async Task Walk_in_is_booked_by_name_with_a_receipt()
    {
        var (gw, vm) = await Open();
        vm.StartWalkInCommand.Execute(null);
        Assert.False(vm.BookCommand.CanExecute(null));
        vm.WalkInName = "  Ana Reyes ";
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        vm.Start = vm.Starts.First(); vm.Length = vm.Lengths.First();
        var start = vm.Start.Utc;
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var r = (await gw.ListAsync<Reservation>(x => x.WalkInName == "Ana Reyes")).Single();
        Assert.Equal(("confirmed", start, (string?)null), (r.Status, r.StartsAt, (string?)r.MemberId));
        var pay = (await gw.ListAsync<Payment>(p => p.Id == r.PaymentId)).Single();
        Assert.Equal((Status.Verified, PayMethod.Cash, "Ana Reyes", r.TotalPrice), (pay.Status, pay.Method, pay.WalkInName, pay.Amount));
        Assert.Equal(pay.ReceiptNo, vm.Receipt!.ReceiptNo);
        Assert.Equal("Ana Reyes", vm.Receipt.Customer);
        Assert.Contains("photo", vm.Receipt.Note);
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        Assert.DoesNotContain(vm.Starts, s => s.Utc == start);   // the walk-in's slot is taken for everyone
    }

    [Fact]   // 0091: a member booked at the desk gets the receipt in their Front desk chat
    public async Task Member_gets_the_receipt_in_front_desk_chat()
    {
        var (gw, vm) = await Open();
        await Pick(vm, "carlo");
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var msg = (await gw.ListAsync<SupportMessage>(m => m.MemberId == "u-carlo" && m.Queue == ChatQueue.FrontDesk)).Last();
        Assert.Contains($"Receipt {vm.Receipt!.ReceiptNo}", msg.Body);
        Assert.Contains("chat", vm.Receipt.Note);
    }

    [Fact]   // owner, 2026-11-17: Find and Book for a walk-in show one at a time
    public async Task Find_and_walk_in_hide_each_other()
    {
        var (_, vm) = await Open();
        vm.Search = "carlo"; await vm.FindCommand.ExecuteAsync(null);
        vm.PickMemberCommand.Execute(vm.Hits.First());
        vm.StartWalkInCommand.Execute(null);
        Assert.True(vm.IsWalkIn);
        Assert.False(vm.HasHits);
        Assert.Null(vm.Member);
        await vm.FindCommand.ExecuteAsync(null);
        Assert.False(vm.IsWalkIn);
        Assert.True(vm.HasHits);
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

    sealed class Files : IReportFiles
    {
        public string? Name; public byte[]? Pdf;
        public Task<byte[]> FontAsync() => Task.FromResult(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "NotoSans-Regular.ttf")));
        public Task ShareAsync(string fileName, byte[] pdf) => OpenAsync(fileName, pdf);
        public Task OpenAsync(string fileName, byte[] pdf) { Name = fileName; Pdf = pdf; return Task.CompletedTask; }
    }

    [Fact]   // owner, 2026-11-17: the Booked card lists who booked; View shows the details; Print opens the receipt PDF
    public async Task Booked_card_lists_bookings_and_prints_the_receipt()
    {
        var (gw, vm) = await Open();
        var files = new Files();
        var page = new AmenityStatusViewModel(new AmenityService(gw), new ReservationService(gw), new PaymentStaffService(gw), vm, files);
        await page.ToggleBookingCommand.ExecuteAsync(null);
        vm.StartWalkInCommand.Execute(null);
        vm.WalkInName = "Ana Reyes";
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        vm.Start = vm.Starts.First(); vm.Length = vm.Lengths.First();
        await vm.BookCommand.ExecuteAsync(null);
        await page.LoadBookedAsync();
        var row = page.Booked.Single(b => b.Customer == "Ana Reyes");
        Assert.Equal(("Ana Reyes (walk-in)", "Pool", vm.Receipt!.ReceiptNo), (row.Who, row.Amenity, row.ReceiptText));
        Assert.Equal(row.Booking.TotalPrice, row.PaidAmount);
        page.ViewCommand.Execute(row);
        Assert.True(page.IsViewing);
        await page.PrintReceiptCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorMessage);
        Assert.Equal($"receipt-booking-{row.Booking.Id}.pdf", files.Name);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(files.Pdf!, 0, 4));
    }
}
