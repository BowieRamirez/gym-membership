using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Book an amenity in demo mode (Phase 6 of the 2026-10-20 plan, then F12-F14 of the 2026-11-09 plan).
// Alex (member@demo.io) holds an active Pool pass and no Group classes pass; Bea and Carlo have no active membership.
public class ReservationTests
{
    static readonly DateOnly Tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(1);   // Manila: every slot is in the future
    static DateTime At(string hm) => DateTime.SpecifyKind(Tomorrow.ToDateTime(TimeOnly.Parse(hm)).AddHours(-8), DateTimeKind.Utc);

    static async Task<(DemoGateway Gw, BookAmenityViewModel Vm)> Member(string email = "member@demo.io", DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new BookAmenityViewModel(new ReservationService(gw), new AmenityService(gw), new ConfirmationTests.Nav());
        await vm.LoadCommand.ExecuteAsync(null);
        return (gw, vm);
    }

    // open the sheet for an amenity and pick tomorrow, a start and a length
    static async Task Pick(BookAmenityViewModel vm, string amenity, string hm, int minutes = 60)
    {
        await vm.OpenCommand.ExecuteAsync(vm.Cards.Single(c => c.Amenity.Name == amenity));
        await vm.PickDayCommand.ExecuteAsync(vm.Days.Single(d => d.Date == Tomorrow));
        vm.PickTimeCommand.Execute(vm.Times.Single(t => t.StartUtc == At(hm)));
        vm.PickLengthCommand.Execute(vm.Lengths.Single(l => l.Minutes == minutes));
    }

    static async Task<BookingRow> Booked(BookAmenityViewModel vm, string amenity, string hm, int minutes = 60, string method = PayMethod.Qr)
    {
        await Pick(vm, amenity, hm, minutes);
        vm.PickMethodCommand.Execute(method);
        await vm.BookCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        return vm.Bookings.Single(b => b.Amenity == amenity && b.Booking.StartsAt.ToUniversalTime() == At(hm));
    }

    static async Task AsDesk(DemoGateway gw, Func<VerifyPaymentsViewModel, Task> act)
    {
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await gw.OpenDrawerAsync();
        var desk = new VerifyPaymentsViewModel(new PaymentStaffService(gw));
        await desk.LoadCommand.ExecuteAsync(null);
        await act(desk);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
    }

    // ---------- the rules the sheet offers ----------
    static AmenityGrid Grid(params (string From, string To)[] taken) => new()
    {
        Now = At("06:00").AddHours(-10), Opens = At("06:00"), Closes = At("22:00"),
        Amenities = [new GridAmenity { Id = 1, Name = "Pool" }],
        Blocks = taken.Select(b => new GridBlock { Amenity = 1, Starts = At(b.From), Ends = At(b.To), Cooldown = At(b.To).AddMinutes(30) }).ToList()
    };

    [Fact]
    public void A_start_offers_only_lengths_that_end_before_the_next_bookings_cooldown_would_clash()
    {
        var g = Grid(("10:00", "11:00"));
        Assert.Equal([30, 60, 90, 120], BookingSlots.Lengths(g, 1, At("07:00"), payFirst: true));
        Assert.Equal([30], BookingSlots.Lengths(g, 1, At("09:00"), true));            // 9:30-10:00 would leave no cooldown before 10:00
        Assert.Empty(BookingSlots.Lengths(g, 1, At("09:30"), true));
        Assert.Empty(BookingSlots.Lengths(g, 1, At("10:30"), true));                  // booked
        Assert.Empty(BookingSlots.Lengths(g, 1, At("11:00"), true));                  // its cooldown
        Assert.Equal([30, 60, 90, 120], BookingSlots.Lengths(g, 1, At("11:30"), true));
        Assert.Equal([30, 60], BookingSlots.Lengths(g, 1, At("21:00"), true));        // closing time
    }

    [Fact]
    public void Paying_first_needs_the_start_more_than_an_hour_away_but_a_pass_books_up_to_the_start()
    {
        var g = Grid(); g.Now = At("09:01");
        Assert.Empty(BookingSlots.Lengths(g, 1, At("10:00"), payFirst: true));
        Assert.NotEmpty(BookingSlots.Lengths(g, 1, At("10:30"), true));
        Assert.NotEmpty(BookingSlots.Lengths(g, 1, At("09:30"), payFirst: false));
    }

