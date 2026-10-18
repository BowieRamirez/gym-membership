# Phase 5: front desk Home and Cashier. Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Front desk and admin land on a Home screen of four live tiles (check-ins today, payments to verify, amenity status, messages), each a shortcut to the screen that handles it, and "Counter payment" becomes a "Cashier" with an icon grid of what is being paid for, item cards with prices, an order panel and one clear "Complete payment" button.

**Architecture:** Two small, independent pieces on the existing structure. (1) `IDeskHomeService` reads numbers the app already has access to (check-ins, pending payments, amenities, chat threads) and `HomeViewModel` turns them into four immutable `HomeTile` records; tiles refresh quietly on a timer. (2) `CounterViewModel` is replaced by `CashierViewModel` over the same `IPaymentStaffService.RecordCounterAsync`; items gain a price so the order panel can show a total. No database change, no new RPC, no change to payment rules.

**Tech Stack:** C# / .NET 10 MAUI, CommunityToolkit.Mvvm, xUnit, the in-memory `DemoGateway` for tests.

**Spec:** `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 5, section 5 item 5; decision 5 "one item per order for now"). Design choices confirmed by the owner on 17 Oct 2026: Home is shown to both front desk and admin; icons are emoji (no new files); a tile is a count plus a short label.

## Corrections to the list the owner approved

1. **The Cashier total is the item's price, before any discount.** The discount code is checked by the server when the payment is completed; there is no preview and adding one would need a new database function. The order panel says so, and the "Payment complete" notice shows the amount actually charged.
2. **Walk-in total reads "Day pass".** The day pass price lives in Gym settings and is applied by the server.

## Design (frontend-design pass)

The app already has a visual identity from phases 1 to 4: Barlow type, Plate colours, flat hairline panels, a gym-signage condensed numeral, no shadows. Phase 5 must look like the rest, so the tokens below are the existing ones, not new choices.

- **Colour:** `Surface` panels with `Line` hairline; `Ink` for the one thing that needs attention; `Chalk` for the selected card; `PlateGreen` only for the online/notice meaning it already has. No new colours.
- **Type:** `Title` for the page heading, `Numeral` (Barlow Condensed) for the count, `BarlowSemiBold` for names and `Caption`/`Muted` for detail. One scale, the app's.
- **Layout (Home):** a wrapping grid of equal tiles, two across on a phone (156 wide), three or four on Windows (210 wide), left aligned under the title.

```
Today at the desk
Tap a number to open the screen that handles it.
+----------------+ +----------------+
| 🏃             | | 🧾             |      <- tile needing attention has an
| 12             | | 3              |         Ink 2px outline AND says so in words
| Check-ins today| | Payments to    |
| Tap to check   | | verify         |
| someone in     | | 3 payments are |
+----------------+ | waiting        |
                   +----------------+
```
- **Layout (Cashier):** one column, top to bottom in the order the work happens: what is it for (four buttons), who (search + pick), which item (cards), the order panel (summary, how paid, reference, discount, total, Complete payment), then "Cash taken today".
- **The memorable thing:** the *attention outline*. A quiet grid where only tiles with something to do carry the Ink outline, and every outline is also stated in words ("3 payments are waiting"), so colour and weight are never the only signal. Everything else stays flat.
- **Review against the brief:** the first idea was a tinted banner behind each count. It reads as a generic dashboard kit and adds a colour the app does not have; replaced by the outline. Emoji icons stay because the owner asked for icon-based with no new files; each has a text label and a `SemanticProperties.Description`.
- **Copy:** plain verbs, sentence case, one name per action: the button says "Complete payment" and the notice says "Payment complete".

## Global Constraints

- Existing tests stay green: `dotnet test tests/GymMembership.Tests` (336 at the start of this phase) and `cd supabase/tests/local && npm test` (110; unchanged, no migration in this phase).
- **No database change.** No new migration, RPC, table or permission.
- **Payment rules unchanged.** `record_counter_payment` is called exactly as today; the Cashier only changes how its arguments are chosen.
- **Money path guards:** the item charged must belong to the kind shown; a payment cannot be completed twice by a double tap; a payment cannot be completed without the member and item (or the walk-in name), nor a non-cash method without a reference.
- XAML bindings are checked by `XamlBindingTests`: every `{Binding X}` must be a public instance property of the page's view model or item type.
- List-row taps use `BasePage.Row<TVm>` with a plain `Tapped`/`Clicked` handler, never `RelativeSource AncestorType` command bindings (they throw on list reload).
- Match the file you edit: tabs in `*.xaml.cs` under `GymMembership.App`, 4 spaces in `GymMembership.Core`, short `//` comments that say why.
- Do not push, merge or delete branches; do not touch `docs/screenshotsforexample/`.

## Review Focus

1. **Double tap on "Complete payment"** must create one payment, not two (Task 4 pins it).
2. **Switching what is being paid for after picking an item** must drop the picked item; the old item's id must never be sent with the new kind (Task 4).
3. **A slow answer for a kind the cashier already left** must not replace the items on screen (Task 4).
4. **Changing the member after picking a renewal** must drop the picked renewal, which belongs to the other member (Task 4).
5. **A failed refresh of the Home tiles** must keep the numbers on screen and show no new error, while a failed first load shows one (Task 3).

## File structure

