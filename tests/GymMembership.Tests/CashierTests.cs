using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CashierTests
{
    static async Task<(DemoGateway Gw, PaymentStaffService Svc)> Desk()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        return (gw, new PaymentStaffService(gw));
    }

    [Fact]
    public async Task Membership_items_are_the_active_packages_with_their_price_and_a_plain_name()
    {
        var (gw, svc) = await Desk();
        var items = (await svc.ItemsAsync(PayKind.Membership, null)).Value!;
        var packages = await gw.ListAsync<MembershipPackage>(p => p.IsActive == true);
        Assert.Equal(packages.Select(p => (p.Id, p.Name, (decimal?)p.Price)), items.Select(i => (i.Id, i.Label, i.Price)));
    }

    [Fact]
    public async Task Amenity_items_are_the_active_amenities_with_their_price_and_a_plain_name()
    {
        var (gw, svc) = await Desk();
        var items = (await svc.ItemsAsync(PayKind.Amenity, null)).Value!;
        var amenities = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        Assert.Equal(amenities.Select(a => (a.Id, a.Name, (decimal?)a.Price)), items.Select(i => (i.Id, i.Label, i.Price)));
    }

    [Fact]
    public async Task A_renewal_item_carries_the_price_of_its_package()
    {
        var (gw, svc) = await Desk();
        var plan = (await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == "u-alex")).First();
        var price = (await gw.ListAsync<MembershipPackage>(p => p.Id == plan.MembershipPackageId)).Single().Price;
        var item = (await svc.ItemsAsync(PayKind.Renewal, "u-alex")).Value!.First(i => i.Id == plan.Id);
        Assert.Equal(price, item.Price);
        Assert.DoesNotContain("₱", item.Label);
    }

    static async Task<(DemoGateway Gw, CashierViewModel Vm)> Open()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task ChooseMember(CashierViewModel vm, string name)
    {
        vm.Search = name; await vm.FindCommand.ExecuteAsync(null);
        vm.Member = vm.Hits.Single();
    }

    static string Peso(decimal amount) => $"₱{amount:N2}";

    [Fact]
    public async Task It_opens_on_memberships_as_cards_with_prices()
    {
        var (gw, vm) = await Open();
        var packages = await gw.ListAsync<MembershipPackage>(p => p.IsActive == true);
        Assert.Equal("Membership", vm.Kind);
        Assert.Equal(packages.Select(p => p.Name), vm.Options.Select(o => o.Name));
        Assert.Equal(packages.Select(p => Peso(p.Price)), vm.Options.Select(o => o.PriceText));
        Assert.All(vm.Options, o => Assert.False(o.IsPicked));
    }

    [Fact]
    public async Task Picking_a_card_marks_only_that_one_and_shows_its_price_as_the_total()
    {
        var (_, vm) = await Open();
        vm.PickItemCommand.Execute(vm.Options[0]);
        Assert.True(vm.Options[0].IsPicked);
        Assert.Equal(vm.Options[0].PriceText, vm.TotalText);
        vm.PickItemCommand.Execute(vm.Options[1]);
        Assert.False(vm.Options[0].IsPicked); Assert.True(vm.Options[1].IsPicked);
        Assert.Equal(vm.Options[1].PriceText, vm.TotalText);
    }

    [Fact]
    public async Task Changing_what_is_being_paid_for_drops_the_picked_item_and_loads_that_kinds_items()
    {
        var (gw, vm) = await Open();
        vm.PickItemCommand.Execute(vm.Options[0]);
        vm.PickKindCommand.Execute("Amenity");
        Assert.Null(vm.Item);
        var amenities = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        Assert.Equal(amenities.Select(a => a.Name), vm.Options.Select(o => o.Name));
        Assert.Equal(Peso(0), vm.TotalText);
    }

    [Fact]
    public async Task A_renewal_needs_a_member_first_and_dropping_the_member_drops_the_pick()
    {
        var (_, vm) = await Open();
        vm.PickKindCommand.Execute("Renewal");
        Assert.Empty(vm.Options);
        Assert.Equal("Choose a member to see their memberships.", vm.ItemsEmptyText);

        await ChooseMember(vm, "Alex");
        Assert.NotEmpty(vm.Options);
        vm.PickItemCommand.Execute(vm.Options[0]);
        Assert.NotNull(vm.Item);

        await ChooseMember(vm, "Bea");      // a different member: the renewal belonged to Alex
        Assert.Null(vm.Item);
    }

    [Fact]
    public async Task Complete_payment_is_available_only_when_the_order_is_complete()
    {
        var (_, vm) = await Open();
        Assert.False(vm.RecordCommand.CanExecute(null));          // nothing chosen
        await ChooseMember(vm, "Carlo");
        Assert.False(vm.RecordCommand.CanExecute(null));          // a member but no item
        vm.PickItemCommand.Execute(vm.Options[0]);
        Assert.True(vm.RecordCommand.CanExecute(null));           // cash needs no reference

        vm.PickMethodCommand.Execute("GCash");
        Assert.False(vm.RecordCommand.CanExecute(null));          // GCash needs a reference
        vm.ReferenceNo = "GC-1001";
        Assert.True(vm.RecordCommand.CanExecute(null));
        vm.ReferenceNo = "  ";
        Assert.False(vm.RecordCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_walk_in_needs_a_name_and_no_member_or_item()
    {
        var (_, vm) = await Open();
        vm.PickKindCommand.Execute("Walk-in");
        Assert.Empty(vm.Options);
        Assert.Equal("Day pass", vm.TotalText);
        Assert.False(vm.RecordCommand.CanExecute(null));
        vm.WalkInName = "Pedro Ramos";
        Assert.True(vm.RecordCommand.CanExecute(null));
        Assert.Equal("Pedro Ramos", vm.OrderWho);
    }

    [Fact]
    public async Task Completing_a_payment_records_it_says_so_and_clears_the_order()
    {
        var (gw, vm) = await Open();
        var before = (await gw.ListAsync<Payment>(p => p.MemberId == "u-carlo" && p.Status == "verified")).Count;   // the demo already has some
        await ChooseMember(vm, "Carlo");
        vm.PickItemCommand.Execute(vm.Options[0]);
        await vm.RecordCommand.ExecuteAsync(null);

        Assert.StartsWith("Payment complete: ", vm.Notice);
        Assert.Contains("Receipt", vm.Notice);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Item);
        Assert.False(vm.RecordCommand.CanExecute(null));
        Assert.Equal(before + 1, (await gw.ListAsync<Payment>(p => p.MemberId == "u-carlo" && p.Status == "verified")).Count);
        Assert.NotEmpty(vm.CashToday);
    }

    [Fact]
    public async Task A_double_tap_on_Complete_payment_records_one_payment()
    {
        var (gw, vm) = await Open();
        var before = (await gw.ListAsync<Payment>(p => p.MemberId == "u-carlo" && p.Status == "verified")).Count;
        await ChooseMember(vm, "Carlo");
        vm.PickItemCommand.Execute(vm.Options[0]);
        var first = vm.RecordCommand.ExecuteAsync(null);
        var second = vm.RecordCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);
        Assert.Equal(before + 1, (await gw.ListAsync<Payment>(p => p.MemberId == "u-carlo" && p.Status == "verified")).Count);   // one new payment, not two
    }

    [Fact]
    public async Task The_discount_hint_appears_only_when_a_code_is_typed()
    {
        var (_, vm) = await Open();
        Assert.Equal("", vm.TotalNote);
        vm.DiscountCode = "WELCOME10";
        Assert.Equal("The discount code is checked when you complete the payment.", vm.TotalNote);   // honest: there is no preview, the server applies it
        vm.DiscountCode = "";
        Assert.Equal("", vm.TotalNote);
    }

    // an items service whose answers the test releases by hand, to make two kinds answer out of order
    sealed class SlowItems : IPaymentStaffService
    {
        public readonly Queue<(string Kind, TaskCompletionSource<AppResult<IReadOnlyList<CounterItem>>> Reply)> Waiting = new();
        public Task<AppResult<IReadOnlyList<CounterItem>>> ItemsAsync(string kind, string? memberId)
        { var tcs = new TaskCompletionSource<AppResult<IReadOnlyList<CounterItem>>>(); Waiting.Enqueue((kind, tcs)); return tcs.Task; }
        public Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync() => throw new NotSupportedException();
        public Task<AppResult<IReadOnlyList<PaymentRow>>> RecentAsync() => throw new NotSupportedException();
        public Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve, string? reason = null) => throw new NotSupportedException();
        public Task<AppResult<Payment>> RecordCounterAsync(string kind, string method, string? memberId, int? itemId, string? walkInName, string? referenceNo, string? discountCode) => throw new NotSupportedException();
        public Task<AppResult<bool>> VoidAsync(int paymentId, string reason) => throw new NotSupportedException();
        public Task<AppResult<IReadOnlyList<CashRow>>> CashSummaryAsync(DateOnly day) => Task.FromResult(AppResult<IReadOnlyList<CashRow>>.Success([]));
        public Task<AppResult<string?>> ProofUrlAsync(string proofPath) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_slow_answer_for_a_kind_the_cashier_already_left_does_not_replace_the_items()
    {
        var slow = new SlowItems();
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(slow, new AttendanceService(gw));
        var opening = vm.OnAppearingAsync();               // asks for Membership items
        vm.PickKindCommand.Execute("Amenity");             // then moves on and asks for Amenity items
        var membership = slow.Waiting.Dequeue(); var amenity = slow.Waiting.Dequeue();

        amenity.Reply.SetResult(AppResult<IReadOnlyList<CounterItem>>.Success([new CounterItem(7, "Pool", 300m)]));
        membership.Reply.SetResult(AppResult<IReadOnlyList<CounterItem>>.Success([new CounterItem(1, "Monthly", 1500m)]));   // arrives last, for the wrong kind
        await opening;

        Assert.Equal(new[] { "Pool" }, vm.Options.Select(o => o.Name));
    }

    static AppResult<IReadOnlyList<CounterItem>> Items(params CounterItem[] items) => AppResult<IReadOnlyList<CounterItem>>.Success(items);

    [Fact]
    public async Task While_the_new_kind_loads_the_old_cards_are_gone_so_they_cannot_be_tapped()
    {
        var slow = new SlowItems();
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(slow, new AttendanceService(gw));
        var opening = vm.OnAppearingAsync();
        slow.Waiting.Dequeue().Reply.SetResult(Items(new CounterItem(3, "Gold", 5000m)));
        await opening;
        vm.PickItemCommand.Execute(vm.Options.Single());
        Assert.NotNull(vm.Item);

        vm.PickKindCommand.Execute("Amenity");                 // the amenity list has not come back yet
        Assert.Empty(vm.Options);                              // so there is no membership card left to tap
        Assert.Null(vm.Item);
        Assert.False(vm.RecordCommand.CanExecute(null));
        Assert.Equal("Loading…", vm.ItemsEmptyText);           // not "Nothing to sell here yet."

        slow.Waiting.Dequeue().Reply.SetResult(Items(new CounterItem(7, "Pool", 300m)));
        Assert.Equal(new[] { "Pool" }, vm.Options.Select(o => o.Name));
        Assert.Equal("Nothing to sell here yet.", vm.ItemsEmptyText);
    }

    [Fact]
    public async Task A_stale_failure_for_a_kind_the_cashier_left_shows_no_error()
    {
        var slow = new SlowItems();
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(slow, new AttendanceService(gw));
        var opening = vm.OnAppearingAsync();
        vm.PickKindCommand.Execute("Amenity");
        var membership = slow.Waiting.Dequeue(); var amenity = slow.Waiting.Dequeue();

        amenity.Reply.SetResult(Items(new CounterItem(7, "Pool", 300m)));
        membership.Reply.SetResult(AppResult<IReadOnlyList<CounterItem>>.Fail(AppErrorKind.Validation, "bad_input"));   // fails late, for a screen already left
        await opening;

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new[] { "Pool" }, vm.Options.Select(o => o.Name));
    }

    [Fact]
    public async Task Coming_back_to_the_cashier_does_not_keep_a_pick_that_no_card_shows()
    {
        var (_, vm) = await Open();
        vm.PickItemCommand.Execute(vm.Options[0]);
        Assert.NotNull(vm.Item);
        await vm.OnAppearingAsync();                           // the page appears again and reloads its cards
        Assert.Null(vm.Item);
        Assert.All(vm.Options, o => Assert.False(o.IsPicked));
    }

    [Fact]
    public async Task The_order_panel_names_the_kind_and_how_it_is_paid_in_words()
    {
        var (_, vm) = await Open();
        Assert.Equal("Membership, paid with Cash", vm.OrderHow);
        vm.PickKindCommand.Execute("Walk-in");
        vm.PickMethodCommand.Execute("GCash");
        Assert.Equal("Walk-in, paid with GCash", vm.OrderHow);
    }
}