    // ---------- booking ----------
    [Fact]
    public async Task A_member_with_a_pass_books_straight_away()
    {
        var (_, vm) = await Member();
        await Pick(vm, "Pool", "10:00", 90);
        Assert.Equal(("Book", false), (vm.BookText, vm.PayFirst));
        Assert.EndsWith("10:00 to 11:30 AM", vm.Summary);
        var row = await Booked(vm, "Pool", "10:00", 90);
        Assert.Equal("Booked.", vm.Notice);
        Assert.False(vm.IsPicking);
        Assert.Equal(("Confirmed", "Booked with your pass.", true), (row.StatusText, row.Detail, row.CanCancel));
    }

    [Fact]
    public async Task Without_a_pass_the_time_is_held_until_the_front_desk_approves_the_payment()
    {
        var (gw, vm) = await Member();
        await Pick(vm, "Group classes", "10:00");
        Assert.Equal(("Book and pay", true), (vm.BookText, vm.PayFirst));
        Assert.StartsWith("Held for you until paid", vm.HoldText);
        var row = await Booked(vm, "Group classes", "10:00");
        Assert.Equal("₱500.00 for 30 days", vm.Cards.Single(c => c.Amenity.Name == "Group classes").PassText);   // the hold is not a pass bought
        Assert.Equal(("Waiting for payment", "wait", true, true), (row.StatusText, row.Tone, row.CanSendProof, row.CanCancel));
        Assert.StartsWith("₱500.00 by GCash or QR. Pay by ", row.Detail);
        Assert.StartsWith("Held for you.", vm.Notice);

        await AsDesk(gw, async desk =>
        {
            var pay = desk.Pending.Single(p => p.HasBooking);
            Assert.Equal(("Group classes booking", "Verify", "QR"), (pay.Item, pay.ApproveText, pay.MethodText));
            Assert.EndsWith("10:00 AM to 11:00 AM", pay.Booking);
            await desk.ApproveCommand.ExecuteAsync(pay);
            Assert.Null(desk.ErrorMessage);
        });
        await vm.LoadCommand.ExecuteAsync(null);
        row = vm.Bookings.Single(b => b.Amenity == "Group classes");
        Assert.Equal(("Confirmed", true), (row.StatusText, row.Paid));
        Assert.StartsWith("Paid ₱500.00. Cancel by ", row.Detail);
        Assert.Contains((await gw.ListAsync<UserNotification>()).Select(n => n.Title), t => t == "Booking confirmed");
    }

    [Fact]
    public async Task Pay_at_the_desk_is_approved_by_taking_the_cash()
    {
        var (gw, vm) = await Member();
        var row = await Booked(vm, "Group classes", "14:00", 30, PayMethod.Cash);
        Assert.StartsWith("₱500.00 at the front desk by ", row.Detail);
        Assert.False(row.CanSendProof);
        await AsDesk(gw, async desk =>
        {
            var pay = desk.Pending.Single(p => p.HasBooking);
            Assert.Equal(("Take cash", "At the desk"), (pay.ApproveText, pay.MethodText));
            await desk.ApproveCommand.ExecuteAsync(pay);
        });
        var cash = (await gw.ListAsync<Payment>(p => p.Id == row.Payment!.Id)).Single();
        Assert.Equal(Status.Verified, cash.Status);
    }

