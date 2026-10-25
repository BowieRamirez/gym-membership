using System.Reflection;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

public class AdminOverviewTests
{
    public class Reads : DispatchProxy
    {
        public readonly Dictionary<string, object> Results = new();
        public readonly List<(string Method, object?[] Args)> Calls = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add((method!.Name, args ?? []));
            return Results.TryGetValue(method.Name, out var result) ? result : throw new InvalidOperationException("Unexpected operation: " + method.Name);
        }
    }

    static (AdminOverviewViewModel Vm, Reads Admin, Reads Subs) Create()
    {
        var admin = DispatchProxy.Create<IAdminService, Reads>();
        var subs = DispatchProxy.Create<ISubscriptionService, Reads>();
        var a = (Reads)(object)admin;
        var s = (Reads)(object)subs;
        Set(a, "UsersAsync", (IReadOnlyList<UserRow>)[]);
        Set(a, "AllPackagesAsync", (IReadOnlyList<MembershipPackage>)[]);
        Set(a, "AllAmenitiesAsync", (IReadOnlyList<Amenity>)[]);
        Set(a, "DiscountsAsync", (IReadOnlyList<DiscountRow>)[]);
        Set(a, "RevenueAsync", (IReadOnlyList<RevenueRow>)[]);
        Set(a, "AuditAsync", (IReadOnlyList<AuditRow>)[]);
        Set(a, "GymSettingsAsync", new GymSettings { GymName = "Test gym" });
        Set(s, "RefundsAsync", (IReadOnlyList<RefundCard>)[]);
        return (new AdminOverviewViewModel(admin, subs) { Clock = () => DateTimeOffset.Parse("2026-10-31T16:30:00Z") }, a, s);
    }

    static void Set<T>(Reads reads, string name, T value) => reads.Results[name] = Task.FromResult(AppResult<T>.Success(value));
    static AdminOverviewCard Card(AdminOverviewViewModel vm, string key) => vm.Cards.Single(c => c.Key == key);

    [Fact]
    public async Task Overview_covers_only_the_eight_admin_pages_and_reads_without_writing()
    {
        var (vm, a, s) = Create();
        await vm.RefreshAsync();
        Assert.Equal(new[] { "users", "catalog", "discounts", "refunds", "revenue", "void", "gym", "audit" }, vm.Cards.Select(c => c.Key));
        Assert.All(vm.Cards, c => Assert.False(c.IsUnavailable));
        Assert.Equal(new[] { "RefundsAsync" }, s.Calls.Select(c => c.Method));
        Assert.All(a.Calls, c => Assert.Contains(c.Method, new[] { "UsersAsync", "AllPackagesAsync", "AllAmenitiesAsync", "DiscountsAsync", "RevenueAsync", "GymSettingsAsync", "AuditAsync" }));
        Assert.Equal("Open page", Card(vm, "void").Value);
    }

    [Fact]
    public async Task Revenue_uses_Manila_today_and_month_start_and_preserves_negative_net_revenue()
    {
        var (vm, a, _) = Create();
        Set(a, "RevenueAsync", (IReadOnlyList<RevenueRow>)[new(new DateTime(2026, 11, 1), "membership", "cash", 1, -250m)]);
        await vm.RefreshAsync();
        var calls = a.Calls.Where(c => c.Method == "RevenueAsync").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(new DateOnly(2026, 11, 1), c.Args[0]));
        Assert.All(calls, c => Assert.Equal(new DateOnly(2026, 11, 1), c.Args[1]));
        Assert.Contains("-250.00", Card(vm, "revenue").Value);
        Assert.Contains("Manila", vm.UpdatedText);
    }

    [Fact]
    public async Task Discounts_exclude_future_expired_and_disabled_codes()
    {
        var (vm, a, _) = Create();
        var now = vm.Clock().UtcDateTime;
        Set(a, "DiscountsAsync", (IReadOnlyList<DiscountRow>)[
            new(new Discount { IsActive = true, StartsAt = now.AddDays(-1), ExpiresAt = now.AddDays(1) }, "active"),
            new(new Discount { IsActive = true, StartsAt = now.AddDays(1), ExpiresAt = now.AddDays(2) }, "active"),
            new(new Discount { IsActive = true, StartsAt = now.AddDays(-2), ExpiresAt = now }, "expired"),
            new(new Discount { IsActive = false, StartsAt = now.AddDays(-1), ExpiresAt = now.AddDays(1) }, "inactive")]);
        await vm.RefreshAsync();
        Assert.Equal("1 usable code", Card(vm, "discounts").Value);
    }

    [Fact]
    public async Task Refund_reviews_and_repayments_are_distinct_and_closed_requests_are_excluded()
    {
        var (vm, _, s) = Create();
        Set(s, "RefundsAsync", (IReadOnlyList<RefundCard>)[
            new(new RefundRequest { Status = "pending" }, "A", "Plan", ""),
            new(new RefundRequest { Status = "approved" }, "B", "Plan", ""),
            new(new RefundRequest { Status = "paid" }, "C", "Plan", ""),
            new(new RefundRequest { Status = "rejected" }, "D", "Plan", "")]);
        await vm.RefreshAsync();
        Assert.Equal("1 awaiting review", Card(vm, "refunds").Value);
        Assert.Contains("1 awaiting manual repayment", Card(vm, "refunds").Detail);
    }

    [Fact]
    public async Task A_failed_summary_is_unavailable_not_zero_and_a_refresh_recovers()
    {
        var (vm, a, _) = Create();
        a.Results["UsersAsync"] = Task.FromResult(AppResult<IReadOnlyList<UserRow>>.Fail(AppErrorKind.Offline));
        await vm.RefreshAsync();
        Assert.Equal("Unavailable", Card(vm, "users").Value);
        Assert.True(Card(vm, "users").IsUnavailable);
        Assert.False(Card(vm, "catalog").IsUnavailable);
        Assert.Contains("unavailable", vm.UpdatedText);
        Set(a, "UsersAsync", (IReadOnlyList<UserRow>)[new("u1", "Alex", true, ["member", "coach"])]);
        await vm.RefreshAsync();
        Assert.False(Card(vm, "users").IsUnavailable);
        Assert.Equal("1 account listed", Card(vm, "users").Value);
        Assert.Contains("1 active", Card(vm, "users").Detail);
    }

    [Fact]
    public async Task Partial_catalog_failure_does_not_present_a_partial_total()
    {
        var (vm, a, _) = Create();
        a.Results["AllAmenitiesAsync"] = Task.FromResult(AppResult<IReadOnlyList<Amenity>>.Fail(AppErrorKind.Offline));
        await vm.RefreshAsync();
        Assert.True(Card(vm, "catalog").IsUnavailable);
        Assert.Equal("Unavailable", Card(vm, "catalog").Value);
    }

    [Fact]
    public async Task Audit_shows_five_newest_loaded_events_and_empty_state()
    {
        var (vm, a, _) = Create();
        await vm.RefreshAsync();
        Assert.Equal("No activity in the loaded audit list.", vm.RecentActivity);
        Set(a, "AuditAsync", (IReadOnlyList<AuditRow>)Enumerable.Range(1, 7).Select(i => new AuditRow(new AuditEntry { Action = "event-" + i, CreatedAt = new DateTime(2026, 10, i, 18, 0, 0, DateTimeKind.Utc) }, "Owner")).ToArray());
        await vm.RefreshAsync();
        Assert.Contains("event-7", vm.RecentActivity);
        Assert.DoesNotContain("event-2", vm.RecentActivity);
        Assert.True(vm.RecentActivity.IndexOf("event-7") < vm.RecentActivity.IndexOf("event-6"));
        Assert.Contains("08 Oct", vm.RecentActivity);
    }

    [Fact]
    public async Task Month_to_date_starts_on_the_first_not_today()
    {
        var (vm, a, _) = Create();
        vm.Clock = () => DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        await vm.RefreshAsync();
        var calls = a.Calls.Where(c => c.Method == "RevenueAsync").ToList();
        Assert.Contains(calls, c => Equals(c.Args[0], new DateOnly(2026, 10, 17)) && Equals(c.Args[1], new DateOnly(2026, 10, 17)));
        Assert.Contains(calls, c => Equals(c.Args[0], new DateOnly(2026, 10, 1)) && Equals(c.Args[1], new DateOnly(2026, 10, 17)));
    }

    [Fact]
    public async Task Failed_refresh_clears_old_revenue_and_audit_instead_of_leaving_stale_success()
    {
        var (vm, a, _) = Create();
        Set(a, "AuditAsync", (IReadOnlyList<AuditRow>)[new(new AuditEntry { Action = "old-event", CreatedAt = DateTime.UtcNow }, "Owner")]);
        await vm.RefreshAsync();
        Assert.Contains("old-event", vm.RecentActivity);
        a.Results["AuditAsync"] = Task.FromResult(AppResult<IReadOnlyList<AuditRow>>.Fail(AppErrorKind.Offline));
        a.Results["RevenueAsync"] = Task.FromException<AppResult<IReadOnlyList<RevenueRow>>>(new HttpRequestException("offline"));
        await vm.RefreshAsync();
        Assert.DoesNotContain("old-event", vm.RecentActivity);
        Assert.Equal("Unavailable", Card(vm, "revenue").Value);
        Assert.True(Card(vm, "audit").IsUnavailable);
        Assert.False(vm.IsBusy);
        Assert.Contains("2 summaries unavailable", vm.UpdatedText);
    }

    [Fact]
    public async Task Existing_admin_services_load_the_overview_against_the_demo_backend()
    {
        var gateway = new GymMembership.Core.Demo.DemoGateway();
        await gateway.SignInAsync("admin@demo.io", GymMembership.Core.Demo.DemoGateway.DemoPassword);
        var vm = new AdminOverviewViewModel(new AdminService(gateway), new SubscriptionService(gateway));
        await vm.OnAppearingAsync();
        Assert.All(vm.Cards, c => Assert.False(c.IsUnavailable, c.Title + ": " + c.Detail));
        Assert.DoesNotContain("Loading", vm.UpdatedText);
    }

    [Fact]
    public async Task A_sub_admin_sees_only_the_cards_for_their_pages_and_none_of_them_fail()
    {
        var gateway = new GymMembership.Core.Demo.DemoGateway();
        await gateway.SignInAsync("subadmin@demo.io", GymMembership.Core.Demo.DemoGateway.DemoPassword);
        var vm = new AdminOverviewViewModel(new AdminService(gateway), new SubscriptionService(gateway)) { SuperAdmin = false };
        Assert.Equal(["users", "catalog", "discounts", "revenue"], vm.Shown.Select(c => c.Key));
        await vm.OnAppearingAsync();
        Assert.All(vm.Shown, c => Assert.False(c.IsUnavailable, c.Title + ": " + c.Detail));
        Assert.DoesNotContain("unavailable", vm.UpdatedText);
    }

    [Fact]
    public async Task A_second_refresh_is_ignored_until_the_current_one_finishes()
    {
        var (vm, a, _) = Create();
        var pending = new TaskCompletionSource<AppResult<IReadOnlyList<UserRow>>>();
        a.Results["UsersAsync"] = pending.Task;
        var first = vm.RefreshAsync();
        Assert.True(vm.IsBusy);
        await vm.RefreshAsync();
        Assert.Single(a.Calls, c => c.Method == "UsersAsync");
        pending.SetResult(AppResult<IReadOnlyList<UserRow>>.Success([]));
        await first;
        Assert.False(vm.IsBusy);
    }
}
