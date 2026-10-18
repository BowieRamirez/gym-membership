# Role Redesign, Phase 2: Notification Bell Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. UI task (5): invoke the `frontend-design:frontend-design` skill first. Finish with `ponytail:ponytail-review` (CLAUDE.md).

**Goal:** The header bell opens a dropdown panel (a full-width sheet on phones) listing alerts newest first with unread ones highlighted and a "Mark all as read" action. The panel replaces the Alerts page, and the bell's badge updates the moment an alert is read, without waiting for a page change.

**Architecture:** `INotificationService` gains a cheap unread count, a one-request "mark all read" and a `Changed` event. The header and an open panel both listen to that event, so they never disagree. The panel is a CommunityToolkit `Popup` shown from the bell; its view model is the existing `NotificationsViewModel`. A single filtered update in the gateway marks every unread alert read; **no database change**.

**Tech Stack:** .NET 10 MAUI (Windows + Android), CommunityToolkit.Mvvm and CommunityToolkit.Maui 15.0.1 (`Popup`), xUnit, Supabase (postgrest-csharp).

**Spec:** `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 2 of section 4; section 3.4 says the bell needs no database change).

## Global Constraints

- **No migration and no new table.** "Mark all as read" is one filtered `PATCH` on `user_notifications.read_at` (the existing column grant and RLS already allow a user to update their own rows' `read_at`).
- Colors and type come from the existing tokens (`Styles.xaml`, `Colors.xaml`). No new colors. WCAG AA contrast must hold in light and dark (the `ContrastTests` guard stays green).
- Trim-safe bindings only: no `RelativeSource AncestorType` command bindings (they threw `NullReferenceException` during list reloads and halted the debugger); use a `Tapped` or `Clicked` handler. No reflection-based bindings.
- Every new Core behavior has an xUnit test against `DemoGateway`; the new gateway request is tested against the fake-HTTP `SupabaseGatewayTests` style.
- Supabase Realtime is **not** introduced here (it arrives with the messenger in phase 4, where chat needs it). Until then the bell refreshes on: opening the panel, any change made in the app, every page change, and a 60-second timer while the app is open.
- Never push or merge without the owner saying so. Never commit `docs/screenshotsforexample/` or `supabase.json.bak`. `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo-mode checks and put it back.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Decisions made while planning (change any of them and this plan changes)

| Question | Decision |
| --- | --- |
| Panel form | Dropdown anchored top right on desktop (380 wide, at most 480 tall); full-width sheet under the header on phones. Tap outside or "Close" dismisses it. |
| Alerts page | Removed, along with the three "Alerts" menu items; the bell is the only entry. |
| Tapping an alert | Marks it read. Deep links (the `reference` payload) are not built; nothing needs them yet. |
| Live updates | Shared `Changed` event + 60-second timer now; Realtime in phase 4. |
| Staff alert sources (new member concern, failed payment, amenity issue) | Not here. The bell already shows any `user_notifications` row; the rows come from phase 3 (amenity status triggers) and phase 4 (support messages). The existing expiry and verification notices already reach it. |

## Review Focus

Failure modes the happy path will not show, most likely first. Each has a test in the task named in brackets.

1. 200 alerts: the list stays newest first, the badge reads `99+`, and "Mark all as read" is one request, not 200. [Task 1, Task 2]
2. Offline while marking: an error line in the panel, alerts stay unread, the badge keeps its old number, nothing crashes. [Task 4]
3. Two refreshes finishing out of order: the newer answer wins, so a late reply never resurrects an old count. [Task 3]
4. Double tap on the bell: one panel, not two. [Task 5, checked by hand]
5. An alert with no body, or a very long title: the row wraps and stays readable. [Task 5, checked by hand]

---

### Task 1: One filtered update in the gateway

**Files:**
- Modify: `src/GymMembership.Core/Services/Foundation.cs` (add one method to `ISupabaseGateway`)
- Modify: `src/GymMembership.Core/Backend/SupabaseGateway.cs`, `src/GymMembership.Core/Demo/DemoGateway.cs`
- Test: `tests/GymMembership.Tests/SupabaseGatewayTests.cs`, create `tests/GymMembership.Tests/NotificationTests.cs`

**Interfaces:**
- Produces: `Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new()` on `ISupabaseGateway`. It sets one column on every matching row in a single request.

- [ ] **Step 0: Create the branch**

```bash
git checkout role-redesign-phase1 && git checkout -b role-redesign-phase2
```

- [ ] **Step 1: Write the failing tests**

