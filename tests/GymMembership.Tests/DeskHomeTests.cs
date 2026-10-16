using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class DeskHomeTests
{
    static async Task<(DemoGateway Gw, ChatService Chat, DeskHomeService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw));
        return (gw, chat, new DeskHomeService(gw, chat));
    }

    [Fact]
    public async Task Amenities_are_counted_by_status_and_add_up_to_the_active_ones()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var s = (await svc.SummaryAsync()).Value!;
        var active = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        Assert.Equal(active.Count, s.AmenitiesTotal);
        Assert.Equal(active.Count(a => a.Availability == "unavailable"), s.AmenitiesUnavailable);   // the demo's sauna is out of service
        Assert.True(s.AmenitiesUnavailable >= 1);
    }

    [Fact]
    public async Task Payments_to_verify_match_the_pending_list()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var pending = (await new PaymentStaffService(gw).PendingAsync()).Value!;
        Assert.Equal(pending.Count, (await svc.SummaryAsync()).Value!.PaymentsToVerify);
    }

    [Fact]
    public async Task A_check_in_today_adds_one()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var before = (await svc.SummaryAsync()).Value!.CheckInsToday;
        Assert.True((await new AttendanceService(gw).CheckInAsync("u-alex")).Ok);
        Assert.Equal(before + 1, (await svc.SummaryAsync()).Value!.CheckInsToday);
    }

    [Fact]
    public async Task A_member_writing_to_the_front_desk_shows_as_unread_and_not_taken()
    {
        var (gw, _, svc) = await As("employee@demo.io");
        var before = (await svc.SummaryAsync()).Value!;

        var (_, memberChat, _) = await As("carlo@demo.io", gw);
        var mine = (await memberChat.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.True((await memberChat.SendAsync(mine, "my payment is stuck")).Ok);

        await As("employee@demo.io", gw);
        var after = (await svc.SummaryAsync()).Value!;
        Assert.Equal(before.UnreadMessages + 1, after.UnreadMessages);
        Assert.Equal(before.UnclaimedChats + 1, after.UnclaimedChats);
    }

    [Fact]
    public async Task A_members_own_front_desk_thread_is_not_counted_as_an_inbox_message()
    {
        var (_, _, svc) = await As("carlo@demo.io");   // a member has a Front desk thread but no inbox
        var s = (await svc.SummaryAsync()).Value!;
        Assert.Equal(0, s.UnreadMessages);
        Assert.Equal(0, s.UnclaimedChats);
    }

    sealed class FakeHome : IDeskHomeService
    {
        public AppResult<DeskSummary> Next = AppResult<DeskSummary>.Success(new DeskSummary(12, 3, 2, 1, 1, 4, 2));
        public Task<AppResult<DeskSummary>> SummaryAsync() => Task.FromResult(Next);
    }

    [Fact]
    public void Tiles_say_what_needs_doing_in_words_as_well_as_by_outline()
    {
        var tiles = HomeViewModel.Build(new DeskSummary(12, 3, 2, 1, 1, 4, 2));
        Assert.Equal(new[] { "checkin", "verify", "amenities", "messages" }, tiles.Select(t => t.Route));

        var verify = tiles.Single(t => t.Route == "verify");
        Assert.Equal("3", verify.Count); Assert.Equal("3 payments are waiting", verify.Detail); Assert.True(verify.NeedsAttention);

        var amenities = tiles.Single(t => t.Route == "amenities");
        Assert.Equal("2 of 4", amenities.Count); Assert.Equal("1 in use, 1 unavailable", amenities.Detail); Assert.True(amenities.NeedsAttention);

        var messages = tiles.Single(t => t.Route == "messages");
        Assert.Equal("4", messages.Count); Assert.Equal("2 chats nobody has taken", messages.Detail); Assert.True(messages.NeedsAttention);

        Assert.False(tiles.Single(t => t.Route == "checkin").NeedsAttention);   // a count of people is not a task
    }

    [Fact]
    public void A_quiet_day_has_no_outlines_and_says_so()
    {
        var tiles = HomeViewModel.Build(new DeskSummary(0, 0, 3, 0, 0, 0, 0));
        Assert.All(tiles, t => Assert.False(t.NeedsAttention));
        Assert.Equal("All caught up", tiles.Single(t => t.Route == "verify").Detail);
        Assert.Equal("All open", tiles.Single(t => t.Route == "amenities").Detail);
        Assert.Equal("No new messages", tiles.Single(t => t.Route == "messages").Detail);
        Assert.Equal("No one has checked in yet", tiles.Single(t => t.Route == "checkin").Detail);
    }

    [Fact]
    public void Singular_counts_read_as_singular()
    {
        var tiles = HomeViewModel.Build(new DeskSummary(1, 1, 1, 0, 0, 1, 1));
        Assert.Equal("1 payment is waiting", tiles.Single(t => t.Route == "verify").Detail);
        Assert.Equal("1 chat nobody has taken", tiles.Single(t => t.Route == "messages").Detail);
    }

    [Fact]
    public async Task The_home_loads_four_tiles_from_the_desk_summary()
    {
        var (_, _, svc) = await As("employee@demo.io");
        var vm = new HomeViewModel(svc);
        await vm.OnAppearingAsync();
        Assert.Equal(4, vm.Tiles.Count);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task A_quiet_refresh_keeps_the_tiles_that_did_not_change_and_swaps_the_ones_that_did()
    {
        var home = new FakeHome();
        var vm = new HomeViewModel(home);
        await vm.OnAppearingAsync();
        var before = vm.Tiles.ToList();

        home.Next = AppResult<DeskSummary>.Success(new DeskSummary(12, 5, 2, 1, 1, 4, 2));   // only the payments changed
        await vm.AutoRefreshAsync();
        Assert.Same(before[0], vm.Tiles[0]); Assert.Same(before[2], vm.Tiles[2]); Assert.Same(before[3], vm.Tiles[3]);
        Assert.Equal("5", vm.Tiles[1].Count);
    }

    [Fact]
    public async Task A_good_refresh_clears_the_error_a_failed_first_load_left_behind()
    {
        var home = new FakeHome { Next = AppResult<DeskSummary>.Fail(AppErrorKind.Validation, "bad_input") };
        var vm = new HomeViewModel(home);
        await vm.OnAppearingAsync();
        Assert.NotNull(vm.ErrorMessage);

        home.Next = AppResult<DeskSummary>.Success(new DeskSummary(1, 0, 1, 0, 0, 0, 0));
        await vm.AutoRefreshAsync();                           // the 20 second look works this time
        Assert.Null(vm.ErrorMessage);                          // so the banner above working numbers goes
        Assert.Equal(4, vm.Tiles.Count);
    }

    [Fact]
    public void A_tile_reads_its_count_aloud_with_its_title()
    {
        var tiles = HomeViewModel.Build(new DeskSummary(12, 3, 2, 1, 1, 4, 2));
        Assert.Equal("Payments to verify: 3", tiles.Single(t => t.Route == "verify").Description);
        Assert.Equal("Amenities available: 2 of 4", tiles.Single(t => t.Route == "amenities").Description);
    }

    [Fact]
    public async Task A_failed_first_load_shows_an_error_but_a_failed_refresh_keeps_the_numbers_and_says_nothing()
    {
        var home = new FakeHome { Next = AppResult<DeskSummary>.Fail(AppErrorKind.Validation, "bad_input") };
        var vm = new HomeViewModel(home);
        await vm.OnAppearingAsync();
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.Tiles);

        home.Next = AppResult<DeskSummary>.Success(new DeskSummary(1, 0, 1, 0, 0, 0, 0));
        await vm.OnAppearingAsync();
        Assert.Equal(4, vm.Tiles.Count);

        home.Next = AppResult<DeskSummary>.Fail(AppErrorKind.Validation, "bad_input");
        await vm.AutoRefreshAsync();
        Assert.Equal(4, vm.Tiles.Count);       // still showing the last numbers
        Assert.Null(vm.ErrorMessage);          // and no new error line
    }
}
