using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Item 7: amenities sold per session, and used only while a coach is in the gym.</summary>
public class AmenitySessionTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 20, 2, 10, 0, TimeSpan.Zero);   // 10:10 in Manila, inside gym hours
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static readonly DateTime Tomorrow10 = new(2026, 10, 21, 2, 0, 0, DateTimeKind.Utc);   // 10:00 in Manila

    static async Task<(DemoGateway Gw, int Id)> WithAmenity(int? days = null)
    {
        var gw = new DemoGateway(clock: new Clock());
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).SaveAmenityAsync(new Amenity { Name = "Crud room", Price = 150, DurationDays = days })).Ok);
        var id = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Crud room").Id;
        foreach (var visit in await gw.ListAsync<CoachVisit>(v => v.CheckedOutAt == null)) Assert.True((await new CoachService(gw).CheckOutAsync(visit.CoachId)).Ok);   // the demo starts with a coach in
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        return (gw, id);
    }

    static async Task Buy(DemoGateway gw, int id)
    {
        var pay = await new AmenityService(gw).AvailAsync(id);
        Assert.True(pay.Ok, pay.Message);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Value!.Id, true)).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
    }

    static async Task CoachIn(DemoGateway gw)
    {
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new CoachService(gw).CheckInAsync()).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
    }

    static Task<AppResult<int>> Book(DemoGateway gw, int id, params DateTime[] starts) => Safe.RunAsync(async () => (await gw.RpcAsync<List<Reservation>>("book_slots",
        new() { ["p_slots"] = starts.Select(s => new Dictionary<string, object> { ["amenity"] = id, ["start"] = s.ToString("yyyy-MM-ddTHH:mm:ssZ") }).ToList() }) ?? []).Count);
    static async Task<AmenityCard> Card(DemoGateway gw) => (await new AmenityService(gw).BoardAsync()).Value!.Cards.Single(c => c.Amenity.Name == "Crud room");

    [Fact]
    public async Task A_member_buys_one_session_books_it_and_can_buy_another()
    {
        var (gw, id) = await WithAmenity();
        var card = await Card(gw);
        Assert.Equal("₱150.00 per session, up to 2 hours", card.PassText);

        await Buy(gw, id);
        card = await Card(gw);
        Assert.Equal("1 session ready to book", card.PassText);

        var booked = await Book(gw, id, Tomorrow10, Tomorrow10.AddMinutes(30));   // touching slots are one session
        Assert.True(booked.Ok, booked.Message);
        Assert.Equal("₱150.00 per session, up to 2 hours", (await Card(gw)).PassText);
        Assert.Equal("no_pass", (await Book(gw, id, Tomorrow10.AddHours(4))).Message);
    }

    [Fact]
    public async Task Cancelling_before_the_start_gives_the_session_back()
    {
        var (gw, id) = await WithAmenity();
        await Buy(gw, id);
        Assert.True((await Book(gw, id, Tomorrow10)).Ok);
        var booking = (await gw.ListAsync<Reservation>(r => r.AmenityId == id)).Single();
        Assert.NotNull(booking.UserAmenityId);
        Assert.True((await new ReservationService(gw).CancelAsync(booking.Id)).Ok);
        Assert.Equal("1 session ready to book", (await Card(gw)).PassText);
    }

    [Fact]
    public async Task Two_separate_bookings_on_one_session_make_neither()
    {
        var (gw, id) = await WithAmenity();
        await Buy(gw, id);
        Assert.Equal("no_pass", (await Book(gw, id, Tomorrow10, Tomorrow10.AddHours(4))).Message);
        Assert.Empty(await gw.ListAsync<Reservation>(r => r.AmenityId == id));
        Assert.Equal("1 session ready to book", (await Card(gw)).PassText);
    }

    [Fact]
    public async Task A_timed_pass_still_books_many_times()
    {
        var (gw, id) = await WithAmenity(days: 30);
        Assert.Equal("₱150.00 for 30 days", (await Card(gw)).PassText);
        await Buy(gw, id);
        Assert.True((await Book(gw, id, Tomorrow10)).Ok);
        Assert.True((await Book(gw, id, Tomorrow10.AddHours(4))).Ok);
    }

    [Fact]
    public async Task Staff_set_a_session_price_on_a_pass_amenity_and_passes_already_sold_keep_working()
    {
        var (gw, id) = await WithAmenity(days: 30);
        await Buy(gw, id);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new CatalogAdminViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        var amenity = vm.Amenities.Single(a => a.Id == id);

        vm.StartSessionPriceCommand.Execute(amenity);
        Assert.Same(amenity, vm.PricingAmenity);
        vm.SessionPrice = 0;
        await vm.SaveSessionPriceCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(30, (await gw.ListAsync<Amenity>(a => a.Id == id)).Single().DurationDays);

        vm.SessionPrice = 80;
        await vm.SaveSessionPriceCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.PricingAmenity);
        var saved = (await gw.ListAsync<Amenity>(a => a.Id == id)).Single();
        Assert.Equal((80m, (int?)null), (saved.Price, saved.DurationDays));

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.True((await Book(gw, id, Tomorrow10)).Ok);
        Assert.True((await Book(gw, id, Tomorrow10.AddHours(4))).Ok);   // the old pass, not one session
    }

    [Fact]
    public async Task An_ordinary_edit_never_switches_a_pass_amenity_and_a_new_amenity_is_per_session()
    {
        var (gw, id) = await WithAmenity(days: 30);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new CatalogAdminViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();

        vm.EditAmenityCommand.Execute(vm.Amenities.Single(a => a.Id == id));
        Assert.Equal("Price for 30 days", vm.AmenityPriceLabel);
        vm.NewAmenityDescription = "New words";
        await vm.AddAmenityCommand.ExecuteAsync(null);
        Assert.Equal(30, (await gw.ListAsync<Amenity>(a => a.Id == id)).Single().DurationDays);

        Assert.Equal("Price per session", vm.AmenityPriceLabel);
        vm.NewAmenityName = "Ice bath"; vm.NewAmenityPrice = 0;   // D4: ₱0 could never be paid
        await vm.AddAmenityCommand.ExecuteAsync(null);
        Assert.Equal("Enter a price above ₱0.", vm.ErrorMessage);
        vm.NewAmenityPrice = 120;
        await vm.AddAmenityCommand.ExecuteAsync(null);
        Assert.Null((await gw.ListAsync<Amenity>()).Single(a => a.Name == "Ice bath").DurationDays);

        var admin = new AdminService(gw);
        var settings = (await admin.GymSettingsAsync()).Value!;
        settings.WalkInPrice = 0;
        Assert.Equal("bad_walk_in_price", (await admin.SaveGymSettingsAsync(settings, null)).Message);
    }

    [Fact]
    public async Task A_booking_in_the_next_slot_needs_a_coach_in_the_gym_and_a_later_one_does_not()
    {
        var (gw, id) = await WithAmenity(days: 30);
        await Buy(gw, id);
        var nextSlot = new DateTime(2026, 10, 20, 2, 30, 0, DateTimeKind.Utc);
        Assert.Equal("no_coach_present", (await Book(gw, id, nextSlot)).Message);
        Assert.True((await Book(gw, id, Tomorrow10)).Ok);

        await CoachIn(gw);
        Assert.True((await Book(gw, id, nextSlot)).Ok);
    }

    [Fact]
    public async Task Using_an_amenity_needs_a_coach_in_the_gym_and_the_board_says_so()
    {
        var (gw, id) = await WithAmenity(days: 30);
        await Buy(gw, id);
        var pass = (await gw.ListAsync<UserAmenity>(u => u.AmenityId == id)).Single();
        var amenities = new AmenityService(gw);
        Assert.False((await amenities.BoardAsync()).Value!.CoachInGym);
        Assert.Equal("no_coach_present", (await amenities.LogUsageAsync(pass.Id, null)).Message);

        await CoachIn(gw);
        Assert.True((await amenities.BoardAsync()).Value!.CoachInGym);
        Assert.True((await amenities.LogUsageAsync(pass.Id, null)).Ok);
    }

    [Fact]
    public void The_no_coach_error_has_plain_words()
    {
        Assert.Equal(AppErrorKind.Validation, ErrorMapper.Map(new Exception("no_coach_present")));
        Assert.Contains("coach", ErrorText.For(AppErrorKind.Validation, "no_coach_present"));
    }
}