Append inside the class in `tests/GymMembership.Tests/SupabaseGatewayTests.cs` (next to `Marking_a_notification_read_changes_only_read_at`):

```csharp
    [Fact]
    public async Task Marking_every_unread_notification_read_is_one_request_that_leaves_read_ones_alone()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, "[]");
        await gw.UpdateWhereAsync<UserNotification, DateTime?>(n => n.UserId == "u-1" && n.ReadAt == null, n => n.ReadAt, DateTime.UtcNow);
        Assert.Equal("PATCH", Last.Method);
        Assert.Contains("user_id=eq.u-1", Last.Query);
        Assert.Contains("read_at=is.null", Last.Query);   // without this every already-read alert would get a new read time
        Assert.Equal(new[] { "read_at" }, Columns(Last.Body));
    }
```

Create `tests/GymMembership.Tests/NotificationTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class NotificationTests
{
    static async Task<DemoGateway> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Fact]
    public async Task The_demo_filtered_update_touches_only_your_own_matching_rows()
    {
        var gw = await As("member@demo.io");
        var before = await gw.ListAsync<UserNotification>();
        var read = before.Where(n => n.ReadAt is not null).ToDictionary(n => n.Id, n => n.ReadAt);
        Assert.Contains(before, n => n.ReadAt is null);
        var stamp = DateTime.UtcNow;

        await gw.UpdateWhereAsync<UserNotification, DateTime?>(n => n.UserId == gw.CurrentUserId && n.ReadAt == null, n => n.ReadAt, stamp);

        var after = await gw.ListAsync<UserNotification>();
        Assert.All(after, n => Assert.NotNull(n.ReadAt));
        foreach (var (id, was) in read) Assert.Equal(was, after.First(n => n.Id == id).ReadAt);   // already-read rows keep their time
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter "Marking_every_unread|filtered_update"`
Expected: build FAILS (`UpdateWhereAsync` does not exist).

- [ ] **Step 3: Add the interface method**

In `src/GymMembership.Core/Services/Foundation.cs`, in `ISupabaseGateway`, after `DeleteAsync`:

```csharp
    /// <summary>Sets one column on every row that matches, in a single request; rows the filter does not match are left alone.</summary>
    Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new();
```

In `src/GymMembership.Core/Backend/SupabaseGateway.cs`, after `UpdateAsync`:

```csharp
    public async Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        await c.From<T>().Where(where).Set(column, value).Update();
    }
```

In `src/GymMembership.Core/Demo/DemoGateway.cs`, after its `UpdateAsync`:

```csharp
    // The demo supports exactly what the app uses this for: marking your own notifications read. It follows the same ownership rule as UpdateAsync.
    public Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        if (typeof(T) != typeof(UserNotification) || column.Body is not MemberExpression { Member.Name: nameof(UserNotification.ReadAt) })
            return Task.FromException(new NotSupportedException("the demo only sets read_at on notifications"));
        var matches = where.Compile();
        foreach (var n in All<UserNotification>().Where(n => n.UserId == Uid && matches((T)(object)n)).ToList())
            n.ReadAt = (DateTime?)(object?)value;
        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/GymMembership.Tests --filter "Marking_every_unread|filtered_update"`
Expected: both PASS. **This step is where the one real unknown lives:** does postgrest-csharp turn `n.ReadAt == null` into `read_at=is.null`? If the gateway test fails only on that `Assert.Contains("read_at=is.null", ...)` line, look at what query it did send (print `Last.Query`) and try the library's explicit filter for the null part (for example `.Filter("read_at", Postgrest.Constants.Operator.Is, "null")` on the same `From<T>()` chain, with the rest of the condition still in `Where`). Keep the signature; adjust the body until the test passes. If no way produces an `is.null` filter, **stop and report**: without it the call would stamp a new read time on alerts already read, and the alternative is a one-function database change (`mark_all_notifications_read()`) that the owner must approve first.

Then run the whole suite: `dotnet test tests/GymMembership.Tests` (everything passes).

- [ ] **Step 5: Commit**

