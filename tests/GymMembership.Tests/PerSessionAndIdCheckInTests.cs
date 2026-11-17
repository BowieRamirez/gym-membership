using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 8 of the 2026-11-12 plan (0066) in demo mode.
// G19: amenities are sold per session only; a pass already bought keeps working until it ends.
// G20/G21: the desk checks a member in by looking at an ID; only the ID's type is kept, and it works offline too.
public class PerSessionAndIdCheckInTests
{
    sealed class MemoryStore : IDeskStore
    {
        public string? Text;
        public string? Load() => Text;
        public void Save(string text) => Text = text;
    }

    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    /// <summary>An amenity still set up as a pass, as the database owner left it before 0066.</summary>
    static async Task<(DemoGateway Gw, int Id)> OldPassAmenity()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        Assert.True((await admin.SaveAmenityAsync(new Amenity { Name = "Old steam", Description = "Dry sauna", Price = 300 })).Ok);
        var id = (await gw.ListAsync<Amenity>()).Single(a => a.Name == "Old steam").Id;
        Assert.True((await admin.PublishAsync("amenity", id)).Ok);
        gw.OwnerSetPassDays(id, 30);
        return (gw, id);
    }

    // ---------- G19 ----------
    [Fact]
    public async Task Staff_cannot_set_up_a_pass_and_a_new_amenity_is_per_session()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        Assert.Equal("pass_not_sold", (await admin.SaveAmenityAsync(new Amenity { Name = "New pass", Description = "x", Price = 100, DurationDays = 30 })).Message);

        var vm = new CatalogAdminViewModel(admin);
        await vm.OnAppearingAsync();
        vm.AddAmenityCommand.Execute(null);
        Assert.Equal(("Price per hour", false), (vm.PriceLabel, vm.IsPackagePanel));
        (vm.FormName, vm.FormDescription, vm.FormPrice) = ("Ice bath", "Cold plunge.", 120);
        await vm.MainCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null((await gw.ListAsync<Amenity>()).Single(a => a.Name == "Ice bath").DurationDays);
    }

    [Fact]
    public async Task A_pass_is_not_sold_to_the_member_at_the_cashier_or_by_booking()
    {
        var (gw, id) = await OldPassAmenity();
        await As("employee@demo.io", gw);
        Assert.DoesNotContain((await new PaymentStaffService(gw).ItemsAsync(PayKind.Amenity, null)).Value!, i => i.Id == id);   // the Cashier sells sessions only
        Assert.Equal("pass_not_sold", (await Safe.RunAsync(() => gw.RpcAsync<Payment>("record_counter_payment",
            new() { ["p_kind"] = PayKind.Amenity, ["p_method"] = "gcash", ["p_reference_no"] = "GC-8", ["p_member_id"] = "u-alex", ["p_item_id"] = id }))).Message);

        await As("member@demo.io", gw);
        Assert.Equal("pass_not_sold", (await Safe.RunAsync(() => gw.RpcAsync<Payment>("avail_amenity", new() { ["p_amenity_id"] = id }))).Message);
        var card = (await new AmenityService(gw).BoardAsync()).Value!.Cards.Single(c => c.Amenity.Id == id);
        Assert.Equal(("Not on sale right now", false), (card.PassText, card.CanBook));
    }

    [Fact]
    public async Task A_pass_bought_before_still_shows_and_books()
    {
        var (gw, id) = await OldPassAmenity();
        gw.OwnerAddPass("u-alex", id, days: 30);
        await As("member@demo.io", gw);
        var card = (await new AmenityService(gw).BoardAsync()).Value!.Cards.Single(c => c.Amenity.Id == id);
        Assert.StartsWith("Pass active until ", card.PassText);
        Assert.True(card.CanBook);
    }

    [Fact]
    public async Task A_per_session_amenity_card_says_per_session()
    {
        var gw = await As("member@demo.io");
        var cards = (await new AmenityService(gw).BoardAsync()).Value!.Cards.Where(c => !c.HasPass && c.PassStatus is null).ToList();
        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.EndsWith(" per hour", c.PassText));
    }

    // ---------- G20 / G21 ----------
    static async Task<(DemoGateway Gw, CheckInViewModel Vm, MemberHit Alex)> DeskWithAlex(OfflineDesk? offline = null, DemoGateway? gw = null)
    {
        gw ??= await As("employee@demo.io");
        var vm = new CheckInViewModel(new AttendanceService(gw), new CoachService(gw), offline);
        await vm.OnAppearingAsync();
        vm.Search = "alex";
        await vm.SearchCommand.ExecuteAsync(null);
        var alex = Assert.Single(vm.Hits);
        await vm.CheckInCommand.ExecuteAsync(alex);
        return (gw, vm, alex);
    }

    [Fact]
    public async Task Checked_their_ID_needs_the_type_and_the_match_then_checks_in_and_keeps_only_the_type()
    {
        var (gw, vm, _) = await DeskWithAlex();
        vm.UseIdCommand.Execute(null);
        Assert.Equal((true, false), (vm.IsIdMode, vm.IsPasswordMode));
        Assert.Equal(["Driver's license", "National ID", "Passport", "School ID", "Other"], vm.IdChips.Select(c => c.Text));
        Assert.Equal((false, false), (vm.IsPinMode, vm.ShowsReason));

        await vm.ConfirmIdCommand.ExecuteAsync(null);
        Assert.Equal("Pick the type of ID the member showed.", vm.ErrorMessage);
        vm.PickIdTypeCommand.Execute(vm.IdChips[2]);
        Assert.Equal(("Passport", true, false), (vm.IdType, vm.IdChips[2].IsPicked, vm.IdChips[0].IsPicked));
        await vm.ConfirmIdCommand.ExecuteAsync(null);
        Assert.Equal("Tick that the name and photo match this member.", vm.ErrorMessage);

        vm.IdMatches = true;
        await vm.ConfirmIdCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Alex Tan is checked in.", vm.Notice);
        Assert.Equal((false, null, false, true), (vm.IsIdMode, vm.IdType, vm.IdMatches, vm.IsPinMode));
        Assert.DoesNotContain(vm.IdChips, c => c.IsPicked);
        var row = (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).OrderBy(c => c.Id).Last();
        Assert.Equal(("id", "passport", "u-emp"), (row.Method, row.IdType, row.RecordedBy));
        Assert.Equal("ID checked (Passport)", vm.Today.First().HowText);
    }

    [Fact]
    public async Task Switching_ways_in_or_closing_the_step_clears_the_ID_choice()
    {
        var (_, vm, _) = await DeskWithAlex();
        vm.UseIdCommand.Execute(null);
        (vm.IdType, vm.IdMatches) = ("National ID", true);
        vm.UsePasswordCommand.Execute(null);
        Assert.Equal((false, true, null, false), (vm.IsIdMode, vm.IsPasswordMode, vm.IdType, vm.IdMatches));
        vm.UseIdCommand.Execute(null);
        Assert.False(vm.IsPasswordMode);
        vm.CancelPinCommand.Execute(null);
        Assert.False(vm.IsIdMode);
    }

    [Fact]
    public async Task Offline_an_ID_check_in_is_queued_with_its_type_and_sent_later()
    {
        var gw = await As("employee@demo.io");
        var desk = new OfflineDesk(gw, new MemoryStore());
        await desk.RefreshAsync(force: true);
        var (_, vm, _) = await DeskWithAlex(desk, gw);
        gw.FailWith = new HttpRequestException("no connection");
        vm.UseIdCommand.Execute(null);
        (vm.IdType, vm.IdMatches) = ("School ID", true);
        await vm.ConfirmIdCommand.ExecuteAsync(null);
        Assert.Equal("school_id", Assert.Single(desk.Queued).IdType);

        gw.FailWith = null;
        Assert.Equal((1, 0), await desk.SendAsync());
        var row = (await gw.ListAsync<GymCheckIn>(c => c.MemberId == "u-alex")).OrderBy(c => c.Id).Last();
        Assert.Equal(("offline", "school_id"), (row.Method, row.IdType));
        Assert.Equal("ID checked (School ID), sent later", new CheckInRow("Alex", DateTime.UtcNow, row.Method, IdType: row.IdType).HowText);
    }

    [Fact]
    public async Task The_server_refuses_a_member_or_coach_and_a_type_not_on_the_list()
    {
        foreach (var email in new[] { "member@demo.io", "coach@demo.io" })
        {
            var gw = await As(email);
            var r = await new AttendanceService(gw).CheckInAsync("u-alex", "id", idType: "passport");
            Assert.False(r.Ok);
        }
        var desk = await As("employee@demo.io");
        Assert.Equal("bad_id_type", (await new AttendanceService(desk).CheckInAsync("u-alex", "id", idType: "library_card")).Message);
        Assert.Equal("bad_input", (await new AttendanceService(desk).CheckInAsync("u-alex", "id")).Message);
    }

    [Fact]
    public void Each_ID_type_has_a_plain_name()
    {
        Assert.Equal("Driver's license", IdTypes.Label("drivers_license"));
        Assert.Equal("drivers_license", IdTypes.Code("Driver's license"));
        Assert.Equal("ID checked (National ID)", new CheckInRow("Bea", DateTime.UtcNow, "id", IdType: "national_id").HowText);
    }
}