| File | Change | Responsibility |
| --- | --- | --- |
| `src/GymMembership.Core/Services/DeskHome.cs` | create | `DeskSummary`, `IDeskHomeService`, `DeskHomeService` |
| `src/GymMembership.Core/Services/Services.cs` | modify | `CounterItem` gains `Price`; `ItemsAsync` fills it and shortens labels to the name |
| `src/GymMembership.Core/ViewModels/StaffViewModels.cs` | modify | add `HomeTile`, `HomeViewModel`, `ItemOption`; replace `CounterViewModel` with `CashierViewModel` |
| `src/GymMembership.App/Views/HomePage.xaml` + `.xaml.cs` | create | the tile grid |
| `src/GymMembership.App/Views/CashierPage.xaml` + `.xaml.cs` | create | the cashier |
| `src/GymMembership.App/Views/CounterPage.xaml` + `.xaml.cs` | delete | replaced by Cashier |
| `src/GymMembership.App/StaffShell.xaml` + `.xaml.cs` | modify | Home first, Cashier, routes, phone bottom bar, `ShowDeskPage` |
| `src/GymMembership.App/MauiProgram.cs` | modify | register `IDeskHomeService` |
| `tests/GymMembership.Tests/DeskHomeTests.cs` | create | service and `HomeViewModel` |
| `tests/GymMembership.Tests/CashierTests.cs` | create | items with prices, `CashierViewModel` |
| `tests/GymMembership.Tests/SupabaseGatewayTests.cs` | modify | prove the check-in date filter translates to a server query |

---

### Task 1: Items carry a price

**Files:**
- Modify: `src/GymMembership.Core/Services/Services.cs:269` (`CounterItem`) and `:336-355` (`ItemsAsync`)
- Test: `tests/GymMembership.Tests/CashierTests.cs`

**Interfaces:**
- Produces: `CounterItem(int Id, string Label, decimal? Price = null)`. For membership and amenity items `Label` is the plain name (no price in it) and `Price` is the price; for renewals `Label` is `"<package>  (<status>, ends <date>)"` and `Price` is that package's price.

- [ ] **Step 1: Write the failing test.** Create `tests/GymMembership.Tests/CashierTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter CashierTests`
Expected: build error, `CounterItem` has no `Price`.

- [ ] **Step 3: Implement.** In `Services.cs` replace the record:

```csharp
public sealed record CounterItem(int Id, string Label, decimal? Price = null);
```
and in `ItemsAsync` replace the three `select` lines:

```csharp
            case PayKind.Membership:
                return (await gw.ListAsync<MembershipPackage>(p => p.IsActive == true)).Select(p => new CounterItem(p.Id, p.Name, p.Price)).ToList();
            case PayKind.Amenity:
                return (await gw.ListAsync<Amenity>(a => a.IsActive == true)).Select(a => new CounterItem(a.Id, a.Name, a.Price)).ToList();
            case PayKind.Renewal:
                var packages = await gw.ListAsync<MembershipPackage>();
                return (await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == memberId && (m.Status == "active" || m.Status == "expired")))
                    .Select(m =>
                    {
                        var package = packages.FirstOrDefault(p => p.Id == m.MembershipPackageId);
                        return new CounterItem(m.Id, $"{package?.Name ?? "Membership"}  ({m.Status}, ends {m.EndsAt:d MMM yyyy})", package?.Price);
                    }).ToList();
```
(Keep whatever follows the Renewal case in the method unchanged.)

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass, including `SupabaseGatewayTests.Every_filter_the_services_use_translates_to_a_server_query` (the renewal filter is unchanged).

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/Services/Services.cs tests/GymMembership.Tests/CashierTests.cs
git commit -m "feat: cashier items carry their price"
```

---

### Task 2: The desk summary service

**Files:**
- Create: `src/GymMembership.Core/Services/DeskHome.cs`
- Modify: `tests/GymMembership.Tests/SupabaseGatewayTests.cs` (inside `Every_filter_the_services_use_translates_to_a_server_query`, before the final `Assert.True`)
- Test: `tests/GymMembership.Tests/DeskHomeTests.cs`

**Interfaces:**
- Consumes: `ISupabaseGateway.ListAsync<GymCheckIn|Payment|Amenity>`, `IChatService.ThreadsAsync` (existing).
- Produces: `DeskSummary(int CheckInsToday, int PaymentsToVerify, int AmenitiesAvailable, int AmenitiesInUse, int AmenitiesUnavailable, int UnreadMessages, int UnclaimedChats)` with `AmenitiesTotal`; `IDeskHomeService.SummaryAsync() : Task<AppResult<DeskSummary>>`; `DeskHomeService(ISupabaseGateway, IChatService)`.

- [ ] **Step 1: Write the failing tests.** Create `tests/GymMembership.Tests/DeskHomeTests.cs`:

```csharp
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
        var (gw, chat, svc) = await As("employee@demo.io");
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
    public async Task A_member_own_front_desk_thread_is_not_counted_as_an_inbox_message()
    {
        var (gw, _, svc) = await As("carlo@demo.io");   // a member has a Front desk thread but no inbox
        var s = (await svc.SummaryAsync()).Value!;
        Assert.Equal(0, s.UnreadMessages);
        Assert.Equal(0, s.UnclaimedChats);
    }
}
```
Add to `SupabaseGatewayTests.cs`, just before `Assert.True(results.All(...))`:

```csharp
        Note("desk home", await new DeskHomeService(gw, new ChatService(gw, new NotificationService(gw))).SummaryAsync());
        Assert.Contains(_seen, r => r.Path.EndsWith("/gym_check_ins") && r.Query.Contains("checked_in_at"));   // today's check-ins are filtered on the server
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter "DeskHomeTests|Every_filter"`
Expected: build error, `DeskHomeService` / `DeskSummary` not defined.

- [ ] **Step 3: Implement.** Create `src/GymMembership.Core/Services/DeskHome.cs`:

```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>What the front desk home shows: today's numbers. Each one is a shortcut to the screen that handles it.</summary>
public sealed record DeskSummary(int CheckInsToday, int PaymentsToVerify, int AmenitiesAvailable, int AmenitiesInUse, int AmenitiesUnavailable,
                                 int UnreadMessages, int UnclaimedChats)
{
    public int AmenitiesTotal => AmenitiesAvailable + AmenitiesInUse + AmenitiesUnavailable;
}