```bash
git add src/GymMembership.Core tests/GymMembership.Tests/SupabaseGatewayTests.cs tests/GymMembership.Tests/NotificationTests.cs
git commit -m "feat: gateway can set one column on every matching row in a single request

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Notification service: count, mark all, Changed

**Files:**
- Modify: `src/GymMembership.Core/Services/Services.cs` (`INotificationService` and `NotificationService`)
- Test: `tests/GymMembership.Tests/NotificationTests.cs`

**Interfaces:**
- Consumes: `UpdateWhereAsync` (Task 1).
- Produces on `INotificationService`: `event Action? Changed`, `Task<AppResult<int>> UnreadCountAsync()`, `Task<AppResult<bool>> MarkAllReadAsync()`. `MarkReadAsync` and `MarkAllReadAsync` raise `Changed` after they succeed.

- [ ] **Step 1: Write the failing tests**

Add inside the class in `tests/GymMembership.Tests/NotificationTests.cs`:

```csharp
    [Fact]
    public async Task The_unread_count_is_the_number_of_alerts_without_a_read_time()
    {
        var gw = await As("member@demo.io");
        var svc = new NotificationService(gw);
        var expected = (await svc.ListAsync()).Value!.Count(n => n.ReadAt is null);
        Assert.True(expected > 0);
        Assert.Equal(expected, (await svc.UnreadCountAsync()).Value);
    }

    [Fact]
    public async Task Mark_all_read_clears_the_count_and_announces_one_change()
    {
        var gw = await As("member@demo.io");
        var svc = new NotificationService(gw);
        var changes = 0;
        svc.Changed += () => changes++;

        Assert.True((await svc.MarkAllReadAsync()).Ok);

        Assert.Equal(0, (await svc.UnreadCountAsync()).Value);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Reading_one_alert_announces_a_change_and_a_failure_announces_nothing()
    {
        var gw = await As("member@demo.io");
        var svc = new NotificationService(gw);
        var changes = 0;
        svc.Changed += () => changes++;

        await svc.MarkReadAsync((await svc.ListAsync()).Value!.First(n => n.ReadAt is null).Id);
        Assert.Equal(1, changes);

        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await svc.MarkAllReadAsync()).Error);
        Assert.Equal(AppErrorKind.Offline, (await svc.UnreadCountAsync()).Error);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Two_hundred_alerts_come_back_newest_first_and_mark_all_is_still_one_call()
    {
        var gw = await As("member@demo.io");
        for (var i = 0; i < 200; i++) gw.Notify(gw.CurrentUserId, "system", $"n{i}");   // the demo's server-side "send an alert" (made public in Step 3)
        var svc = new NotificationService(gw);

        var list = (await svc.ListAsync()).Value!;
        Assert.True(list.Count >= 200);
        Assert.Equal(list.OrderByDescending(n => n.CreatedAt).Select(n => n.Id), list.Select(n => n.Id));

        Assert.True((await svc.MarkAllReadAsync()).Ok);
        Assert.Equal(0, (await svc.UnreadCountAsync()).Value);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter NotificationTests`
Expected: build FAILS (`UnreadCountAsync`, `MarkAllReadAsync`, `Changed` do not exist).

- [ ] **Step 3: Implement**

In `src/GymMembership.Core/Demo/DemoGateway.cs` change `void Notify(string? userId, string type, string title, string? body = null)` to `public void Notify(...)` (same body). It is the demo's stand-in for the server sending an alert, which a client cannot insert itself (the real table has no insert grant either); tests use it to create alerts.

Replace the interface and class in `src/GymMembership.Core/Services/Services.cs`:

```csharp
public interface INotificationService
{
    /// <summary>Raised after an alert is read or all are, so the bell and an open panel redraw together.</summary>
    event Action? Changed;
    /// <summary>Newest first.</summary>
    Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync();
    Task<AppResult<int>> UnreadCountAsync();
    Task<AppResult<bool>> MarkReadAsync(int id);
    Task<AppResult<bool>> MarkAllReadAsync();
}

public sealed class NotificationService(ISupabaseGateway gw) : INotificationService
{
    public event Action? Changed;

    public Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        return (IReadOnlyList<UserNotification>)(await gw.ListAsync<UserNotification>(n => n.UserId == uid)).OrderByDescending(n => n.CreatedAt).ToList();
    });

    // fetches only the unread rows, so the bell does not download every alert you ever had
    public Task<AppResult<int>> UnreadCountAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        return (await gw.ListAsync<UserNotification>(n => n.UserId == uid && n.ReadAt == null)).Count;
    });

    public Task<AppResult<bool>> MarkReadAsync(int id) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<UserNotification>(n => n.Id == id)).First();
        row.ReadAt = DateTime.UtcNow;
        await gw.UpdateAsync(row);
        Changed?.Invoke();
        return true;
    });

    public Task<AppResult<bool>> MarkAllReadAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        await gw.UpdateWhereAsync<UserNotification, DateTime?>(n => n.UserId == uid && n.ReadAt == null, n => n.ReadAt, DateTime.UtcNow);
        Changed?.Invoke();
        return true;
    });
}
```

- [ ] **Step 4: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests --filter NotificationTests`, then `dotnet test tests/GymMembership.Tests`
Expected: all PASS (the existing header tests still pass: they only call `ListAsync` and `MarkReadAsync`).