    [Fact]
    public async Task A_held_time_is_taken_for_everyone_else_and_rejecting_the_payment_frees_it()
    {
        var (gw, vm) = await Member();
        var row = await Booked(vm, "Group classes", "10:00");
        await vm.OpenCommand.ExecuteAsync(vm.Cards.Single(c => c.Amenity.Name == "Group classes"));
        await vm.PickDayCommand.ExecuteAsync(vm.Days.Single(d => d.Date == Tomorrow));
        Assert.Equal((false, false, true), (vm.Times.Single(t => t.StartUtc == At("10:30")).CanPick, vm.Times.Single(t => t.StartUtc == At("11:00")).CanPick,
            vm.Times.Single(t => t.StartUtc == At("11:30")).CanPick));   // held, then its cooldown
        Assert.True(vm.Days.Single(d => d.Date == Tomorrow).HasBooking);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(row.Payment!.Id, false, "No transfer found")).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.Equal("cancelled", (await gw.ListAsync<Reservation>(r => r.Id == row.Booking.Id)).Single().Status);
        var grid = (await new ReservationService(gw).GridAsync(Tomorrow)).Value!;
        Assert.DoesNotContain(grid.Blocks, b => b.Amenity == row.Booking.AmenityId);
    }

    [Fact]
    public async Task A_clash_books_nothing_reloads_the_day_and_keeps_the_reason()
    {
        var (gw, vm) = await Member();
        await Pick(vm, "Pool", "14:00");
        Assert.True((await new ReservationService(gw).BookAsync(vm.Picked!.Amenity.Id, At("14:30"), 30, PayMethod.Qr)).Ok);   // meanwhile, from another device
        await vm.BookCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsPicking);
        Assert.False(vm.Times.Single(t => t.StartUtc == At("14:00")).CanPick);
    }

    [Fact]
    public async Task Another_members_booking_shows_as_taken_without_who()
    {
        var (gw, vm) = await Member();
        await Booked(vm, "Pool", "12:00");
        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var block = Assert.Single((await new ReservationService(gw).GridAsync(Tomorrow)).Value!.Blocks);
        Assert.False(block.Mine);
        Assert.Null(block.Id);
    }

    // ---------- F13: cancel and refunds ----------
    [Fact]
    public async Task Cancelling_an_unpaid_hold_releases_it_and_withdraws_the_payment()
    {
        var (gw, vm) = await Member();
        var row = await Booked(vm, "Group classes", "10:00");
        await vm.CancelCommand.ExecuteAsync(row);
        Assert.Equal("Booking cancelled.", vm.Notice);
        Assert.Empty(vm.Bookings);
        Assert.Equal(Status.Rejected, (await gw.ListAsync<Payment>(p => p.Id == row.Payment!.Id)).Single().Status);
    }

    [Fact]
    public async Task Cancelling_a_paid_booking_approves_a_full_refund_that_admin_repays()
    {
        var (gw, vm) = await Member();
        var row = await Booked(vm, "Group classes", "10:00");
        await AsDesk(gw, desk => desk.ApproveCommand.ExecuteAsync(desk.Pending.Single(p => p.HasBooking)));
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.CancelCommand.ExecuteAsync(vm.Bookings.Single(b => b.Amenity == "Group classes"));
        Assert.StartsWith("Booking cancelled. Your refund is approved", vm.Notice);
        row = vm.Bookings.Single(b => b.Amenity == "Group classes");
        Assert.Equal(("Cancelled · refund approved", "₱500.00 to be repaid at the front desk."), (row.StatusText, row.Detail));
        Assert.False(row.CanCancel);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await gw.OpenDrawerAsync();
        var card = (await new SubscriptionService(gw).RefundsAsync()).Value!.Single(c => c.Request.IsBooking);
        Assert.Equal(("Group classes booking", false, true), (card.PackageName, card.CanDecide, card.CanRepay));
        Assert.StartsWith("Booking cancelled 1 hour or more", card.ReviewText);
        Assert.True((await new SubscriptionService(gw).MarkPaidAsync(card.Request.Id, PayMethod.Cash, null)).Ok);
    }

    [Fact]
    public void A_paid_booking_can_be_cancelled_until_an_hour_before_and_claimed_for_a_day_after()
    {
        var start = At("10:00");
        var paid = new Payment { Id = 1, Status = Status.Verified, Amount = 500, Method = PayMethod.Qr };
        BookingRow Row(DateTime now, RefundRequest? f = null) => new(new Reservation { Id = 1, StartsAt = start, EndsAt = start.AddHours(1), Status = "confirmed", PaymentId = 1 }, "Pool", now, paid, f);
        Assert.True(Row(start.AddHours(-1)).CanCancel);
        Assert.False(Row(start.AddMinutes(-59)).CanCancel);
        Assert.Equal("Paid ₱500.00. It starts within the hour, so it can't be cancelled now.", Row(start.AddMinutes(-59)).Detail);
        Assert.False(Row(start.AddMinutes(59)).CanClaim);   // still going
        Assert.True(Row(start.AddHours(1)).CanClaim);
        Assert.True(Row(start.AddHours(25)).CanClaim);
        Assert.False(Row(start.AddHours(25).AddMinutes(1)).CanClaim);
        Assert.False(Row(start.AddHours(2), new RefundRequest { Kind = "no_show", Status = "pending" }).CanClaim);
        Assert.Equal("Refund claim sent", Row(start.AddHours(2), new RefundRequest { Kind = "no_show", Status = "pending" }).StatusText);
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task I_didnt_use_it_goes_to_the_front_desk_who_approve_or_decline_with_a_reason()
    {
        var clock = new Clock();
        var (gw, vm) = await Member(gw: new DemoGateway(clock: clock));
        var row = await Booked(vm, "Group classes", "10:00");
        await AsDesk(gw, desk => desk.ApproveCommand.ExecuteAsync(desk.Pending.Single(p => p.HasBooking)));
        var service = new ReservationService(gw);
        Assert.Equal("not_ended", (await service.ClaimAsync(row.Booking.Id, "The room was locked")).Message);
        clock.Now = row.Booking.EndsAt.ToUniversalTime().AddHours(2);
        Assert.Equal(AppErrorKind.Validation, (await service.ClaimAsync(row.Booking.Id, " ")).Error);
        Assert.True((await service.ClaimAsync(row.Booking.Id, "The room was locked")).Ok);
        Assert.Equal("already_requested", (await service.ClaimAsync(row.Booking.Id, "again")).Message);

        await AsDesk(gw, async desk =>
        {
            var claim = Assert.Single(desk.Claims);
            Assert.Equal(("Alex Tan", "Group classes", "₱500.00"), (claim.Member, claim.Amenity, claim.Price));
            Assert.Equal("Available", claim.AmenityNow);
            desk.DeclineClaimCommand.Execute(claim);
            Assert.Equal("Decline", desk.DecideText);
            await desk.ConfirmDecisionCommand.ExecuteAsync(null);
            Assert.NotNull(desk.ErrorMessage);                                    // a reason is needed
            desk.DecideReason = "Checked in at 10:02 and the room log shows a class";
            await desk.ConfirmDecisionCommand.ExecuteAsync(null);
            Assert.Empty(desk.Claims);
            Assert.False(desk.IsDeciding);
        });
        var f = (await gw.ListAsync<RefundRequest>()).Single(r => r.ReservationId == row.Booking.Id);
        Assert.Equal(("rejected", "Checked in at 10:02 and the room log shows a class"), (f.Status, f.DecisionReason));
    }

    [Fact]
    public async Task Admin_cannot_decide_a_booking_claim_as_a_membership_refund()
    {
        var clock = new Clock();
        var (gw, vm) = await Member(gw: new DemoGateway(clock: clock));
        var row = await Booked(vm, "Group classes", "10:00");
        await AsDesk(gw, desk => desk.ApproveCommand.ExecuteAsync(desk.Pending.Single(p => p.HasBooking)));
        clock.Now = row.Booking.EndsAt.ToUniversalTime().AddHours(1);
        Assert.True((await new ReservationService(gw).ClaimAsync(row.Booking.Id, "Locked")).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var f = (await gw.ListAsync<RefundRequest>()).Single(r => r.ReservationId == row.Booking.Id);
        Assert.Equal("not_found", (await new SubscriptionService(gw).DecideAsync(f.Id, true, "ok")).Message);
    }

    // ---------- F14: coaches run amenities ----------
    [Fact]
    public async Task A_coach_says_they_run_an_amenity_and_members_see_who_runs_it()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("kai@demo.io", DemoGateway.DemoPassword);
        var coach = new AmenityBoardViewModel(new AmenityService(gw));
        await coach.LoadCommand.ExecuteAsync(null);
        var pool = coach.Cards.Single(c => c.Amenity.Name == "Pool");
        Assert.Equal(("Nobody yet: any coach in the gym counts", false), (pool.RunText, pool.RunsThis));
        await coach.ToggleRunsCommand.ExecuteAsync(pool);
        pool = coach.Cards.Single(c => c.Amenity.Name == "Pool");
        Assert.Equal(("Only you run this", true), (pool.RunText, pool.RunsThis));

        var (_, vm) = await Member(gw: gw);
        Assert.StartsWith("Run by ", vm.Cards.Single(c => c.Amenity.Name == "Pool").RunText);
        Assert.Equal("Any coach in the gym", vm.Cards.Single(c => c.Amenity.Name == "Group classes").RunText);
        Assert.Equal("forbidden", (await new AmenityService(gw).SetRunsAsync(pool.Amenity.Id, true)).Message);
    }
}
