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
        var gw = new DemoGateway();
        // 0097: the desk books members only, so Carlo buys a Monthly first (from the admin's drawer, leaving the employee's shift as asked)
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var monthly = (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        Assert.True((await new PaymentStaffService(gw).RecordCounterAsync("membership", "cash", "u-carlo", monthly.Id, null, null, null)).Ok);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        if (shift) await gw.OpenDrawerAsync();
        var vm = new DeskBookingViewModel(new AttendanceService(gw), new ReservationService(gw), new PaymentStaffService(gw));
        vm.Day = DateTime.Today.AddDays(2);
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task Pick(DeskBookingViewModel vm, string who)
    {
        vm.Search = who; await vm.SuggestNowAsync();
        vm.ChooseSuggestionCommand.Execute(vm.Suggestions.First());
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

    [Fact]   // owner fixes 2026-11-19 Q1 (0097): an account with no active membership can't be booked at the desk either
    public async Task Desk_cannot_book_for_an_account_with_no_membership()
    {
        var (gw, vm) = await Open();
        var before = (await gw.ListAsync<Reservation>()).Count;
        await Pick(vm, "bea");   // her membership is still waiting for its payment
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Equal(ErrorText.For(AppErrorKind.Validation, "no_active_membership"), vm.ErrorMessage);
        Assert.Equal(before, (await gw.ListAsync<Reservation>()).Count);
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

    [Fact]   // owner, 2026-11-17: Suggestions and Book for a walk-in show one at a time
    public async Task Find_and_walk_in_hide_each_other()
    {
        var (_, vm) = await Open();
        vm.Search = "carlo"; await vm.SuggestNowAsync();
        vm.PickMemberCommand.Execute(vm.Suggestions.First());
        vm.StartWalkInCommand.Execute(null);
        Assert.True(vm.IsWalkIn);
        Assert.False(vm.HasSuggestions);
        Assert.Null(vm.Member);
        await vm.SuggestNowAsync();
        Assert.False(vm.IsWalkIn);
        Assert.True(vm.HasSuggestions);
    }

    [Fact]   // final review: a new search drops the customer picked before, so Book can't go to the wrong person
    public async Task A_new_search_drops_the_old_pick()
    {
        var (_, vm) = await Open();
        await Pick(vm, "carlo");
        vm.Search = "alex"; await vm.SuggestNowAsync();
        Assert.Null(vm.Member);
        Assert.False(vm.BookCommand.CanExecute(null));
    }

    internal sealed class Files : IReportFiles
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
        var page = new AmenityStatusViewModel(new AmenityService(gw), new ReservationService(gw), new PaymentStaffService(gw), files);
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

    [Fact]   // 0092 (owner, 2026-11-17): a walk-in pays online through Xendit (demo: paid at once); the booking is confirmed with its receipt
    public async Task Walk_in_pays_online_through_xendit()
    {
        var (gw, vm) = await Open(shift: false);   // online money never touches the drawer
        vm.StartWalkInCommand.Execute(null);
        vm.WalkInName = "Ana Reyes";
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        vm.Start = vm.Starts.First(); vm.Length = vm.Lengths.First();
        Assert.True(vm.PayOnlineCommand.CanExecute(null));
        await vm.PayOnlineCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsWaiting);
        var r = (await gw.ListAsync<Reservation>(x => x.WalkInName == "Ana Reyes")).Single();
        var pay = (await gw.ListAsync<Payment>(p => p.Id == r.PaymentId)).Single();
        Assert.Equal(("confirmed", Status.Verified, PayMethod.Qr, (int?)null), (r.Status, pay.Status, pay.Method, pay.CashShiftId));
        Assert.Equal(pay.ReceiptNo, vm.Receipt!.ReceiptNo);
        Assert.Contains("online", vm.Receipt.Paid);
    }

    [Fact]   // 0096: 24 hours or more ahead the desk asks; approving cancels the booking and frees it from the Booked card
    public async Task A_walk_in_booking_refund_is_asked_at_the_desk_and_approving_cancels_it()
    {
        var (gw, vm) = await Open();
        var page = new AmenityStatusViewModel(new AmenityService(gw), new ReservationService(gw), new PaymentStaffService(gw), refunds: new SubscriptionService(gw));
        vm.StartWalkInCommand.Execute(null); vm.WalkInName = "Ana Reyes";
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool"); vm.Start = vm.Starts.First(); vm.Length = vm.Lengths.First();
        await vm.BookCommand.ExecuteAsync(null);
        await page.LoadBookedAsync();
        var row = page.Booked.Single(b => b.Customer == "Ana Reyes");
        Assert.Equal(("Request refund", true), (row.RefundButton, row.CanRequestRefund));
        page.ViewCommand.Execute(row);
        page.AskRefundCommand.Execute(null);
        page.RefundReason = "Customer can't come";
        await page.SendRefundCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorMessage);
        Assert.Equal("Refund requested. Waiting for the System admin.", page.Viewing!.RefundText);
        Assert.Equal("confirmed", (await gw.ListAsync<Reservation>(r => r.Id == row.Booking.Id)).Single().Status);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var refunds = new RefundsViewModel(new SubscriptionService(gw));
        await refunds.OnAppearingAsync();
        var card = refunds.Requests.Single(r => r.Request.Kind == "walk_in_booking");
        Assert.Equal(("Ana Reyes", "To review", true), (card.Member, card.Status, card.CanDecide));
        await refunds.DecideAsync(card, true, "Fine");
        Assert.Null(refunds.ErrorMessage);
        Assert.Equal("cancelled", (await gw.ListAsync<Reservation>(r => r.Id == row.Booking.Id)).Single().Status);
        await page.LoadBookedAsync();
        Assert.DoesNotContain(page.Booked, b => b.Customer == "Ana Reyes");
    }

    [Fact]
    public void Under_24_hours_only_the_gym_cancelling_can_ask_for_a_walk_ins_refund()
    {
        var soon = new BookedRow(new Reservation { WalkInName = "Ana", StartsAt = DateTime.UtcNow.AddHours(3), EndsAt = DateTime.UtcNow.AddHours(4) }, "Ana", "Pool", null, [new Payment { Amount = 200 }]);
        Assert.Equal(("Gym is cancelling", true), (soon.RefundButton, soon.CanRequestRefund));
        Assert.False((soon with { RefundStatus = "rejected" }).CanRequestRefund);
        Assert.False((soon with { Paid = [] }).CanRequestRefund);
    }

    /// <summary>The real desk service, but Xendit's answers are scripted: a link that isn't paid, then a check that says paid when told.</summary>
    public class ScriptedDesk : System.Reflection.DispatchProxy
    {
        IPaymentStaffService real = null!;
        public bool Paid; public int Checks;
        public static (IPaymentStaffService Svc, ScriptedDesk Script) Over(IPaymentStaffService real)
        {
            var svc = Create<IPaymentStaffService, ScriptedDesk>();
            var script = (ScriptedDesk)(object)svc; script.real = real;
            return (svc, script);
        }
        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? a) => m!.Name switch
        {
            "PayOnlineAsync" => Task.FromResult(AppResult<OnlineCheckout>.Success(new("https://checkout.test/ps", false))),
            "CheckOnlineAsync" => CheckAsync((int)a![0]!),
            _ => m.Invoke(real, a)
        };
        async Task<AppResult<bool>> CheckAsync(int id)
        {
            Checks++;
            if (Paid) await real.PayOnlineAsync(id);   // demo: this is the customer paying
            return AppResult<bool>.Success(Paid);
        }
    }

    // owner, 2026-11-23: the desk books amenities on the Cashier ("Amenity booking"), no longer on Amenity status
    static CashierViewModel Screen(DemoGateway gw, IPaymentStaffService svc)
    {
        var booking = new DeskBookingViewModel(new AttendanceService(gw), new ReservationService(gw), svc) { Day = DateTime.Today.AddDays(2) };
        return new CashierViewModel(svc, new AttendanceService(gw), booking: booking);
    }

    static async Task<(DemoGateway Gw, CashierViewModel Vm, ScriptedDesk Xendit, IPaymentStaffService Svc)> WaitingForXendit()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var (svc, xendit) = ScriptedDesk.Over(new PaymentStaffService(gw));
        var vm = Screen(gw, svc);
        await vm.OnAppearingAsync();
        vm.PickKindCommand.Execute(CashierViewModel.AmenityBooking);
        var b = vm.Booking!;
        await b.OnAppearingAsync();
        b.StartWalkInCommand.Execute(null); b.WalkInName = "Lin";
        b.Amenity = b.Amenities.First(a => a.Name == "Pool"); b.Start = b.Starts.First(); b.Length = b.Lengths.First();
        await b.PayOnlineCommand.ExecuteAsync(null);
        Assert.True(b.IsWaiting);
        return (gw, vm, xendit, svc);
    }

    [Fact]   // owner, 2026-11-18: nobody has to press Check payment; the receipt and the Booked card follow by themselves
    public async Task The_desk_notices_the_online_payment_by_itself()
    {
        var (_, vm, xendit, _) = await WaitingForXendit();
        await vm.AutoRefreshAsync();
        Assert.Equal(1, xendit.Checks);
        Assert.True(vm.Booking!.IsWaiting);

        xendit.Paid = true;
        await vm.AutoRefreshAsync();
        Assert.False(vm.Booking.IsWaiting);
        Assert.Equal("Lin", vm.Booking.Receipt!.Customer);
    }

    [Fact]
    public async Task The_desk_stops_waiting_when_the_payment_is_over()
    {
        var (_, vm, _, svc) = await WaitingForXendit();
        Assert.True((await svc.VerifyAsync(vm.Booking!.Waiting!.PaymentId!.Value, false, "Elsewhere")).Ok);
        await vm.AutoRefreshAsync();
        Assert.False(vm.Booking.IsWaiting);
        Assert.Contains("not booked", vm.Booking.Notice);
    }

    [Fact]   // pages are rebuilt: leaving the Cashier or restarting the app must not lose a customer who is paying
    public async Task Coming_back_to_the_page_picks_up_the_walk_in_still_paying()
    {
        var (gw, _, xendit, svc) = await WaitingForXendit();
        var again = Screen(gw, svc);
        await again.OnAppearingAsync();
        Assert.True(again.IsAmenityBooking);
        Assert.Equal("Lin", again.Booking!.Waiting!.WalkInName);
        xendit.Paid = true;
        await again.AutoRefreshAsync();
        Assert.Equal("Lin", again.Booking.Receipt!.Customer);
        Assert.Contains(",", again.Booking.Receipt.When);
    }

    [Fact]
    public async Task The_cashier_notices_the_online_payment_by_itself()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var real = new PaymentStaffService(gw);
        var (svc, xendit) = ScriptedDesk.Over(real);
        Assert.True((await real.StartDeskOnlineAsync(PayKind.WalkIn, null, null, "Ana", null)).Ok);
        var vm = new CashierViewModel(svc, new AttendanceService(gw));
        await vm.OnAppearingAsync();
        Assert.NotNull(vm.Waiting);
        await vm.AutoRefreshAsync();
        Assert.Equal(1, xendit.Checks);
        Assert.NotNull(vm.Waiting);
        xendit.Paid = true;
        await vm.AutoRefreshAsync();
        Assert.Null(vm.Waiting);
        Assert.Matches(@"Receipt OR-\d{4}-\d{6}", vm.Notice);
    }

    [Theory]   // owner, 2026-11-18: the button only re-reads CanExecute when told, so it stayed grey with the form complete
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pay_online_button_lights_up_when_the_walk_in_form_is_complete(bool nameLast)
    {
        var (_, vm) = await Open();
        var enabled = vm.PayOnlineCommand.CanExecute(null);
        vm.PayOnlineCommand.CanExecuteChanged += (_, _) => enabled = vm.PayOnlineCommand.CanExecute(null);
        vm.StartWalkInCommand.Execute(null);
        if (!nameLast) vm.WalkInName = "Lin";
        vm.Amenity = vm.Amenities.First(a => a.Name == "Pool");
        vm.Start = vm.Starts.First(); vm.Length = vm.Lengths.First();
        if (nameLast) vm.WalkInName = "Lin";
        Assert.True(enabled);
        vm.WalkInName = "";
        Assert.False(enabled);
    }

    [Fact]   // 0092: Pay online is for walk-ins only; the customer leaving cancels the payment and the booking
    public async Task Online_walk_in_cancelled_at_the_desk_frees_the_slot()
    {
        var (gw, vm) = await Open();
        await Pick(vm, "carlo");
        Assert.False(vm.PayOnlineCommand.CanExecute(null));
        var start = vm.Start!.Utc;
        var r = (await new ReservationService(gw).DeskWalkInOnlineAsync("Ben", vm.Amenity!.Id, start, 60)).Value!;
        Assert.Equal("awaiting_payment", r.Status);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(r.PaymentId!.Value, false, "Customer left at the desk")).Ok);
        Assert.Equal("cancelled", (await gw.ListAsync<Reservation>(x => x.Id == r.Id)).Single().Status);
        await vm.BookCommand.ExecuteAsync(null);   // the slot is free again for someone else
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]   // owner, 2026-11-23: amenity and coach bookings moved from Amenity status to the Cashier; the time-less "Amenity" sale went
    public async Task Cashier_shows_the_booking_forms_instead_of_the_order()
    {
        var (gw, booking) = await Open();
        var coach = new DeskCoachBookingViewModel(new SessionService(gw), new AttendanceService(gw));
        var cashier = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw), booking: booking, coachBooking: coach);
        await cashier.OnAppearingAsync();
        Assert.Equal(new[] { "Membership", "Renewal", "Amenity booking", "Coach session", "Day pass" }, cashier.Kinds);
        Assert.True(cashier.ShowsOrder);

        cashier.PickKindCommand.Execute(CashierViewModel.AmenityBooking);
        Assert.True(cashier.IsAmenityBooking);
        Assert.False(cashier.ShowsOrder); Assert.False(cashier.NeedsMember);   // the form has its own customer box and pay buttons
        Assert.Empty(cashier.Options);
        await booking.OnAppearingAsync();
        Assert.NotEmpty(booking.Amenities);

        cashier.PickKindCommand.Execute(CashierViewModel.CoachSession);
        Assert.True(cashier.IsCoachSession); Assert.False(cashier.ShowsOrder);

        cashier.PickKindCommand.Execute("Membership");
        Assert.True(cashier.ShowsOrder);
    }

    [Fact]   // Phase 1: picking a customer for an amenity booking survives the page's auto refresh (now the Cashier's)
    public async Task Picked_customer_stays_through_status_refresh()
    {
        var (gw, booking) = await Open();
        var status = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw), booking: booking);
        await status.OnAppearingAsync();
        status.PickKindCommand.Execute(CashierViewModel.AmenityBooking);
        booking.Search = "carlo";
        await booking.SuggestNowAsync();
        var hit = booking.Suggestions.First();
        booking.ChooseSuggestionCommand.Execute(hit);
        await status.AutoRefreshAsync();
        Assert.Same(hit, booking.Member);
    }
}