- [ ] **Step 5: Commit**

```bash
git add src/GymMembership.Core/Services/Services.cs tests/GymMembership.Tests/NotificationTests.cs
git commit -m "feat: notification service counts unread cheaply, marks all read in one call and announces changes

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The header listens, and stale answers lose

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`HeaderViewModel`)
- Modify: `src/GymMembership.App/Controls/HeaderBar.xaml.cs` (60-second timer)
- Test: `tests/GymMembership.Tests/HeaderTests.cs`

**Interfaces:**
- Consumes: `INotificationService.UnreadCountAsync`, `Changed` (Task 2).
- Produces: `HeaderViewModel.RefreshAsync()` now uses the count call, reloads on `Changed`, and ignores an answer that a newer refresh has overtaken. `Dispose()` stops listening to both services.

- [ ] **Step 1: Write the failing tests**

Add inside the class in `tests/GymMembership.Tests/HeaderTests.cs`:

```csharp
    // a notification service whose answers the test releases by hand, to make two refreshes finish out of order
    sealed class SlowNotes : INotificationService
    {
        public readonly Queue<TaskCompletionSource<AppResult<int>>> Pending = new();
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
        public Task<AppResult<int>> UnreadCountAsync() { var t = new TaskCompletionSource<AppResult<int>>(); Pending.Enqueue(t); return t.Task; }
        public Task<AppResult<IReadOnlyList<GymMembership.Core.Models.UserNotification>>> ListAsync() => throw new NotSupportedException();
        public Task<AppResult<bool>> MarkReadAsync(int id) => throw new NotSupportedException();
        public Task<AppResult<bool>> MarkAllReadAsync() => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_late_answer_from_an_older_refresh_does_not_overwrite_the_newer_count()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var notes = new SlowNotes();
        var vm = new HeaderViewModel(new ProfileService(gw), notes, new AuthService(gw), new Nav());

        var older = vm.RefreshAsync();
        var newer = vm.RefreshAsync();
        var first = notes.Pending.Dequeue();
        var second = notes.Pending.Dequeue();
        second.SetResult(AppResult<int>.Success(5));
        await newer;
        first.SetResult(AppResult<int>.Success(2));
        await older;

        Assert.Equal(5, vm.UnreadCount);
    }

    [Fact]
    public async Task Reading_an_alert_anywhere_updates_the_badge_at_once_and_a_disposed_header_stops_listening()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var notes = new NotificationService(gw);
        var vm = new HeaderViewModel(new ProfileService(gw), notes, new AuthService(gw), new Nav());
        await vm.RefreshAsync();
        var before = vm.UnreadCount;
        Assert.True(before > 0);

        var closed = new HeaderViewModel(new ProfileService(gw), notes, new AuthService(gw), new Nav());
        await closed.RefreshAsync();
        Assert.Equal(before, closed.UnreadCount);
        closed.Dispose();

        await notes.MarkAllReadAsync();
        await Task.Delay(100);
        Assert.Equal(0, vm.UnreadCount);   // the live header followed at once: no page change was needed
        Assert.Equal(before, closed.UnreadCount);   // the disposed one did not react
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter HeaderTests`
Expected: the new tests FAIL (the first: `UnreadCount` ends up 2; the second: the badge stays at its old number because the header does not listen yet). If the first fails to build because `HeaderViewModel` still calls `ListAsync` on `SlowNotes`, that is the same failure.

- [ ] **Step 3: Implement**

In `HeaderViewModel` (`MemberViewModels.cs`) replace the constructor's subscription block, `Dispose` and `RefreshAsync`:

```csharp
    readonly Action onAvatarChanged;
    readonly Action onAlertsChanged;
    int refreshTicket;

    public HeaderViewModel(IProfileService profile, INotificationService notes, IAuthService auth, IAppNavigator nav)
    {
        (this.profile, this.notes, this.auth, this.nav) = (profile, notes, auth, nav);
        onAvatarChanged = onAlertsChanged = () => _ = RefreshAsync();
        profile.AvatarChanged += onAvatarChanged;   // the services outlive every header, so each header must let go (Dispose)
        notes.Changed += onAlertsChanged;
    }

    /// <summary>Stops listening. Called on sign-out and when the shell is torn down.</summary>
    public void Dispose() { profile.AvatarChanged -= onAvatarChanged; notes.Changed -= onAlertsChanged; }

    /// <summary>Reloads quietly: a bar that cannot reach the server keeps what it had and shows no error. The newest refresh wins.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var ticket = ++refreshTicket;
        var me = (await profile.MeAsync()).Value;
        var link = me is null ? null : await profile.AvatarUrlAsync(me.AvatarPath);
        var unread = await notes.UnreadCountAsync();
        if (ticket != refreshTicket) return;   // a newer refresh started while this one waited; its answer is the one to show
        if (me is not null) { Letters = Initials.From(me.Name); if (link is { Ok: true }) AvatarUrl = link.Value; }
        if (unread.Ok) UnreadCount = unread.Value;
    }
