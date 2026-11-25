using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CashierFilterTests
{
    sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNowValue { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNowValue, TimeSpan.Zero);
    }

    sealed class StaleThenFreshAttendance(MemberHit stale, MemberHit fresh) : IAttendanceService
    {
        public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string query) =>
            Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { stale }));
        public Task<AppResult<MemberHit?>> MemberStatusAsync(string memberId) =>
            Task.FromResult(AppResult<MemberHit?>.Success(memberId == fresh.MemberId ? fresh : null));
        public Task<AppResult<bool>> RecordAsync(int sessionId, string status) => throw new NotImplementedException();
        public Task<AppResult<bool>> CheckInAsync(string memberId, string? method = null, string? pin = null, string? reason = null, string? proof = null, string? idType = null) => throw new NotImplementedException();
        public Task<AppResult<string>> VerifyPasswordAsync(string memberId, string password) => throw new NotImplementedException();
        public Task<AppResult<bool>> CanSkipPinAsync() => throw new NotImplementedException();
        public Task<AppResult<MemberPass>> MyPassAsync() => throw new NotImplementedException();
        public Task<AppResult<MemberScan>> ScanAsync(string code) => throw new NotImplementedException();
        public Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay) => throw new NotImplementedException();
    }

    static async Task<(DemoGateway Gw, CashierViewModel Vm, FakeClock Clock)> OpenAsync()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 20, 15, 59, 0, DateTimeKind.Utc)); // 11:59 pm Manila
        var gw = new DemoGateway(clock: clock);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var pkgs = (await new MembershipService(gw).ListPackagesAsync()).Value!;
        var monthly = pkgs.First(p => p.Name == "Monthly");
        var dayPass = pkgs.First(p => p.IsDayPass);
        var paySvc = new PaymentStaffService(gw);
        // Alex Tan: active Monthly; Carlo Diaz: active Day pass until midnight; Bea Lim: no active membership
        Assert.True((await paySvc.RecordCounterAsync("membership", "cash", "u-carlo", dayPass.Id, null, null, null)).Ok);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw, clock));
        await vm.OnAppearingAsync();
        return (gw, vm, clock);
    }

    [Fact]
    public async Task Membership_and_Day_pass_hide_active_members_and_Day_pass_holders_while_Renewal_shows_only_members()
    {
        var (_, vm, _) = await OpenAsync();
        vm.Search = "demo";
        await vm.SuggestNowAsync();   // Alex (active), Carlo (day pass), Bea (pending/no membership), Joy (no membership)

        // Membership kind: hides Alex and Carlo, shows Bea and Joy
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-alex");
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-carlo");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-bea");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-joy");

        // Day pass kind: same filter
        vm.PickKindCommand.Execute("Day pass");
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-alex");
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-carlo");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-bea");

        // Renewal kind (owner, 2026-11-25): every existing account, active (extend) or not (buy again); a Day pass until midnight is hidden
        vm.PickKindCommand.Execute("Renewal");
        await vm.SuggestNowAsync();
        Assert.Contains(vm.Hits, h => h.MemberId == "u-alex");
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-carlo");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-bea");

        // Amenity kind: shows everyone
        vm.PickKindCommand.Execute("Amenity");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-alex");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-carlo");
        Assert.Contains(vm.Hits, h => h.MemberId == "u-bea");
    }

    [Fact]   // owner, 2026-11-25: Renewal lists accounts with nothing typed; one with no membership buys a package again
    public async Task Renewal_lists_accounts_and_a_member_with_no_membership_buys_a_package()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var (gw, vm, _) = await OpenAsync();
        vm.PickKindCommand.Execute("Renewal");
        await vm.SuggestNowAsync();
        Assert.Contains(vm.Hits, h => h.MemberId == "u-joy");
        Assert.Empty(vm.Suggestions);   // no dropdown without a typed search
        Assert.Empty(vm.Options);       // nothing to sell until a member is picked

        vm.PickMemberCommand.Execute(vm.Hits.First(h => h.MemberId == "u-joy"));
        for (var i = 0; i < 50 && vm.Options.Count == 0; i++) await Task.Delay(10);
        vm.PickItemCommand.Execute(vm.Options.First(o => o.Item.Label.Contains("Monthly")));
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var paid = (await gw.ListAsync<Payment>()).Where(p => p.MemberId == "u-joy").OrderBy(p => p.Id).Last();
        Assert.Equal(PayKind.Membership, paid.Kind);
    }

    [Fact]
    public async Task Typing_a_hidden_member_shows_HiddenHint_and_not_NoMatch()
    {
        var (_, vm, _) = await OpenAsync();
        vm.Search = "alex";
        await vm.SuggestNowAsync();
        Assert.Empty(vm.Hits);
        Assert.False(vm.NoMatch);
        Assert.Contains("already has a membership", vm.HiddenHint);
    }

    [Fact]   // Review Focus 3: 11:59 pm hidden, 12:01 am shown
    public async Task Expired_Day_pass_after_Manila_midnight_is_shown_for_Membership()
    {
        var (_, vm, clock) = await OpenAsync();
        vm.Search = "carlo";
        await vm.SuggestNowAsync();
        Assert.DoesNotContain(vm.Hits, h => h.MemberId == "u-carlo");
        Assert.Contains("Day pass", vm.HiddenHint);

        clock.UtcNowValue = clock.UtcNowValue.AddMinutes(2);   // 12:01 am Manila
        await vm.SuggestNowAsync();
        var carlo = Assert.Single(vm.Hits, h => h.MemberId == "u-carlo");
        Assert.Equal("No membership", carlo.DeskStatusText);
    }

    [Fact]
    public async Task Pick_rereads_fresh_status_keeps_pick_and_turns_off_Complete_payment_when_ineligible()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var stale = new MemberHit("u-joy", "Joy", false, null, null, "expired", null, null, "joy@demo.io");
        var fresh = new MemberHit("u-joy", "Joy", true, new DateTime(2026, 12, 16, 15, 59, 59, DateTimeKind.Utc), null, "active", null, null, "joy@demo.io");
        var vm = new CashierViewModel(new PaymentStaffService(gw), new StaleThenFreshAttendance(stale, fresh));
        await vm.OnAppearingAsync();

        vm.Search = "joy";
        await vm.SuggestNowAsync();
        vm.PickItemCommand.Execute(vm.Options.First());
        vm.ChooseSuggestionCommand.Execute(vm.Suggestions.Single());
        await Task.Delay(50);

        Assert.Equal("u-joy", vm.Member?.MemberId);
        Assert.Equal("Member until 16 Dec 2026", vm.Member?.DeskStatusText);
        Assert.False(vm.RecordCommand.CanExecute(null));
        Assert.Contains("already has a membership", vm.OrderIneligibleText);
    }

    [Fact]   // Review Focus 1: picked customer, item, and code stay through AutoRefreshAsync
    public async Task Picked_customer_and_order_stay_through_auto_refresh()
    {
        var (_, vm, _) = await OpenAsync();
        vm.Search = "bea";
        await vm.SuggestNowAsync();
        vm.ChooseSuggestionCommand.Execute(vm.Suggestions.Single());
        vm.PickItemCommand.Execute(vm.Options.First());
        vm.DiscountCode = "SAVE10";
        await vm.AutoRefreshAsync();
        Assert.Equal("u-bea", vm.Member?.MemberId);
        Assert.NotNull(vm.Item);
        Assert.Equal("SAVE10", vm.DiscountCode);
    }

    [Fact]   // Phase 4 (D5): ChangeCustomer clears the pick and marks/unmarks the chosen row
    public async Task Change_customer_clears_pick_and_row_marker()
    {
        var (_, vm, _) = await OpenAsync();
        vm.Search = "bea";
        await vm.SuggestNowAsync();
        var bea = vm.Suggestions.Single();
        vm.ChooseSuggestionCommand.Execute(bea);
        await Task.Delay(50);

        Assert.Equal("No membership", vm.OrderWhoStatus);
        Assert.Equal("0917 555 0142 · bea@demo.io", vm.OrderWhoContact);
        Assert.True(vm.Hits.Single().IsChosen);

        vm.ChangeCustomerCommand.Execute(null);
        Assert.Null(vm.Member);
        Assert.Equal("", vm.Search);
        Assert.False(vm.Hits.Single().IsChosen);
        Assert.True(vm.ShowChooseCustomerEmpty);
    }
}
