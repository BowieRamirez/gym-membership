using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 5: members buy amenity passes from the Amenities page again; admins edit amenities; gym hours and coach shifts follow D6
public class AmenityPassTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static async Task<DemoGateway> WithSauna()
    {
        var gw = await As("admin@demo.io");
        Assert.True((await new AdminService(gw).SaveAmenityAsync(new Amenity { Name = "Steam Room", Price = 300, DurationDays = 30, Description = "Dry sauna, 6 seats" })).Ok);   // a timed pass, as amenities sold before per-session pricing are
        Assert.True((await new AdminService(gw).PublishAsync("amenity", (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Steam Room").Id)).Ok);   // G6 (0061): added as a draft
        return gw;
    }

    static async Task<AmenityCard> Card(AmenityBoardViewModel vm, string name) { await vm.LoadCommand.ExecuteAsync(null); return vm.Cards.Single(c => c.Amenity.Name == name); }

    [Fact]
    public async Task A_member_sees_the_price_buys_a_pass_and_it_turns_on_once_verified()
    {
        var gw = await As("member@demo.io", await WithSauna());
        var vm = new AmenityBoardViewModel(new AmenityService(gw));
        var card = await Card(vm, "Steam Room");
        Assert.Equal("₱300.00 for 30 days", card.PassText);
        Assert.Equal("Dry sauna, 6 seats", card.Amenity.Description);

        Assert.True((await Safe.RunAsync(() => gw.RpcAsync<Payment>("avail_amenity", new() { ["p_amenity_id"] = card.Amenity.Id }))).Ok);   // a pass bought earlier, or at the counter
        card = await Card(vm, "Steam Room");
        Assert.Equal("Waiting for your payment to be verified", card.PassText);

        var pay = (await new MembershipService(gw).MyPaymentsAsync()).Value!.Single(p => p.Kind == PayKind.Amenity && p.Status == Status.Pending);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        card = await Card(vm, "Steam Room");
        Assert.StartsWith("Pass active until ", card.PassText);
        Assert.True(card.HasPass);
    }

    [Theory]
    [InlineData("coach@demo.io")]
    [InlineData("employee@demo.io")]
    public async Task Coaches_and_staff_see_no_price_or_buy_button(string email)
    {
        var gw = await As(email, await WithSauna());
        var card = await Card(new AmenityBoardViewModel(new AmenityService(gw)), "Steam Room");
        Assert.Equal("", card.PassText);
        Assert.False(card.CanBook);
    }

    [Fact]
    public async Task A_member_without_an_active_membership_is_told_why_they_cannot_buy()
    {
        var gw = await As("carlo@demo.io", await WithSauna());   // membership expired
        var card = await Card(new AmenityBoardViewModel(new AmenityService(gw)), "Steam Room");
        var booked = await new ReservationService(gw).BookAsync(card.Amenity.Id, DateTime.UtcNow.Date.AddDays(2).AddHours(2), 60, PayMethod.Qr);
        Assert.Contains("active membership", ErrorText.For(booked.Error, booked.Message));
    }

    [Fact]
    public async Task An_admin_edits_an_amenity_price_days_and_description()
    {
        var gw = await WithSauna();
        var catalog = new CatalogAdminViewModel(new AdminService(gw));
        await catalog.LoadCommand.ExecuteAsync(null);
        await catalog.RunActionCommand.ExecuteAsync((catalog.Amenities.Single(r => r.Amenity.Name == "Steam Room"), "Edit"));
        Assert.Equal(300, catalog.FormPrice);
        catalog.FormPrice = 350; catalog.FormDescription = "Now with cold plunge";
        await catalog.MainCommand.ExecuteAsync(null);
        Assert.Null(catalog.ErrorMessage);
        Assert.False(catalog.IsPanelOpen);
        var saved = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Steam Room");
        Assert.Equal((350m, 30, "Now with cold plunge"), (saved.Price, saved.DurationDays, saved.Description));
        Assert.Single(await gw.ListAsync<Amenity>(), a => a.Name == "Steam Room");   // edited, not added again
    }

    [Fact]
    public async Task Gym_hours_are_saved_and_must_be_on_the_half_hour_with_closing_after_opening()
    {
        var gw = await As("admin@demo.io");
        var vm = new GymSettingsViewModel(new AdminService(gw), new NoPicker());
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal((TimeSpan.FromHours(6), TimeSpan.FromHours(22)), (vm.OpensAt, vm.ClosesAt));

        vm.OpensAt = TimeSpan.FromHours(23);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        vm.OpensAt = new TimeSpan(6, 15, 0);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);

        vm.OpensAt = new TimeSpan(5, 30, 0); vm.ClosesAt = TimeSpan.FromHours(23);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new TimeSpan(5, 30, 0), (await new AdminService(gw).GymSettingsAsync()).Value!.OpensAt);
    }

    [Fact]
    public async Task Coach_shifts_must_sit_on_the_half_hour_inside_gym_hours()
    {
        var gw = await As("coach@demo.io");
        var coach = new CoachService(gw);
        var day = DateTime.UtcNow.AddHours(8).Date.AddDays(5);          // a Manila date
        DateTime Manila(int h, int m = 0) => DateTime.SpecifyKind(day.AddHours(h - 8).AddMinutes(m), DateTimeKind.Utc);
        Assert.Equal("off_grid", (await coach.PostShiftAsync(Manila(8, 15), Manila(10), null)).Message);
        Assert.Equal("outside_gym_hours", (await coach.PostShiftAsync(Manila(5), Manila(7), null)).Message);
        Assert.Equal("outside_gym_hours", (await coach.PostShiftAsync(Manila(21), Manila(23), null)).Message);
        Assert.True((await coach.PostShiftAsync(Manila(8), Manila(10, 30), null)).Ok);
    }

    sealed class NoPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string FileName, byte[] Data)?>(null); }
}