```

(Remove the old `Dispose` and `RefreshAsync` bodies and the old single `onAvatarChanged` assignment; keep the rest of the class.)

In `src/GymMembership.App/Controls/HeaderBar.xaml.cs`, in `Attach`, after the `shell.Unloaded` line add:

```csharp
		// no live connection yet (Realtime arrives with the messenger), so look again once a minute while the app is open
		var timer = Application.Current!.Dispatcher.CreateTimer();
		timer.Interval = TimeSpan.FromSeconds(60);
		timer.Tick += (_, _) => _ = vm.RefreshAsync();
		timer.Start();
		shell.Unloaded += (_, _) => timer.Stop();
```

- [ ] **Step 4: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS (earlier header tests included).

- [ ] **Step 5: Commit**

```bash
git add src tests/GymMembership.Tests/HeaderTests.cs
git commit -m "feat: header badge follows alert changes at once; stale refreshes lose; one-minute refresh while open

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The panel's view model

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`NotificationsViewModel`)
- Create: `tests/GymMembership.Tests/NotificationPanelTests.cs`

**Interfaces:**
- Consumes: `INotificationService` (Task 2).
- Produces: `NotificationsViewModel : LoadableViewModel, IDisposable` with `Items` (newest first), `UnreadCount`, `HasUnread`, `LoadCommand`, `MarkReadCommand(UserNotification)`, `MarkAllReadCommand` (can execute only while `HasUnread`). It reloads itself when the service raises `Changed`. `Dispose()` stops that.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/NotificationPanelTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class NotificationPanelTests
{
    static async Task<(DemoGateway Gw, NotificationService Svc, NotificationsViewModel Vm)> Open()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var svc = new NotificationService(gw);
        var vm = new NotificationsViewModel(svc);
        await vm.OnAppearingAsync();
        return (gw, svc, vm);
    }

    [Fact]
    public async Task Opening_lists_newest_first_and_counts_the_unread()
    {
        var (_, _, vm) = await Open();
        Assert.Equal(vm.Items.OrderByDescending(n => n.CreatedAt).Select(n => n.Id), vm.Items.Select(n => n.Id));
        Assert.Equal(vm.Items.Count(n => n.ReadAt is null), vm.UnreadCount);
        Assert.True(vm.HasUnread);
        Assert.True(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Tapping_an_unread_alert_reads_it_and_the_list_redraws()
    {
        var (_, _, vm) = await Open();
        var before = vm.UnreadCount;
        var unread = vm.Items.First(n => n.ReadAt is null);

        await vm.MarkReadCommand.ExecuteAsync(unread);

        Assert.Equal(before - 1, vm.UnreadCount);
        Assert.NotNull(vm.Items.First(n => n.Id == unread.Id).ReadAt);
    }

    [Fact]
    public async Task Mark_all_read_empties_the_unread_count_and_switches_itself_off()
    {
        var (_, _, vm) = await Open();
        await vm.MarkAllReadCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.UnreadCount);
        Assert.All(vm.Items, n => Assert.NotNull(n.ReadAt));
        Assert.False(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Offline_shows_a_plain_message_and_leaves_everything_unread()
    {
        var (gw, _, vm) = await Open();
        var before = vm.UnreadCount;
        gw.FailWith = new HttpRequestException("offline");

        await vm.MarkAllReadCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.Equal(before, vm.UnreadCount);
    }

    [Fact]
    public async Task A_change_made_elsewhere_refreshes_an_open_panel_and_a_disposed_one_ignores_it()
    {
        var (_, svc, vm) = await Open();
        await svc.MarkAllReadAsync();
        await Task.Delay(100);
        Assert.Equal(0, vm.UnreadCount);

        var (gw2, svc2, vm2) = await Open();
        var before = vm2.UnreadCount;
        vm2.Dispose();
        await svc2.MarkAllReadAsync();
        await Task.Delay(100);
        Assert.Equal(before, vm2.UnreadCount);   // closed panel: it no longer redraws
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter NotificationPanelTests`
Expected: build FAILS (`MarkAllReadCommand`, `HasUnread`, `Dispose` missing).

- [ ] **Step 3: Implement**

Replace `NotificationsViewModel` in `MemberViewModels.cs`:

```csharp
public partial class NotificationsViewModel : LoadableViewModel, IDisposable
{
    readonly INotificationService svc;
    readonly Action onChanged;

    public NotificationsViewModel(INotificationService svc)
    {
        this.svc = svc;
        onChanged = () => _ = LoadAsync();   // a change from anywhere (the bell, another page) redraws the open panel
        svc.Changed += onChanged;
    }

    public void Dispose() => svc.Changed -= onChanged;

    public ObservableCollection<UserNotification> Items { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread)), NotifyCanExecuteChangedFor(nameof(MarkAllReadCommand))]
    private int unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListAsync, rows => { Fill(Items, rows); UnreadCount = rows.Count(n => n.ReadAt is null); });

    // the service raises Changed on success, which reloads the list
    [RelayCommand]
    private async Task MarkReadAsync(UserNotification n)
    { if (n.ReadAt is null) await RunAsync(() => svc.MarkReadAsync(n.Id)); }

    [RelayCommand(CanExecute = nameof(HasUnread))]
    private Task MarkAllReadAsync() => RunAsync(svc.MarkAllReadAsync);
}
```

- [ ] **Step 4: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GymMembership.Core/ViewModels/MemberViewModels.cs tests/GymMembership.Tests/NotificationPanelTests.cs
git commit -m "feat: alerts view model gets mark-all-read and redraws when alerts change

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The panel itself, and retiring the Alerts page

**Invoke the `frontend-design:frontend-design` skill first.** Keep the existing look: flat panels, hairlines, Barlow, no shadows, ink as primary, Plate Blue only as the unread marker.

**Files:**
- Create: `src/GymMembership.App/Controls/NotificationPanel.xaml`, `src/GymMembership.App/Controls/NotificationPanel.xaml.cs`
- Modify: `src/GymMembership.App/Controls/HeaderBar.xaml.cs`, `src/GymMembership.App/MemberShell.xaml`, `StaffShell.xaml`, `CoachShell.xaml`
- Delete: `src/GymMembership.App/Views/NotificationsPage.xaml`, `NotificationsPage.xaml.cs`

**Interfaces:**
- Consumes: `NotificationsViewModel` (Task 4), `HeaderViewModel` (Task 3).
- Produces: `NotificationPanel(NotificationsViewModel vm)`, a `CommunityToolkit.Maui.Views.Popup`.

- [ ] **Step 1: Probe the popup API (2 minutes)**

The toolkit is 15.0.1; its docs show `toolkit:Popup` XAML roots, `Shell.Current.ShowPopupAsync(view, new PopupOptions { ... })` and `CloseAsync()`. Confirm they compile before building on them: create `NotificationPanel.xaml` / `.xaml.cs` with only an empty `<VerticalStackLayout />` body and the `x:Class`, build for Windows and Android, and if a name differs in 15.0.1, read the installed package's API (or `mcp__plugin_context7_context7__query-docs` for the toolkit) and use the real name in the next steps. If `Popup` cannot be positioned top-right at all, stop and report: the fallback is the existing pushed-page route (keep `NotificationsPage`, add the mark-all button there).

- [ ] **Step 2: Write the panel**

`src/GymMembership.App/Controls/NotificationPanel.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<toolkit:Popup xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
               xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
               xmlns:toolkit="http://schemas.microsoft.com/dotnet/2022/maui/toolkit"
               xmlns:c="clr-namespace:GymMembership.App.Controls"
               xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
               xmlns:m="clr-namespace:GymMembership.Core.Models;assembly=GymMembership.Core"
               x:Class="GymMembership.App.Controls.NotificationPanel" x:DataType="vm:NotificationsViewModel"
               HorizontalOptions="{OnIdiom Phone=Fill, Default=End}" VerticalOptions="Start"
               Margin="{OnIdiom Phone='8,60,8,0', Default='0,60,16,0'}" Padding="0"
               BackgroundColor="{AppThemeBinding Light={StaticResource SurfaceLight}, Dark={StaticResource SurfaceDark}}">

    <Grid RowDefinitions="Auto,Auto,*" WidthRequest="{OnIdiom Phone=-1, Default=380}" MaximumHeightRequest="480">
        <Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="8" Padding="16,12">
            <Label Text="Alerts" Style="{StaticResource Heading}" VerticalOptions="Center" />
            <Button Grid.Column="1" Text="Mark all as read" Style="{StaticResource SmallOutlineButton}" Command="{Binding MarkAllReadCommand}"
                    SemanticProperties.Description="Mark all alerts as read" />
            <Button Grid.Column="2" Text="Close" Style="{StaticResource SmallOutlineButton}" Clicked="OnClose" />
        </Grid>

        <c:Banner Grid.Row="1" Message="{Binding ErrorMessage}" Margin="16,0,16,8" />

        <CollectionView Grid.Row="2" ItemsSource="{Binding Items}" Margin="12,0,12,12">
            <CollectionView.ItemsLayout><LinearItemsLayout Orientation="Vertical" ItemSpacing="8" /></CollectionView.ItemsLayout>
            <CollectionView.EmptyView><Label Text="You're all caught up." Style="{StaticResource Muted}" Margin="4,8" /></CollectionView.EmptyView>
            <CollectionView.ItemTemplate>
                <DataTemplate x:DataType="m:UserNotification">
                    <Border Style="{StaticResource Panel}" Padding="12,10">
                        <Border.GestureRecognizers><TapGestureRecognizer Tapped="OnTapped" /></Border.GestureRecognizers>
                        <!-- unread: a blue dot and a darker row; read: neither -->
                        <Border.Triggers>
                            <DataTrigger TargetType="Border" Binding="{Binding ReadAt, Converter={StaticResource IsNull}}" Value="True">
                                <Setter Property="BackgroundColor" Value="{AppThemeBinding Light={StaticResource ChalkLight}, Dark={StaticResource ChalkDark}}" />
                            </DataTrigger>
                        </Border.Triggers>
                        <Grid ColumnDefinitions="10,*" ColumnSpacing="10">
                            <Ellipse Style="{StaticResource DotScheduled}" VerticalOptions="Start" Margin="0,7,0,0"
                                     IsVisible="{Binding ReadAt, Converter={StaticResource IsNull}}" />
                            <VerticalStackLayout Grid.Column="1" Spacing="2">
                                <Label Text="{Binding Title}" FontFamily="BarlowSemiBold" />
                                <Label Text="{Binding Body}" Style="{StaticResource Muted}" IsVisible="{Binding Body, Converter={StaticResource HasText}}" />
                                <Label Text="{Binding CreatedAt, Converter={StaticResource LocalDate}, ConverterParameter='d MMM, h:mm tt'}" Style="{StaticResource Caption}" />
                            </VerticalStackLayout>
                        </Grid>
                    </Border>
                </DataTemplate>
            </CollectionView.ItemTemplate>
        </CollectionView>
    </Grid>
</toolkit:Popup>
```

`src/GymMembership.App/Controls/NotificationPanel.xaml.cs`:

```csharp
using CommunityToolkit.Maui.Views;
using GymMembership.Core.Models;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

/// <summary>The alerts dropdown opened by the header bell. Replaces the old Alerts page.</summary>
public partial class NotificationPanel : Popup
{
	public NotificationPanel(NotificationsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	async void OnClose(object? sender, EventArgs e) => await CloseAsync();

	// a plain handler, not a RelativeSource command binding: those threw NullReferenceException while the list reloaded
	void OnTapped(object? sender, TappedEventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is UserNotification row) ((NotificationsViewModel)BindingContext).MarkReadCommand.Execute(row);
	}
}
```

- [ ] **Step 3: Open it from the bell**

In `src/GymMembership.App/Controls/HeaderBar.xaml.cs`: add `using CommunityToolkit.Maui.Extensions;` (and `CommunityToolkit.Maui` if the probe showed it is needed), store the service provider in a field in `Attach` (`_sp = sp;` with `IServiceProvider? _sp;`), remove the `Routing.RegisterRoute("header-alerts", ...)` line, and replace `OnBellClicked`:

```csharp
	bool _panelOpen;

	async void OnBellClicked(object? sender, EventArgs e)
	{
		if (_panelOpen || _sp is null) return;   // a second tap while it is open (or opening) does nothing
		_panelOpen = true;
		var vm = _sp.GetRequiredService<NotificationsViewModel>();
		try
		{
			var panel = new NotificationPanel(vm);
			_ = vm.OnAppearingAsync();   // load while it opens
			await Shell.Current.ShowPopupAsync(panel, new PopupOptions());   // returns when it closes (outside tap or Close)
		}
		finally { vm.Dispose(); _panelOpen = false; }
	}
```

(Adjust `ShowPopupAsync`'s argument to what the Step 1 probe confirmed.)

- [ ] **Step 4: Retire the Alerts page and menu items**

Delete `src/GymMembership.App/Views/NotificationsPage.xaml` and `.xaml.cs`. In each of `MemberShell.xaml`, `StaffShell.xaml` and `CoachShell.xaml` remove the line `<FlyoutItem Title="Alerts" Route="alerts"><ShellContent ContentTemplate="{DataTemplate views:NotificationsPage}" /></FlyoutItem>`. Remove the now-unused `using GymMembership.App.Views;` from `HeaderBar.xaml.cs` only if nothing else there needs it (`SettingsPage` still does).

- [ ] **Step 5: Build and run the tests**

Run:
```bash
dotnet test tests/GymMembership.Tests
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
dotnet build src/GymMembership.App -f net10.0-android
```
Expected: all pass and both builds succeed (`XamlBindingTests` now reads `Controls\NotificationPanel.xaml` and checks every binding).

- [ ] **Step 6: Look at it (demo mode; Windows and the Android emulator)**

Demo mode needs `src/GymMembership.App/Resources/Raw/supabase.json` moved to `supabase.json.bak` (it points at the real project); put it back afterwards and confirm it is back. Windows: `dotnet build` then run `bin\Debug\net10.0-windows10.0.19041.0\win-x64\GymMembership.App.exe`; Android: build with `-p:EmbedAssembliesIntoApk=true`, uninstall the old app first, emulator `pixel_7_-_api_36_0` (`-no-snapshot-save`).

Check, as Member, Coach, Front desk and Admin, in light and dark:
- The bell opens the panel under the header: dropdown top right on Windows, full width on the phone. Newest first; unread rows have the blue dot and a darker row; read rows do not.
- Tapping an unread alert marks it read, and the **badge drops at once** with no page change.
- "Mark all as read" clears every dot and the badge; the button is disabled when nothing is unread.
- Tapping outside, "Close" and (Windows) Esc close it. A second tap on the bell while open does nothing (Review Focus 4).
- An alert with no body and one with a long title wrap cleanly (Review Focus 5); 200+ alerts scroll (add rows temporarily in the demo seed, then remove them).
- The menu no longer lists Alerts; the front desk bottom bar still works.
- If the popup misbehaves on a platform (wrong position, cannot dismiss), report it before working around it.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: the bell opens an alerts panel; the Alerts page and its menu items are gone

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

(`git status` must not list `supabase.json.bak`, `docs/screenshotsforexample/` or the demo seed experiment before this commit.)

---

### Task 6: Wrap-up

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 2 note)

- [ ] **Step 1: Record what was built in the spec**

In section 4, after phase 2's sentence, add: `**Built in phase 2:** no database change; the panel is a toolkit Popup (dropdown on desktop, full-width sheet on phones); "mark all as read" is one filtered update; the bell refreshes on any change in the app, on every page change and once a minute; Realtime moves to phase 4 with the messenger; alert deep links and the staff alert sources arrive with phases 3 and 4.`

- [ ] **Step 2: Full verification**

```bash
dotnet test tests/GymMembership.Tests
npm --prefix supabase/tests/local test
dotnet build src/GymMembership.App -f net10.0-android
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
```
Expected: every test passes (report the counts), 66 database checks pass (unchanged: no migration), both builds succeed.

- [ ] **Step 3: Review**

Run `ponytail:ponytail-review` on `git diff role-redesign-phase1...HEAD`, apply what is worth applying, re-run Step 2. Then one fresh whole-branch review of the same range, as for phase 1 (review package, most capable model, the plan's Review Focus verbatim).

- [ ] **Step 4: Commit and hand over**

```bash
git add docs/superpowers/specs/2026-10-15-role-redesign-design.md
git commit -m "docs: phase 2 recorded in the redesign spec

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Stop here. Do not push or merge. Tell the owner: branch `role-redesign-phase2` (on top of `role-redesign-phase1`, which is not merged either) is ready; no migration is needed for this phase, so nothing to run on Supabase beyond `0012` and `0013` from earlier.