public interface IDeskHomeService { Task<AppResult<DeskSummary>> SummaryAsync(); }

public sealed class DeskHomeService(ISupabaseGateway gw, IChatService chat) : IDeskHomeService
{
    public async Task<AppResult<DeskSummary>> SummaryAsync()
    {
        var threads = await chat.ThreadsAsync();
        if (!threads.Ok) return AppResult<DeskSummary>.Fail(threads.Error, threads.Message);

        return await Safe.RunAsync(async () =>
        {
            var startOfToday = DateTime.Today.ToUniversalTime();   // midnight where the desk is
            var checkIns = await gw.ListAsync<GymCheckIn>(c => c.CheckedInAt >= startOfToday);
            var pending = await gw.ListAsync<Payment>(p => p.Status == "pending");
            var amenities = await gw.ListAsync<Amenity>(a => a.IsActive == true);
            var inbox = threads.Value!.Where(t => t.IsSupport && t.WithId is not null).ToList();   // a member's own "Front desk" thread has no WithId
            return new DeskSummary(
                checkIns.Count, pending.Count,
                amenities.Count(a => a.Availability == "available"), amenities.Count(a => a.Availability == "in_use"), amenities.Count(a => a.Availability == "unavailable"),
                inbox.Sum(t => t.Unread), inbox.Count(t => t.Status == "open" && t.ClaimedBy is null));
        });
    }
}
```

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass. If `Every_filter_...` fails on the new `Assert.Contains`, print `_seen`'s `gym_check_ins` query (add `Console.WriteLine`) and change only the substring to what the library really sent (for example `checked_in_at.gte` or `checked_in_at=gte`); the point is to prove the date filter reaches the server. If the library refuses the date filter altogether, `Note("desk home", ...)` fails: then list `GymCheckIn` unfiltered and filter in memory (`ponytail: unfiltered until check-ins are many; add a server-side count function if this grows`).

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/Services/DeskHome.cs tests/GymMembership.Tests/DeskHomeTests.cs tests/GymMembership.Tests/SupabaseGatewayTests.cs
git commit -m "feat: desk summary service for the front desk home"
```

---

### Task 3: Home view model and tiles

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/StaffViewModels.cs` (add before `VerifyPaymentsViewModel`, under the `// ---------- front desk ----------` line)
- Test: `tests/GymMembership.Tests/DeskHomeTests.cs` (append)

**Interfaces:**
- Consumes: `IDeskHomeService`, `DeskSummary` (Task 2).
- Produces: `HomeTile(string Route, string Icon, string Title, string Count, string Detail, bool NeedsAttention)`; `HomeViewModel(IDeskHomeService)` with `Tiles`, `static Build(DeskSummary)`. Route keys, which Task 5 must give to the shell items: `checkin`, `verify`, `amenities`, `messages`.

- [ ] **Step 1: Write the failing tests.** Append inside `DeskHomeTests` (add `using GymMembership.Core.ViewModels;` already present):

```csharp
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
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new HomeViewModel(new DeskHomeService(gw, new ChatService(gw, new NotificationService(gw))));
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
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter DeskHomeTests`
Expected: build error, `HomeViewModel` / `HomeTile` not defined.

- [ ] **Step 3: Implement.** In `StaffViewModels.cs` add after the `// ---------- front desk ----------` comment:

```csharp
/// <summary>One tile on the front desk home. Route says which screen it opens (a StaffShell route).</summary>
public sealed record HomeTile(string Route, string Icon, string Title, string Count, string Detail, bool NeedsAttention);

public partial class HomeViewModel(IDeskHomeService svc) : LoadableViewModel
{
    public ObservableCollection<HomeTile> Tiles { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.SummaryAsync, s => Show(s));

    // Quiet: the error line is never touched and a failed look leaves the numbers as they were.
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        var result = await svc.SummaryAsync();
        if (result.Ok) Show(result.Value!);
    }

    void Show(DeskSummary s) => Sync(Tiles, Build(s), t => t.Route, (a, b) => a == b);

    public static IReadOnlyList<HomeTile> Build(DeskSummary s)
    {
        var amenityDetail = string.Join(", ", new[] { s.AmenitiesInUse > 0 ? $"{s.AmenitiesInUse} in use" : null, s.AmenitiesUnavailable > 0 ? $"{s.AmenitiesUnavailable} unavailable" : null }.OfType<string>());
        return
        [
            new("checkin", "🏃", "Check-ins today", s.CheckInsToday.ToString(),
                s.CheckInsToday == 0 ? "No one has checked in yet" : "Tap to check someone in", false),
            new("verify", "🧾", "Payments to verify", s.PaymentsToVerify.ToString(),
                s.PaymentsToVerify == 0 ? "All caught up" : s.PaymentsToVerify == 1 ? "1 payment is waiting" : $"{s.PaymentsToVerify} payments are waiting", s.PaymentsToVerify > 0),
            new("amenities", "🏊", "Amenities available", $"{s.AmenitiesAvailable} of {s.AmenitiesTotal}",
                amenityDetail.Length == 0 ? "All open" : amenityDetail, s.AmenitiesUnavailable > 0),
            new("messages", "💬", "Unread messages", s.UnreadMessages.ToString(),
                s.UnclaimedChats > 0 ? (s.UnclaimedChats == 1 ? "1 chat nobody has taken" : $"{s.UnclaimedChats} chats nobody has taken")
                : s.UnreadMessages > 0 ? "Open Messages to reply" : "No new messages", s.UnreadMessages > 0 || s.UnclaimedChats > 0),
        ];
    }
}
```

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass.

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/ViewModels/StaffViewModels.cs tests/GymMembership.Tests/DeskHomeTests.cs
git commit -m "feat: front desk home view model with four tiles"
```

---

### Task 4: The Cashier view model

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/StaffViewModels.cs` (replace `CounterViewModel`, currently lines 54-109)
- Test: `tests/GymMembership.Tests/CashierTests.cs` (append)

**Interfaces:**
- Consumes: `CounterItem.Price` (Task 1).
- Produces: `ItemOption(CounterItem)` with `Item`, `Name`, `PriceText`, `IsPicked`; `CashierViewModel(IPaymentStaffService, IAttendanceService)` with `Kinds`, `Methods`, `Hits`, `Options`, `CashToday`, `Kind`, `Method`, `Search`, `Member`, `Item`, `WalkInName`, `ReferenceNo`, `DiscountCode`, `CashTotal`, `IsWalkIn`, `NeedsMember`, `NeedsReference`, `OrderWho`, `OrderWhat`, `TotalText`, `TotalNote`, `ItemsEmptyText`, and commands `PickKindCommand(string)`, `PickMethodCommand(string)`, `PickItemCommand(ItemOption)`, `FindCommand`, `RecordCommand` (enabled only when the order is complete).

- [ ] **Step 1: Write the failing tests.** Append inside `CashierTests`:

```csharp
    static async Task<(DemoGateway Gw, CashierViewModel Vm)> Open()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task ChooseCarlo(CashierViewModel vm)
    {
        vm.Search = "Carlo"; await vm.FindCommand.ExecuteAsync(null);
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

        vm.Search = "Alex"; await vm.FindCommand.ExecuteAsync(null);
        vm.Member = vm.Hits.Single();
        Assert.NotEmpty(vm.Options);
        vm.PickItemCommand.Execute(vm.Options[0]);
        Assert.NotNull(vm.Item);

        vm.Search = "Bea"; await vm.FindCommand.ExecuteAsync(null);
        vm.Member = vm.Hits.Single();       // a different member: the renewal belonged to Alex
        Assert.Null(vm.Item);
    }

    [Fact]
    public async Task Complete_payment_is_available_only_when_the_order_is_complete()
    {
        var (_, vm) = await Open();
        Assert.False(vm.RecordCommand.CanExecute(null));          // nothing chosen
        await ChooseCarlo(vm);
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
        await ChooseCarlo(vm);
        vm.PickItemCommand.Execute(vm.Options[0]);
        await vm.RecordCommand.ExecuteAsync(null);

        Assert.StartsWith("Payment complete: ", vm.Notice);
        Assert.Contains("Receipt", vm.Notice);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Item);
        Assert.False(vm.RecordCommand.CanExecute(null));
        Assert.Single(await gw.ListAsync<Payment>(p => p.MemberId == "u-carlo" && p.Status == "verified"));
        Assert.NotEmpty(vm.CashToday);
    }

    [Fact]
    public async Task A_double_tap_on_Complete_payment_records_one_payment()
    {
        var (gw, vm) = await Open();
        await ChooseCarlo(vm);
        vm.PickItemCommand.Execute(vm.Options[0]);
        var first = vm.RecordCommand.ExecuteAsync(null);
        var second = vm.RecordCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);
        Assert.Single(await gw.ListAsync<Payment>(p => p.MemberId == "u-carlo" && p.Status == "verified"));
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
```
(Add `using GymMembership.Core.ViewModels;` if the file lacks it; it already has it from Task 1's header. `PaymentRow` and `CashRow` are in `GymMembership.Core.Services`.)

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter CashierTests`
Expected: build error, `CashierViewModel` / `ItemOption` not defined.

- [ ] **Step 3: Implement.** In `StaffViewModels.cs` replace the whole `CounterViewModel` class (and its leading `// front desk: cash and GCash...` comment) with:

```csharp
/// <summary>One thing for sale on the cashier's grid, and whether it is the one picked.</summary>
public partial class ItemOption(CounterItem item) : ObservableObject
{
    public CounterItem Item { get; } = item;
    public string Name => Item.Label;
    public string PriceText => Item.Price is { } p ? $"₱{p:N2}" : "";
    [ObservableProperty] private bool isPicked;
}

// front desk: cash, GCash and bank transfers at the counter, and walk-ins. Verified straight away with a receipt number
public partial class CashierViewModel(IPaymentStaffService svc, IAttendanceService members) : LoadableViewModel
{
    public string[] Kinds { get; } = ["Membership", "Renewal", "Amenity", "Walk-in"];
    public string[] Methods { get; } = ["Cash", "GCash", "Bank transfer"];
    public ObservableCollection<MemberHit> Hits { get; } = new();
    public ObservableCollection<ItemOption> Options { get; } = new();
    public ObservableCollection<CashRow> CashToday { get; } = new();
    int itemsTicket;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWalkIn), nameof(NeedsMember), nameof(NeedsReference), nameof(OrderWho), nameof(OrderWhat), nameof(TotalText), nameof(TotalNote), nameof(ItemsEmptyText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private string kind = "Membership";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsReference)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string method = "Cash";
    [ObservableProperty] private string search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWho), nameof(ItemsEmptyText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private MemberHit? member;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWhat), nameof(TotalText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private CounterItem? item;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWho)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string walkInName = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string referenceNo = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalNote))] private string discountCode = "";
    [ObservableProperty] private string cashTotal = "₱0.00";

    public bool IsWalkIn => Kind == "Walk-in";
    public bool NeedsMember => !IsWalkIn;
    public bool NeedsReference => Method != "Cash";

    // the order panel, in the words the cashier uses
    public string OrderWho => IsWalkIn ? (string.IsNullOrWhiteSpace(WalkInName) ? "Walk-in: type a name" : WalkInName.Trim()) : Member?.Username ?? "Find and choose a member";
    public string OrderWhat => Item?.Label ?? (IsWalkIn ? "Day pass" : "Choose what they are paying for");
    public string TotalText => IsWalkIn ? "Day pass" : Item?.Price is { } p ? $"₱{p:N2}" : "₱0.00";
    public string TotalNote => IsWalkIn ? "The day pass price is set in Gym settings."
        : string.IsNullOrWhiteSpace(DiscountCode) ? "" : "The discount code is checked when you complete the payment.";
    public string ItemsEmptyText => Kind == "Renewal" && Member is null ? "Choose a member to see their memberships." : "Nothing to sell here yet.";

    static string KindKey(string k) => k switch { "Renewal" => PayKind.Renewal, "Amenity" => PayKind.Amenity, "Walk-in" => PayKind.WalkIn, _ => PayKind.Membership };
    static string MethodKey(string m) => m switch { "GCash" => PayMethod.Gcash, "Bank transfer" => PayMethod.BankTransfer, _ => PayMethod.Cash };

    public override async Task OnAppearingAsync() { await RefreshItemsAsync(); await LoadCashAsync(); }

    // an item belongs to the kind (and member) it was listed for: change either and the pick goes
    partial void OnKindChanged(string value) { Item = null; _ = RefreshItemsAsync(); }
    partial void OnMemberChanged(MemberHit? value) { Item = null; if (Kind == "Renewal") _ = RefreshItemsAsync(); }
    partial void OnItemChanged(CounterItem? value) { foreach (var o in Options) o.IsPicked = value is not null && o.Item.Id == value.Id; }

    async Task RefreshItemsAsync()
    {
        var ticket = ++itemsTicket;   // the newest request wins: a slow answer for a kind you have left is dropped
        if (IsWalkIn) { Options.Clear(); return; }
        await RunAsync(() => svc.ItemsAsync(KindKey(Kind), Member?.MemberId), rows =>
        {
            if (ticket == itemsTicket) Fill(Options, rows.Select(r => new ItemOption(r)));
        });
    }

    async Task LoadCashAsync() =>
        await RunAsync(() => svc.CashSummaryAsync(DateOnly.FromDateTime(DateTime.Today)), rows =>
        { Fill(CashToday, rows); CashTotal = $"₱{rows.Sum(r => r.Total):N2}"; });

    [RelayCommand] private Task FindAsync() => RunAsync(() => members.FindMembersAsync(Search), rows => Fill(Hits, rows));
    [RelayCommand] private void PickKind(string value) => Kind = value;
    [RelayCommand] private void PickMethod(string value) => Method = value;
    [RelayCommand] private void PickItem(ItemOption option) => Item = option.Item;   // OnItemChanged marks the card

    bool CanComplete() =>
        (IsWalkIn ? !string.IsNullOrWhiteSpace(WalkInName) : Member is not null && Item is not null)
        && (!NeedsReference || !string.IsNullOrWhiteSpace(ReferenceNo));

    // The command is off while it runs, so a double tap records one payment.
    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task RecordAsync()
    {
        Notice = null;
        if (!IsWalkIn && (Member is null || Item is null)) { ErrorMessage = "Choose the member and what they are paying for."; return; }
        Payment? paid = null;
        if (!await RunAsync(() => svc.RecordCounterAsync(KindKey(Kind), MethodKey(Method), IsWalkIn ? null : Member!.MemberId, Item?.Id,
                IsWalkIn ? WalkInName : null, NeedsReference ? ReferenceNo : null, IsWalkIn ? null : DiscountCode), p => paid = p)) return;
        Notice = $"Payment complete: ₱{paid!.Amount:N2}. Receipt {paid.ReceiptNo}.";
        Item = null; ReferenceNo = ""; DiscountCode = ""; WalkInName = "";
        await LoadCashAsync();
    }
}
```

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass. (`CounterViewModel` has no other users in the code or tests; only `CounterPage` and the shell, replaced in Task 5, still name it, so the *app* project will not build until Task 5; the unit-test project does not reference the app.)

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/ViewModels/StaffViewModels.cs tests/GymMembership.Tests/CashierTests.cs
git commit -m "feat: cashier view model with item cards, order panel and a guarded Complete payment"
```

---

### Task 5: Pages, shell and wiring

**Files:**
- Create: `src/GymMembership.App/Views/HomePage.xaml`, `HomePage.xaml.cs`, `CashierPage.xaml`, `CashierPage.xaml.cs`
- Delete: `src/GymMembership.App/Views/CounterPage.xaml`, `CounterPage.xaml.cs`
- Modify: `src/GymMembership.App/StaffShell.xaml`, `StaffShell.xaml.cs`, `src/GymMembership.App/MauiProgram.cs`

**Interfaces:**
- Consumes: `HomeViewModel`, `HomeTile`, `CashierViewModel`, `ItemOption` (Tasks 3, 4); route keys `checkin`, `verify`, `amenities`, `messages`.
- Produces: `StaffShell.ShowDeskPage(string route)`; shell routes `home`, `verify`, `cashier`, `checkin`, `amenities`, `messages`.

- [ ] **Step 1: Home page.** Create `HomePage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<v:BasePage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
            xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
            xmlns:v="clr-namespace:GymMembership.App.Views"
            xmlns:c="clr-namespace:GymMembership.App.Controls"
            xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
            x:Class="GymMembership.App.Views.HomePage" x:DataType="vm:HomeViewModel" Title="Home">

    <ScrollView MaximumWidthRequest="920">
        <VerticalStackLayout Padding="20,16,20,24" Spacing="14">
            <Label Text="Today at the desk" Style="{StaticResource Title}" />
            <Label Text="Tap a number to open the screen that handles it." Style="{StaticResource Muted}" />
            <c:Banner Message="{Binding ErrorMessage}" />

            <!-- A tile with something to do carries an outline AND says what in words, so weight is never the only signal -->
            <FlexLayout Wrap="Wrap" BindableLayout.ItemsSource="{Binding Tiles}">
                <BindableLayout.ItemTemplate>
                    <DataTemplate x:DataType="vm:HomeTile">
                        <Border Style="{StaticResource Panel}" WidthRequest="{OnIdiom Phone=156, Default=210}" Margin="0,0,12,12"
                                SemanticProperties.Description="{Binding Title}" SemanticProperties.Hint="{Binding Detail}">
                            <Border.Triggers>
                                <DataTrigger TargetType="Border" Binding="{Binding NeedsAttention}" Value="True">
                                    <Setter Property="Stroke" Value="{AppThemeBinding Light={StaticResource InkLight}, Dark={StaticResource InkDark}}" />
                                    <Setter Property="StrokeThickness" Value="2" />
                                </DataTrigger>
                            </Border.Triggers>
                            <Border.GestureRecognizers>
                                <TapGestureRecognizer Tapped="OnTile" />
                            </Border.GestureRecognizers>
                            <VerticalStackLayout Spacing="6">
                                <Label Text="{Binding Icon}" FontSize="26" />
                                <Label Text="{Binding Count}" Style="{StaticResource Numeral}" />
                                <Label Text="{Binding Title}" FontFamily="BarlowSemiBold" />
                                <Label Text="{Binding Detail}" Style="{StaticResource Caption}" />
                            </VerticalStackLayout>
                        </Border>
                    </DataTemplate>
                </BindableLayout.ItemTemplate>
            </FlexLayout>
        </VerticalStackLayout>
    </ScrollView>
</v:BasePage>
```
Create `HomePage.xaml.cs`:

```csharp
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class HomePage : BasePage
{
	public HomePage(HomeViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// the numbers settle on their own while the desk is open
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);

	void OnTile(object? sender, TappedEventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is HomeTile tile) (Shell.Current as StaffShell)?.ShowDeskPage(tile.Route);
	}
}
```

- [ ] **Step 2: Cashier page.** Create `CashierPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<v:BasePage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
            xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
            xmlns:v="clr-namespace:GymMembership.App.Views"
            xmlns:c="clr-namespace:GymMembership.App.Controls"
            xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
            xmlns:svc="clr-namespace:GymMembership.Core.Services;assembly=GymMembership.Core"
            x:Class="GymMembership.App.Views.CashierPage" x:DataType="vm:CashierViewModel" Title="Cashier">

    <ScrollView MaximumWidthRequest="920">
        <VerticalStackLayout Padding="20,16,20,24" Spacing="14">
            <Label Text="Take cash, GCash or a bank transfer. It is verified at once and gets a receipt number to write on the paper receipt." Style="{StaticResource Muted}" />
            <c:Banner Message="{Binding ErrorMessage}" />
            <c:Banner Kind="notice" Message="{Binding Notice}" />

            <Label Text="What are they paying for?" Style="{StaticResource Heading}" />
            <FlexLayout Wrap="Wrap">
                <Button Text="🎟  Membership" SemanticProperties.Description="Membership" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickKindCommand}" CommandParameter="Membership">
                    <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Kind}" Value="Membership"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                </Button>
                <Button Text="🔄  Renewal" SemanticProperties.Description="Renewal" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickKindCommand}" CommandParameter="Renewal">
                    <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Kind}" Value="Renewal"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                </Button>
                <Button Text="🏊  Amenity" SemanticProperties.Description="Amenity" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickKindCommand}" CommandParameter="Amenity">
                    <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Kind}" Value="Amenity"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                </Button>
                <Button Text="🚶  Walk-in" SemanticProperties.Description="Walk-in" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickKindCommand}" CommandParameter="Walk-in">
                    <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Kind}" Value="Walk-in"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                </Button>
            </FlexLayout>

            <!-- walk-ins have no member: just a name -->
            <VerticalStackLayout Spacing="4" WidthRequest="300" HorizontalOptions="Start" IsVisible="{Binding IsWalkIn}">
                <Label Text="Name" Style="{StaticResource Caption}" />
                <Border Style="{StaticResource Field}"><Entry Text="{Binding WalkInName}" SemanticProperties.Description="Walk-in name" /></Border>
            </VerticalStackLayout>

            <VerticalStackLayout Spacing="12" IsVisible="{Binding NeedsMember}">
                <FlexLayout Wrap="Wrap" AlignItems="End">
                    <VerticalStackLayout Spacing="4" Margin="0,0,12,8" WidthRequest="240">
                        <Label Text="Find the member" Style="{StaticResource Caption}" />
                        <Border Style="{StaticResource Field}"><Entry Text="{Binding Search}" Placeholder="Start of their name" ReturnCommand="{Binding FindCommand}" SemanticProperties.Description="Search members" /></Border>
                    </VerticalStackLayout>
                    <Button Text="Find" Style="{StaticResource SmallOutlineButton}" Margin="0,0,12,8" Command="{Binding FindCommand}" />
                    <VerticalStackLayout Spacing="4" Margin="0,0,12,8" WidthRequest="260">
                        <Label Text="Member" Style="{StaticResource Caption}" />
                        <Border Style="{StaticResource Field}"><Picker ItemsSource="{Binding Hits}" ItemDisplayBinding="{Binding Username}" SelectedItem="{Binding Member}" SemanticProperties.Description="Member" /></Border>
                    </VerticalStackLayout>
                </FlexLayout>

                <Label Text="Choose one" Style="{StaticResource Heading}" />
                <FlexLayout Wrap="Wrap" BindableLayout.ItemsSource="{Binding Options}">
                    <BindableLayout.EmptyView>
                        <Label Text="{Binding ItemsEmptyText}" Style="{StaticResource Muted}" />
                    </BindableLayout.EmptyView>
                    <BindableLayout.ItemTemplate>
                        <DataTemplate x:DataType="vm:ItemOption">
                            <Border Style="{StaticResource Panel}" Padding="14,12" WidthRequest="{OnIdiom Phone=156, Default=200}" Margin="0,0,12,12"
                                    SemanticProperties.Description="{Binding Name}" SemanticProperties.Hint="{Binding PriceText}">
                                <Border.Triggers>
                                    <DataTrigger TargetType="Border" Binding="{Binding IsPicked}" Value="True">
                                        <Setter Property="BackgroundColor" Value="{AppThemeBinding Light={StaticResource ChalkLight}, Dark={StaticResource ChalkDark}}" />
                                        <Setter Property="Stroke" Value="{AppThemeBinding Light={StaticResource InkLight}, Dark={StaticResource InkDark}}" />
                                        <Setter Property="StrokeThickness" Value="2" />
                                    </DataTrigger>
                                </Border.Triggers>
                                <Border.GestureRecognizers>
                                    <TapGestureRecognizer Tapped="OnPickItem" />
                                </Border.GestureRecognizers>
                                <VerticalStackLayout Spacing="4">
                                    <Label Text="{Binding Name}" FontFamily="BarlowSemiBold" LineBreakMode="WordWrap" />
                                    <Label Text="{Binding PriceText}" Style="{StaticResource Heading}" />
                                    <Label Text="Selected" Style="{StaticResource Caption}" IsVisible="{Binding IsPicked}" />
                                </VerticalStackLayout>
                            </Border>
                        </DataTemplate>
                    </BindableLayout.ItemTemplate>
                </FlexLayout>
            </VerticalStackLayout>

            <!-- the order: what the cashier is about to charge, how, and the one button that does it -->
            <Border Style="{StaticResource Panel}">
                <VerticalStackLayout Spacing="12">
                    <Label Text="Order" Style="{StaticResource Heading}" />
                    <Label Text="{Binding OrderWho}" FontFamily="BarlowSemiBold" />
                    <Label Text="{Binding OrderWhat}" Style="{StaticResource Muted}" />

                    <Label Text="Paid with" Style="{StaticResource Caption}" />
                    <FlexLayout Wrap="Wrap">
                        <Button Text="💵  Cash" SemanticProperties.Description="Cash" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickMethodCommand}" CommandParameter="Cash">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Method}" Value="Cash"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                        <Button Text="📱  GCash" SemanticProperties.Description="GCash" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickMethodCommand}" CommandParameter="GCash">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Method}" Value="GCash"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                        <Button Text="🏦  Bank transfer" SemanticProperties.Description="Bank transfer" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Command="{Binding PickMethodCommand}" CommandParameter="Bank transfer">
                            <Button.Triggers><DataTrigger TargetType="Button" Binding="{Binding Method}" Value="Bank transfer"><Setter Property="Style" Value="{StaticResource SmallButton}" /></DataTrigger></Button.Triggers>
                        </Button>
                    </FlexLayout>

                    <VerticalStackLayout Spacing="4" WidthRequest="260" HorizontalOptions="Start" IsVisible="{Binding NeedsReference}">
                        <Label Text="Reference number" Style="{StaticResource Caption}" />
                        <Border Style="{StaticResource Field}"><Entry Text="{Binding ReferenceNo}" SemanticProperties.Description="Reference number" /></Border>
                    </VerticalStackLayout>
                    <VerticalStackLayout Spacing="4" WidthRequest="260" HorizontalOptions="Start" IsVisible="{Binding NeedsMember}">
                        <Label Text="Discount code (optional)" Style="{StaticResource Caption}" />
                        <Border Style="{StaticResource Field}"><Entry Text="{Binding DiscountCode}" SemanticProperties.Description="Discount code" /></Border>
                    </VerticalStackLayout>

                    <VerticalStackLayout Spacing="2">
                        <Label Text="Total" Style="{StaticResource Caption}" />
                        <Label Text="{Binding TotalText}" Style="{StaticResource Numeral}" />
                        <Label Text="{Binding TotalNote}" Style="{StaticResource Caption}" IsVisible="{Binding TotalNote, Converter={StaticResource HasText}}" />
                    </VerticalStackLayout>

                    <Button Text="Complete payment" Command="{Binding RecordCommand}" MinimumHeightRequest="56" FontSize="18" />
                </VerticalStackLayout>
            </Border>

            <Label Text="Cash taken today" Style="{StaticResource Heading}" Margin="0,8,0,0" />
            <Border Style="{StaticResource Panel}" Padding="24,18">
                <VerticalStackLayout Spacing="4">
                    <Label Text="Compare this with the drawer at closing." Style="{StaticResource Caption}" />
                    <Label Text="{Binding CashTotal}" Style="{StaticResource Numeral}" />
                </VerticalStackLayout>
            </Border>
            <VerticalStackLayout BindableLayout.ItemsSource="{Binding CashToday}" Spacing="8">
                <BindableLayout.EmptyView><Label Text="No cash payments recorded today." Style="{StaticResource Muted}" /></BindableLayout.EmptyView>
                <BindableLayout.ItemTemplate>
                    <DataTemplate x:DataType="svc:CashRow">
                        <Border Style="{StaticResource Panel}" Padding="16,12">
                            <Grid ColumnDefinitions="*,Auto" ColumnSpacing="12">
                                <Label Style="{StaticResource BaseLabel}" VerticalOptions="Center">
                                    <Label.Text>
                                        <MultiBinding StringFormat="{}{0}  ({1})"><Binding Path="Kind" /><Binding Path="Payments" /></MultiBinding>
                                    </Label.Text>
                                </Label>
                                <Label Grid.Column="1" Text="{Binding Total, StringFormat='₱{0:N2}'}" Style="{StaticResource Heading}" />
                            </Grid>
                        </Border>
                    </DataTemplate>
                </BindableLayout.ItemTemplate>
            </VerticalStackLayout>
        </VerticalStackLayout>
    </ScrollView>
</v:BasePage>
```
Create `CashierPage.xaml.cs` (same shape as the `CounterPage.xaml.cs` it replaces, plus the tap handler):

```csharp
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CashierPage : BasePage
{
	public CashierPage(CashierViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnPickItem(object? sender, TappedEventArgs e) => Row<CashierViewModel>(sender, v => v.PickItemCommand);
}
```
Then delete `CounterPage.xaml` and `CounterPage.xaml.cs`: `git rm src/GymMembership.App/Views/CounterPage.xaml src/GymMembership.App/Views/CounterPage.xaml.cs`.

- [ ] **Step 3: Shell.** In `StaffShell.xaml` replace the four `desk` items with:

```xml
    <FlyoutItem Title="Home" ClassId="desk" Route="home"><ShellContent ContentTemplate="{DataTemplate views:HomePage}" /></FlyoutItem>
    <FlyoutItem Title="Payments to verify" ClassId="desk" Route="verify"><ShellContent ContentTemplate="{DataTemplate views:VerifyPaymentsPage}" /></FlyoutItem>
    <FlyoutItem Title="Cashier" ClassId="desk" Route="cashier"><ShellContent ContentTemplate="{DataTemplate views:CashierPage}" /></FlyoutItem>
    <FlyoutItem Title="Check-in" ClassId="desk" Route="checkin"><ShellContent ContentTemplate="{DataTemplate views:CheckInPage}" /></FlyoutItem>
    <FlyoutItem Title="Amenity Status" ClassId="desk" Route="amenities"><ShellContent ContentTemplate="{DataTemplate views:AmenityStatusPage}" /></FlyoutItem>
```
In `StaffShell.xaml.cs` replace `ShortNames` and the tab creation, and add `ShowDeskPage`:

```csharp
	static readonly Dictionary<string, string> ShortNames = new()
	{
		["Home"] = "Home", ["Payments to verify"] = "Verify", ["Cashier"] = "Cashier", ["Check-in"] = "Check-in", ["Amenity Status"] = "Amenities",
	};
```
```csharp
			bar.Items.Add(new Tab { Title = ShortNames.GetValueOrDefault(item.Title, item.Title), Route = item.Route, Items = { new ShellContent { ContentTemplate = page.ContentTemplate } } });
```
```csharp
	// A Home tile opens the screen it counts. Works for the phone's bottom bar and for the side menu alike, because it looks the route up
	// on both levels instead of asking the shell to resolve a path.
	public void ShowDeskPage(string route)
	{
		foreach (var item in Items)
		{
			if (item.Route == route) { CurrentItem = item; return; }
			foreach (var section in item.Items)
				if (section.Route == route) { CurrentItem = item; item.CurrentItem = section; return; }
		}
	}
```
The `messages` FlyoutItem already has `Route="messages"`.

- [ ] **Step 4: Register the service.** In `MauiProgram.cs`, after `s.AddSingleton<IChatService, ChatService>();` and the `IRealtimeChat` registration, add: `s.AddSingleton<IDeskHomeService, DeskHomeService>();`

- [ ] **Step 5: Build and run all tests.**
Run: `dotnet test tests/GymMembership.Tests` then `dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0` then `dotnet build src/GymMembership.App -f net10.0-android`
Expected: all tests pass (including `XamlBindingTests`, which now sees the two new pages); both builds succeed with 0 errors. No new warnings from the new files.

- [ ] **Step 6: Commit.**
```bash
git add -A src tests
git commit -m "feat: front desk Home and Cashier pages, shell wiring"
```
(`git add -A` is limited to `src` and `tests`; never add the repo root while a `supabase.json.bak` exists.)

---

### Task 6: Look at it, review it, record it

**Files:**
- Modify: `docs/progress-2026-10-16.md`, `docs/superpowers/specs/2026-10-15-role-redesign-design.md`

- [ ] **Step 1: Look at it on Windows and the Android emulator in demo mode** (move `src/GymMembership.App/Resources/Raw/supabase.json` to `supabase.json.bak`, rebuild, sign in with the one-tap Front desk and Admin buttons; put the file back afterwards and confirm it is back). Check and write down what you saw:
  1. Front desk and Admin both land on Home; four tiles show; the Verify tile has an outline when payments are pending.
  2. Tapping each tile opens the right screen, on Windows (side menu) **and** on the phone layout (bottom bar). If a tile does nothing on the phone, `ShowDeskPage` did not find the tab: check that `Route` was copied onto the `Tab`.
  3. Phone bottom bar shows Home, Verify, Cashier, Check-in, Amenities.
  4. Cashier: pick Membership, find Carlo, pick a card (the card gets the outline and the word "Selected"), the total shows its price, Complete payment is greyed until the order is complete, then it records, shows "Payment complete…" and clears.
  5. Switch to GCash: the reference box appears and Complete payment needs it. Switch to Walk-in: name only, total reads "Day pass".
  6. Light and dark mode both read well; text sizes fit at phone width with no horizontal scroll.
- [ ] **Step 2: Whole-branch checks.** `dotnet test tests/GymMembership.Tests` and `cd supabase/tests/local && npm test` (judge by "0 failed"; stop any orphan `postgres.exe` first); both apps build. Use superpowers:verification-before-completion and quote the real counts.
- [ ] **Step 3: Independent review.** One fresh review of the phase 5 commits (most capable model, read-only) with the Review Focus section above; fix Critical and Important findings with a failing test first; list minors as deferred.
- [ ] **Step 4: Check on the real project (owner).** With the real `supabase.json`, sign in as front desk: Home shows real numbers (the check-in tile proves the date filter works on the real server); one small cash payment for a test member from the Cashier; an admin can void it afterwards from "Void a payment".
- [ ] **Step 5: Record it.** Mark phase 5 done in the progress doc table and the redesign spec, with the deferred minors, and commit:
```bash
git add docs
git commit -m "docs: phase 5 recorded"
```
