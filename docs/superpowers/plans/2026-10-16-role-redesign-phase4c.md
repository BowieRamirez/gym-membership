# Phase 4c: instant messages, typing, online status. Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A message appears on the other person's screen within about a second; "typing…" and an online dot / "Active 2 h ago" show in a conversation; if Realtime cannot connect, Messages works exactly as today.

**Architecture:** A small `IRealtimeChat` interface in Core (hints only: *something changed*, *they are typing*, *these ids are online*). `MessengerViewModel` listens to it and reacts by calling its existing `AutoRefreshAsync`, so there is still one load path. A Supabase implementation sits beside `SupabaseGateway`; a `NoRealtime` null object is used in demo mode and in tests. The view model is written and tested against a fake first; the Supabase library is touched last, so a library problem cannot block the rest.

**Tech Stack:** C# / .NET 10 MAUI, `Supabase` 8.3.1 (includes `Supabase.Realtime`), CommunityToolkit.Mvvm, xUnit, Postgres migrations with the embedded-Postgres test harness (`supabase/tests/local`).

**Spec:** `docs/superpowers/specs/2026-10-16-phase4c-realtime-design.md` (corrected by Task 9; see "Spec changes" below).

## Spec changes this plan makes (read first)

1. **`last_seen_at` is a new locked table `user_last_seen`, not a `profiles` column.** Migration `0006` runs `grant select on all tables ... to authenticated`, so any column on `profiles` can be read by anyone who can read that profile row (staff read every profile). A table with no grant and no policy can only be read by the `security definer` function `chat_threads()`. Task 9 fixes the spec text.
2. **Last seen is shown only for a current hire** (`coaches_member`) in coach chats, plus to front desk for a member. An ended hire shows nothing.
3. **Online ids are visible to anyone with the app's public key.** Presence runs on one channel, `online`, and the screens only *show* the dot for the people in your conversations, but the channel lists the ids (UUIDs, no names) of everyone online. This is the ceiling of the "no tables" design; moving to a private channel with a database policy is the upgrade if it ever matters. It is stated in Task 7's code comment and in the spec.

## Global Constraints

- Existing tests stay green: `dotnet test tests/GymMembership.Tests` (296 xUnit) and `cd supabase/tests/local && npm test` (103 checks).
- **Do not widen `IChatService`.** The test class `MessengerViewModelTests.SlowChat` implements it; adding a member breaks that test.
- **`MessengerViewModel` keeps its first four constructor arguments** `(IChatService chat, IProfileService profile, IAuthService auth, IProofPicker picker)`; the new argument is a fifth, optional `IRealtimeChat? live = null`, so every existing `new MessengerViewModel(...)` still compiles.
- Realtime failure never shows an error and never blocks Messages; it only means `Connected` is false and the page polls every 6 s.
- Timer rule from the spec: 30 s while connected, 6 s when not.
- Typing: at most one ping every 2 s; the indicator hides after about 4 s without a ping.
- Reload after a change signal: about 300 ms after the last signal, so a burst is one reload.
- Online: green dot **plus** the word "Online" (not colour alone).
- Front desk is never shown as online or last-active to a member (staff rotate).
- Migrations are never pushed without the owner saying so. Task 8 stops and asks.
- XAML bindings are checked by `XamlBindingTests`: every `{Binding X}` must be a public instance property of the page's view model / item type.
- Match the file you edit: its indentation (tabs in `MauiProgram.cs`, `MessagesPage.xaml.cs` and `HeaderBar.xaml.cs`; 4 spaces elsewhere) and its short `//` comments that say why.

## Review Focus

1. **A change signal while a reload is already running or the user is typing a draft:** the reload must not clear the draft, the error line or the scroll position (it goes through `AutoRefreshAsync`, which already guarantees this); Task 3 pins that the draft survives a signal.
2. **Switching conversations while "typing…" is showing:** the indicator must clear and the restored draft must not send a typing ping for the new conversation (Task 4).
3. **Signing out and in as someone else with Messages open:** no events, no online dots and no typing line from the last person may remain (Task 3 pins a stop-and-restart for the new user).
4. **Realtime throws on connect (no network, blocked websocket, old server):** Messages still loads, no error text, interval stays 6 s (Task 3).
5. **A thread that must never show presence** (a member's own "Front desk", an ended hire): even if its id is in the online set, no dot and no "Active…" text (Task 5).

---

## File structure

| File | Change | Responsibility |
| --- | --- | --- |
| `supabase/migrations/0017_realtime_chat.sql` | create | publication, `user_last_seen`, `touch_last_seen()`, `chat_threads()` with `with_last_seen` |
| `supabase/tests/local/harness.mjs` | modify | stub `supabase_realtime` publication |
| `supabase/tests/local/rules.mjs` | modify | database checks for 0017 |
| `src/GymMembership.Core/Models/Models.cs` | modify | `ChatThread.WithLastSeen`, `ShowsPresence` |
| `src/GymMembership.Core/Services/Realtime.cs` | create | `IRealtimeChat`, `NoRealtime`, `ActiveAgo` |
| `src/GymMembership.Core/ViewModels/MemberViewModels.cs` | modify | `LoadableViewModel.OnDisappearing`, `ThreadItem.IsOnline`, all the live behaviour in `MessengerViewModel` |
| `src/GymMembership.Core/Services/ProfileService.cs` | modify | `TouchSeenAsync` |
| `src/GymMembership.Core/Demo/DemoGateway.cs` | modify | `touch_last_seen` no-op |
| `src/GymMembership.Core/Backend/SupabaseGateway.cs` | modify | two `internal` accessors for Realtime |
| `src/GymMembership.Core/Backend/SupabaseRealtimeChat.cs` | create | the Supabase implementation |
| `src/GymMembership.App/Views/BasePage.cs` | modify | call `OnDisappearing`; re-read the refresh interval each tick |
| `src/GymMembership.App/Views/MessagesPage.xaml` + `.xaml.cs` | modify | typing / status line, interval from the view model |
| `src/GymMembership.App/Controls/ThreadRowView.xaml` | modify | online dot + word |
| `src/GymMembership.App/Controls/HeaderBar.xaml.cs` | modify | last-seen heartbeat |
| `src/GymMembership.App/MauiProgram.cs` | modify | register `IRealtimeChat` |
| `tests/GymMembership.Tests/RealtimeMessengerTests.cs` | create | view model behaviour with a fake |
| `tests/GymMembership.Tests/PresenceTextTests.cs` | create | `ActiveAgo`, `ShowsPresence` |
| `scripts/check-realtime.mjs` | create | live two-account check (run only with the owner's OK) |

---

### Task 1: Migration 0017 and its database checks

**Files:**
- Create: `supabase/migrations/0017_realtime_chat.sql`
- Modify: `supabase/tests/local/harness.mjs` (stubs string, line 15)
- Modify: `supabase/tests/local/rules.mjs` (append before line 741, the `console.log` of totals)

**Interfaces:**
- Produces: table `public.user_last_seen(user_id uuid pk, seen_at timestamptz)` (no direct access); `public.touch_last_seen() returns void`; `public.chat_threads()` returning the old columns **plus** `with_last_seen timestamptz` as the last column; publication `supabase_realtime` contains `messages`, `support_messages`.

- [ ] **Step 1: Make the harness know the publication.** In `harness.mjs`, add to the `stubs` string (after the `create schema ...` line, before `grant usage`):

```js
create publication supabase_realtime;
```

- [ ] **Step 2: Write the failing checks.** Append to `rules.mjs` just before `console.log(\`\n${pass} passed, ${failed} failed\`);`:

```js
console.log('\nlive chat (0017)');
await test('messages and support_messages are in the Realtime publication', async () => {
  const r = await db.query(`select tablename from pg_publication_tables where pubname = 'supabase_realtime' order by 1`);
  assert.deepEqual(r.rows.map(x => x.tablename), ['messages', 'support_messages']);
});
const gus = await user('gus@t.io', 'member', 'Gus');
await db.query(`insert into coach_hires (coach_id, member_id) values ($1, $2)`, [coach, gus]);
const seenAt = async uid => (await db.query(`select seen_at from user_last_seen where user_id = $1`, [uid])).rows[0]?.seen_at;
await test('touch_last_seen records the caller and moves at most once every 5 minutes', async () => {
  await run(gus, `select touch_last_seen()`);
  const first = await seenAt(gus);
  assert.ok(first);
  await run(gus, `select touch_last_seen()`);
  assert.equal((await seenAt(gus)).getTime(), first.getTime());   // too soon: unchanged
  await db.query(`update user_last_seen set seen_at = now() - interval '10 minutes' where user_id = $1`, [gus]);
  await run(gus, `select touch_last_seen()`);
  assert.ok((await seenAt(gus)).getTime() > Date.now() - 60000);   // old enough: moved to now
  assert.equal(await seenAt(alex), undefined);                     // nobody else was touched
});
await test('the last-seen table cannot be read or written directly, by anyone', async () => {
  assert.match(await code(run(gus, `select * from user_last_seen`)), /42501/);
  assert.match(await code(run(desk, `select * from user_last_seen`)), /42501/);
  assert.match(await code(run(admin, `select * from user_last_seen`)), /42501/);
  assert.match(await code(run(gus, `insert into user_last_seen (user_id, seen_at) values ($1, now())`, [alex])), /42501/);
  assert.match(await code(run(gus, `update user_last_seen set seen_at = now()`)), /42501/);
  assert.match(await code(run(gus, `delete from user_last_seen`)), /42501/);
});
await test('a profile read does not expose last seen', async () => {
  const cols = (await run(desk, `select * from profiles limit 1`)).fields.map(f => f.name);
  assert.ok(!cols.includes('last_seen_at') && !cols.includes('seen_at'));
});
await test('coach and member with a current hire see each other\'s last seen; nobody else does', async () => {
  await run(coach, `select touch_last_seen()`);
  await db.query(`insert into messages (coach_id, member_id, sender_id, body) values ($1, $2, $2, 'hi')`, [coach, gus]);
  const mine = (await threads(gus)).find(r => r.kind === 'coach');
  const theirs = (await threads(coach)).find(r => r.kind === 'coach' && r.member_id === gus);
  assert.ok(mine.with_last_seen, 'the member sees the coach');
  assert.ok(theirs.with_last_seen, 'the coach sees the member');
  assert.equal((await threads(desk)).filter(r => r.kind === 'coach').length, 0);                 // front desk is not in coach chats
  assert.equal((await threads(alex)).filter(r => r.kind === 'coach' && r.member_id === gus).length, 0);   // a stranger sees no such thread
});
await test('an ended hire shows no last seen', async () => {
  await db.query(`update coach_hires set status = 'ended', ended_at = now() where member_id = $1 and status = 'active'`, [gus]);
  assert.equal((await threads(gus)).find(r => r.kind === 'coach').with_last_seen, null);
  assert.equal((await threads(coach)).find(r => r.kind === 'coach' && r.member_id === gus).with_last_seen, null);
  await db.query(`insert into coach_hires (coach_id, member_id) values ($1, $2)`, [coach, gus]);   // hired again for the next check
});
await test('front desk sees a member\'s last seen; the member never sees the front desk\'s', async () => {
  await run(gus, `select send_support_message($1, 'hello desk')`, [gus]);
  await run(desk, `select touch_last_seen()`);
  const inbox = (await threads(desk)).find(r => r.kind === 'support' && r.member_id === gus);
  assert.ok(inbox.with_last_seen);
  const own = (await threads(gus)).find(r => r.kind === 'support');
  assert.equal(own.with_last_seen, null);
  assert.equal(own.with_id, null);
});
```

- [ ] **Step 3: Run to verify it fails.**
Run: `cd supabase/tests/local && npm test`
Expected: the new block fails (first at the publication check: relation / function does not exist, or `with_last_seen` undefined). The old 103 still pass.

- [ ] **Step 4: Write the migration.** Create `supabase/migrations/0017_realtime_chat.sql`:

```sql
-- 0017: live chat, and "last active".
--
-- 1. messages and support_messages join the Realtime publication, so the app is told when a row is added or changed. The app treats
--    that as a hint only (it asks chat_messages() again), and Realtime sends a change only to people whose row access lets them read
--    the row, so staff still never hear about a coach chat.
-- 2. user_last_seen: when someone was last in the app. A separate table and not a profiles column, because 0006 lets every signed-in
--    user select every column of any profile row they can see (staff see them all). This table has no grant and no policy, so the only
--    reader is chat_threads() below.

alter publication supabase_realtime add table public.messages, public.support_messages;

create table public.user_last_seen (
	user_id uuid        primary key references public.profiles (id) on delete cascade,
	seen_at timestamptz not null
);
alter table public.user_last_seen enable row level security;
revoke all on public.user_last_seen from anon, authenticated;

-- The app calls this on open and every few minutes while open. Called sooner than 5 minutes after the last time, it changes nothing.
create function public.touch_last_seen() returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	insert into public.user_last_seen (user_id, seen_at) values (auth.uid(), now())
	on conflict (user_id) do update set seen_at = now()
	where public.user_last_seen.seen_at < now() - interval '5 minutes';
end;
$$;

revoke all on function public.touch_last_seen() from public, anon, authenticated;
grant execute on function public.touch_last_seen() to authenticated;

-- Same as 0016 plus with_last_seen (the last column):
--   a coach chat: the other person's last seen, only while the hire is current
--   a member's own front desk thread: null (staff come and go, so there is no one to be "active")
--   the front desk inbox: the member's last seen
-- The return type changes, so the function is replaced.
drop function public.chat_threads();
create function public.chat_threads()
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, with_avatar text, last_body text, last_at timestamptz,
               last_from_me boolean, unread integer, can_send boolean, status text, claimed_by uuid, claimed_by_name text, with_last_seen timestamptz)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v_uid uuid := auth.uid();
begin
	perform public.require_active_user();

	return query
	select * from (
		-- coach chats
		select 'coach'::text as kind, p.coach_id as coach_id, p.member_id as member_id,
		       case when p.coach_id = v_uid then p.member_id else p.coach_id end as with_id,
		       pr.full_name as with_name, pr.avatar_path as with_avatar,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end) as last_body,
		       lm.sent_at as last_at, coalesce(lm.sender_id = v_uid, false) as last_from_me,
		       (select count(*)::integer from public.messages x
		         where x.coach_id = p.coach_id and x.member_id = p.member_id and x.sender_id <> v_uid and x.read_at is null) as unread,
		       (public.coaches_member(p.coach_id, p.member_id) and public.is_active_member(p.member_id)) as can_send,
		       null::text as status, null::uuid as claimed_by, null::text as claimed_by_name,
		       case when public.coaches_member(p.coach_id, p.member_id)
		            then (select ls.seen_at from public.user_last_seen ls where ls.user_id = case when p.coach_id = v_uid then p.member_id else p.coach_id end)
		       end as with_last_seen
		from (
			select h.coach_id, h.member_id from public.coach_hires h where h.coach_id = v_uid or h.member_id = v_uid
			union
			select m.coach_id, m.member_id from public.messages m where m.coach_id = v_uid or m.member_id = v_uid
		) p
		join public.profiles pr on pr.id = case when p.coach_id = v_uid then p.member_id else p.coach_id end
		left join lateral (select x.body, x.attachment_path, x.sent_at, x.sender_id from public.messages x
		                   where x.coach_id = p.coach_id and x.member_id = p.member_id order by x.message_id desc limit 1) lm on true

		union all

		-- a member's own front-desk thread (always there, even with no messages yet)
		select 'support'::text, null::uuid, mb.user_id, null::uuid, 'Front desk'::text, null::text,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id = v_uid, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = mb.user_id and s.sender_id <> v_uid and s.read_at is null),
		       true, coalesce(t.status, 'open'), null::uuid, null::text, null::timestamptz
		from public.members mb
		left join public.support_threads t on t.member_id = mb.user_id
		left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s where s.member_id = mb.user_id order by s.message_id desc limit 1) lm on true
		where mb.user_id = v_uid

		union all

		-- the inbox, for staff who may answer
		select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
		       coalesce(nullif(btrim(lm.body), ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.sender_id = t.member_id and s.read_at is null),
		       true, t.status, t.claimed_by, cb.full_name,
		       (select ls.seen_at from public.user_last_seen ls where ls.user_id = t.member_id)
		-- the 500 most recently active threads, so the list stays small however many members have written
		from (select * from public.support_threads st where public.has_permission('support:reply') and st.member_id <> v_uid
		      order by st.last_message_at desc limit 500) t
		join public.profiles pr on pr.id = t.member_id
		left join public.profiles cb on cb.id = t.claimed_by
		left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s where s.member_id = t.member_id order by s.message_id desc limit 1) lm on true
	) q
	order by q.last_at desc nulls last;
end;
$$;

revoke all on function public.chat_threads() from public, anon, authenticated;
grant execute on function public.chat_threads() to authenticated;
```

- [ ] **Step 5: Run to verify it passes.**
Run: `cd supabase/tests/local && npm test`
Expected: `ok  0017_realtime_chat.sql` in the apply list, every new check `ok`, and the totals line shows **0 failed** (103 old + 7 new).
If `chat_search` (0015) fails because it selected `*` from `chat_threads()` into a fixed shape, change its select list to the explicit old columns in this same migration and re-run.

- [ ] **Step 6: Commit.**

```bash
git add supabase/migrations/0017_realtime_chat.sql supabase/tests/local/harness.mjs supabase/tests/local/rules.mjs
git commit -m "feat: realtime publication, last seen and chat_threads with with_last_seen"
```
(End the message with the attribution line the session was given.)

---

### Task 2: Thread model and "Active 2 h ago" text

**Files:**
- Modify: `src/GymMembership.Core/Models/Models.cs:180-186` (the `ChatThread` class)
- Create: `src/GymMembership.Core/Services/Realtime.cs` (only `ActiveAgo` in this task)
- Test: `tests/GymMembership.Tests/PresenceTextTests.cs`

**Interfaces:**
- Produces: `ChatThread.WithLastSeen : DateTime?`; `ChatThread.ShowsPresence : bool`; `static string ActiveAgo.Text(DateTime? seen, DateTime nowUtc)`.

- [ ] **Step 1: Write the failing tests.** Create `tests/GymMembership.Tests/PresenceTextTests.cs`:

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class PresenceTextTests
{
    static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "Active just now")]
    [InlineData(90, "Active just now")]
    [InlineData(5 * 60, "Active 5 min ago")]
    [InlineData(59 * 60, "Active 59 min ago")]
    [InlineData(2 * 3600, "Active 2 h ago")]
    [InlineData(23 * 3600, "Active 23 h ago")]
    [InlineData(26 * 3600, "Active 1 d ago")]
    [InlineData(10 * 86400, "Active 10 d ago")]
    public void Active_ago_is_in_words(int secondsAgo, string expected) =>
        Assert.Equal(expected, ActiveAgo.Text(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Never_seen_says_nothing() => Assert.Equal("", ActiveAgo.Text(null, Now));

    [Fact]
    public void A_time_in_the_future_reads_as_just_now() => Assert.Equal("Active just now", ActiveAgo.Text(Now.AddMinutes(3), Now));

    [Fact]
    public void A_time_of_unknown_kind_is_taken_as_utc() =>
        Assert.Equal("Active 2 h ago", ActiveAgo.Text(new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Unspecified), Now));

    [Fact]
    public void Presence_is_shown_for_a_coach_chat_you_can_still_write_in_and_for_a_member_seen_by_staff_only()
    {
        Assert.True(new ChatThread { Kind = "coach", WithId = "c", CanSend = true }.ShowsPresence);
        Assert.False(new ChatThread { Kind = "coach", WithId = "c", CanSend = false }.ShowsPresence);   // the hire ended
        Assert.True(new ChatThread { Kind = "support", WithId = "m" }.ShowsPresence);                    // staff looking at a member
        Assert.False(new ChatThread { Kind = "support", WithId = null, WithName = "Front desk" }.ShowsPresence);   // a member's own front desk thread
    }
}
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter PresenceTextTests`
Expected: build error `ActiveAgo` / `ShowsPresence` / `WithLastSeen` not defined.

- [ ] **Step 3: Implement.** In `Models.cs`, inside `ChatThread` after the `claimed_by_name` column add:

```csharp
  [Column("with_last_seen")] public DateTime? WithLastSeen { get; set; }
```
and after `HasUnread` add:
```csharp
  /// <summary>Whether to show online / last active for the other person: not for a member's own "Front desk" thread (staff come and go) and not once a hire has ended.</summary>
  public bool ShowsPresence => WithId is not null && (IsSupport || CanSend);
```
Create `src/GymMembership.Core/Services/Realtime.cs`:

```csharp
namespace GymMembership.Core.Services;

/// <summary>"Active 2 h ago", in words. Empty when the person has never been seen.</summary>
public static class ActiveAgo
{
    public static string Text(DateTime? seen, DateTime nowUtc)
    {
        if (seen is not { } s) return "";
        var utc = s.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : s.ToUniversalTime();   // the server sends UTC
        var ago = nowUtc - utc;
        if (ago < TimeSpan.FromMinutes(2)) return "Active just now";   // also covers a clock a little ahead
        if (ago < TimeSpan.FromHours(1)) return $"Active {(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromHours(24)) return $"Active {(int)ago.TotalHours} h ago";
        return $"Active {(int)ago.TotalDays} d ago";
    }
}
```

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests --filter PresenceTextTests`
Expected: all pass.

- [ ] **Step 5: Commit.**
```bash
git add src/GymMembership.Core/Models/Models.cs src/GymMembership.Core/Services/Realtime.cs tests/GymMembership.Tests/PresenceTextTests.cs
git commit -m "feat: last seen on a chat thread and Active-ago text"
```

---

### Task 3: Realtime seam and instant reload in the view model

**Files:**
- Modify: `src/GymMembership.Core/Services/Realtime.cs` (add `IRealtimeChat`, `NoRealtime`)
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`LoadableViewModel` line ~54; `MessengerViewModel` lines 279-560)
- Modify: `src/GymMembership.App/Views/BasePage.cs`
- Modify: `src/GymMembership.App/Views/MessagesPage.xaml.cs:21-22`
- Test: `tests/GymMembership.Tests/RealtimeMessengerTests.cs`

**Interfaces:**
- Produces (used by Tasks 4, 5, 7):
  - `interface IRealtimeChat { bool Connected; event Action? ConnectionChanged; event Action? Changed; event Action<string>? Typing; event Action<IReadOnlySet<string>>? OnlineChanged; Task StartAsync(string userId); Task StopAsync(); Task WatchAsync(string? conversationKey); Task SendTypingAsync(string conversationKey); }`
  - `NoRealtime.Instance`
  - `LoadableViewModel.OnDisappearing()` (virtual, void)
  - `MessengerViewModel(IChatService, IProfileService, IAuthService, IProofPicker, IRealtimeChat? live = null)`; `RefreshEvery : TimeSpan`; settable `ChangeDelay`, `TypingHold`, `TypingEvery`.

- [ ] **Step 1: Write the failing tests.** Create `tests/GymMembership.Tests/RealtimeMessengerTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class RealtimeMessengerTests
{
    // a stand-in for Supabase Realtime that the test drives by hand
    public sealed class FakeRealtime : IRealtimeChat
    {
        public bool Connected { get; private set; }
        public event Action? ConnectionChanged;
        public event Action? Changed;
        public event Action<string>? Typing;
        public event Action<IReadOnlySet<string>>? OnlineChanged;
        public List<string> Started = [];
        public List<string?> Watched = [];
        public List<string> TypingSent = [];
        public int Stops;
        public Exception? FailStart;
        public Task StartAsync(string userId)
        {
            Started.Add(userId);
            if (FailStart is not null) throw FailStart;
            SetConnected(true);
            return Task.CompletedTask;
        }
        public Task StopAsync() { Stops++; SetConnected(false); return Task.CompletedTask; }
        public Task WatchAsync(string? key) { Watched.Add(key); return Task.CompletedTask; }
        public Task SendTypingAsync(string key) { TypingSent.Add(key); return Task.CompletedTask; }
        public void SetConnected(bool on) { if (Connected == on) return; Connected = on; ConnectionChanged?.Invoke(); }
        public void RaiseChanged() => Changed?.Invoke();
        public void RaiseTyping(string key) => Typing?.Invoke(key);
        public void RaiseOnline(params string[] ids) => OnlineChanged?.Invoke(new HashSet<string>(ids));
        public bool HasListeners => Changed is not null || Typing is not null || OnlineChanged is not null || ConnectionChanged is not null;
    }

    // counts how often the list is asked for, to see how many reloads a burst of signals causes
    sealed class CountingChat(IChatService inner) : IChatService
    {
        public int ThreadCalls;
        public (string CoachId, string? MemberId)? Pending { get => inner.Pending; set => inner.Pending = value; }
        public Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync() { ThreadCalls++; return inner.ThreadsAsync(); }
        public Task<AppResult<ChatPage>> MessagesAsync(ChatThread t, long? beforeId = null, int limit = 50) => inner.MessagesAsync(t, beforeId, limit);
        public Task<AppResult<bool>> SendAsync(ChatThread t, string body) => inner.SendAsync(t, body);
        public Task<AppResult<bool>> SendPhotoAsync(ChatThread t, string fileName, byte[] data, string caption) => inner.SendPhotoAsync(t, fileName, data, caption);
        public Task<AppResult<bool>> MarkReadAsync(ChatThread t) => inner.MarkReadAsync(t);
        public Task<AppResult<bool>> MarkDeliveredAsync() => inner.MarkDeliveredAsync();
        public Task<AppResult<bool>> ClaimAsync(ChatThread t, bool claim) => inner.ClaimAsync(t, claim);
        public Task<AppResult<bool>> SetOpenAsync(ChatThread t, bool open) => inner.SetOpenAsync(t, open);
        public Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query) => inner.SearchAsync(query);
    }

    public static async Task<(DemoGateway Gw, CountingChat Chat, FakeRealtime Rt, MessengerViewModel Vm)> Open(string email = "member@demo.io", DemoGateway? gw = null, FakeRealtime? rt = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        rt ??= new FakeRealtime();
        var chat = new CountingChat(new ChatService(gw, new NotificationService(gw)));
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw), new DemoPhotoPicker(), rt)
        { ChangeDelay = TimeSpan.FromMilliseconds(20), TypingHold = TimeSpan.FromMilliseconds(80) };
        await vm.OnAppearingAsync();
        return (gw, chat, rt, vm);
    }

    [Fact]
    public async Task Opening_the_page_starts_realtime_for_that_user_and_a_connected_page_polls_slowly()
    {
        var (gw, _, rt, vm) = await Open();
        Assert.Equal(new[] { gw.CurrentUserId! }, rt.Started);
        Assert.Equal(TimeSpan.FromSeconds(30), vm.RefreshEvery);
    }

    [Fact]
    public async Task Without_a_connection_the_page_polls_every_6_seconds_and_says_nothing_about_it()
    {
        var rt = new FakeRealtime { FailStart = new Exception("no network") };
        var (_, _, _, vm) = await Open(rt: rt);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.RefreshEvery);
        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.Threads);   // Messages loaded all the same
    }

    [Fact]
    public async Task The_interval_follows_the_connection()
    {
        var (_, _, rt, vm) = await Open();
        var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rt.SetConnected(false);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.RefreshEvery);
        Assert.Contains(nameof(vm.RefreshEvery), changed);
    }

    [Fact]
    public async Task A_burst_of_change_signals_causes_one_reload_after_the_pause()
    {
        var (_, chat, rt, _) = await Open();
        var before = chat.ThreadCalls;
        for (var i = 0; i < 5; i++) rt.RaiseChanged();
        await Task.Delay(300);
        Assert.Equal(before + 1, chat.ThreadCalls);
    }

    [Fact]
    public async Task A_change_signal_brings_in_a_new_message_without_touching_the_draft()
    {
        var (gw, _, rt, member) = await Open();
        member.Draft = "half typed";
        var count = member.Bubbles.Count;

        var (_, _, _, lena) = await Open("coach@demo.io", gw);   // Lena, the demo coach, answers
        lena.Draft = "see you at 6"; await lena.SendCommand.ExecuteAsync(null);

        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        rt.RaiseChanged();
        await Task.Delay(300);
        Assert.Equal(count + 1, member.Bubbles.Count);
        Assert.Equal("see you at 6", member.Bubbles.Last().Line.Body);
        Assert.Equal("half typed", member.Draft);
    }

    [Fact]
    public async Task Leaving_the_page_stops_realtime_and_later_signals_do_nothing()
    {
        var (_, chat, rt, vm) = await Open();
        vm.OnDisappearing();
        Assert.Equal(1, rt.Stops);
        Assert.False(rt.HasListeners);
        var before = chat.ThreadCalls;
        rt.RaiseChanged();
        await Task.Delay(100);
        Assert.Equal(before, chat.ThreadCalls);
    }

    [Fact]
    public async Task Coming_back_as_someone_else_restarts_realtime_for_them()
    {
        var (gw, _, rt, vm) = await Open("member@demo.io");
        var first = gw.CurrentUserId!;
        vm.OnDisappearing();
        await gw.SignOutAsync(); await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        await vm.OnAppearingAsync();
        Assert.Equal(new[] { first, gw.CurrentUserId! }, rt.Started);
        Assert.NotEqual(first, gw.CurrentUserId);
    }

    [Fact]
    public async Task Without_a_realtime_service_the_page_works_as_before()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), new DemoPhotoPicker());
        await vm.OnAppearingAsync();
        Assert.NotEmpty(vm.Threads);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.RefreshEvery);
        vm.OnDisappearing();   // nothing to stop, and no error
    }
}
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter RealtimeMessengerTests`
Expected: build error (`IRealtimeChat`, `OnDisappearing`, `RefreshEvery`, `ChangeDelay` do not exist).

- [ ] **Step 3: Add the seam.** Append to `src/GymMembership.Core/Services/Realtime.cs`:

```csharp
/// <summary>
/// Live updates for Messages. Every event is a hint: the app still asks the server for the truth, so a missed or doubled event
/// costs nothing. A failure of any kind only means Connected is false and the page keeps polling.
/// </summary>
public interface IRealtimeChat
{
    bool Connected { get; }
    event Action? ConnectionChanged;
    /// <summary>A message row was added or changed somewhere this user may read.</summary>
    event Action? Changed;
    /// <summary>The other side of the watched conversation is typing. The argument is that conversation's key (ChatThread.Key).</summary>
    event Action<string>? Typing;
    /// <summary>The ids of everyone online right now.</summary>
    event Action<IReadOnlySet<string>>? OnlineChanged;
    /// <summary>Connects and subscribes for this user. Safe to call again for the same user; a different user replaces the first.</summary>
    Task StartAsync(string userId);
    Task StopAsync();
    /// <summary>Joins the typing channel of one conversation, leaving the last one. Null just leaves.</summary>
    Task WatchAsync(string? conversationKey);
    Task SendTypingAsync(string conversationKey);
}

/// <summary>Demo mode and tests: never connects, never raises anything.</summary>
public sealed class NoRealtime : IRealtimeChat
{
    public static readonly NoRealtime Instance = new();
    public bool Connected => false;
#pragma warning disable CS0067   // the events exist for the interface and are never raised
    public event Action? ConnectionChanged;
    public event Action? Changed;
    public event Action<string>? Typing;
    public event Action<IReadOnlySet<string>>? OnlineChanged;
#pragma warning restore CS0067
    public Task StartAsync(string userId) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task WatchAsync(string? conversationKey) => Task.CompletedTask;
    public Task SendTypingAsync(string conversationKey) => Task.CompletedTask;
}
```

- [ ] **Step 4: Hook the page lifecycle.** In `MemberViewModels.cs`, `LoadableViewModel`, after `OnAppearingAsync`:

```csharp
    /// <summary>Called by the page each time it goes away.</summary>
    public virtual void OnDisappearing() { }
```
In `BasePage.cs` replace `OnDisappearing` and `OnRefreshTick`:

```csharp
    protected override void OnDisappearing()
    {
        _refresh?.Stop();
        (BindingContext as LoadableViewModel)?.OnDisappearing();
        base.OnDisappearing();
    }
```
```csharp
    async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (AutoRefresh is { } every && _refresh is not null) _refresh.Interval = every;   // a page whose pace depends on its connection
        if (BindingContext is LoadableViewModel live) await live.AutoRefreshAsync();
    }
```
In `MessagesPage.xaml.cs`, in the constructor add `vm.UiContext = SynchronizationContext.Current;` just before `BindingContext = vm;` (the page is built on the UI thread), and replace lines 21-22:

```csharp
	// new messages arrive instantly through Realtime; this timer is the safety net (slower while connected)
	protected override TimeSpan? AutoRefresh => (BindingContext as MessengerViewModel)?.RefreshEvery ?? TimeSpan.FromSeconds(6);
```

- [ ] **Step 5: Add the live behaviour to the view model.** In `MessengerViewModel`:

Change the class line to
```csharp
public partial class MessengerViewModel(IChatService chat, IProfileService profile, IAuthService auth, IProofPicker picker, IRealtimeChat? live = null) : LoadableViewModel
```
Add fields after `readonly Dictionary<string, string> drafts = new();`:

```csharp
    readonly IRealtimeChat rt = live ?? NoRealtime.Instance;
    bool listening;
    int changeTicket;
```
Add properties after `EmptyListText`:

```csharp
    // ---- live updates. Every Realtime event is a hint; the answer always comes from the server through AutoRefreshAsync.
    /// <summary>Realtime calls back on its own thread, and the lists must change on the UI thread. The page sets this; with none (tests) the work runs where it is.</summary>
    public SynchronizationContext? UiContext { get; set; }
    public TimeSpan ChangeDelay { get; set; } = TimeSpan.FromMilliseconds(300);   // a burst of signals becomes one reload
    /// <summary>How often the page looks again by itself: rarely while Realtime is connected, as before when not.</summary>
    public TimeSpan RefreshEvery => rt.Connected ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(6);
```
Replace `OnAppearingAsync` and `Reset` start, and add the live methods:

```csharp
    public override async Task OnAppearingAsync()
    {
        if (auth.UserId != myId)   // somebody else signed in: nothing of the last person stays on screen
        {
            var hadSomeone = myId is not null;
            Reset();
            if (hadSomeone) StopLive();   // the last person's connection, if any, goes
        }
        await LoadThreadsAsync(first: true);
        if (Selected is { } item) await ShowAsync(item, markRead: ShowChat);
        await StartLiveAsync();
    }

    public override void OnDisappearing() => StopLive();

    async Task StartLiveAsync()
    {
        if (myId is null) return;
        if (!listening)
        {
            rt.Changed += OnRealtimeChanged; rt.ConnectionChanged += OnRealtimeConnection;
            listening = true;
        }
        try { await rt.StartAsync(myId); }
        catch { /* Realtime is a bonus: without it the timer keeps the page fresh and nobody needs to hear about it */ }
        OnPropertyChanged(nameof(RefreshEvery));
    }

    void StopLive()
    {
        if (listening)
        {
            rt.Changed -= OnRealtimeChanged; rt.ConnectionChanged -= OnRealtimeConnection;
            listening = false;
        }
        changeTicket++;   // a reload still waiting for its pause is dropped
        _ = StopQuietlyAsync();
    }

    async Task StopQuietlyAsync() { try { await rt.StopAsync(); } catch { /* already gone */ } }

    void Post(Action work) { if (UiContext is { } ui) ui.Post(_ => work(), null); else work(); }
    void OnRealtimeConnection() => Post(() => OnPropertyChanged(nameof(RefreshEvery)));
    void OnRealtimeChanged() => Post(() => _ = ReloadSoonAsync());

    async Task ReloadSoonAsync()
    {
        var ticket = ++changeTicket;
        await Task.Delay(ChangeDelay);
        if (ticket == changeTicket && listening) await AutoRefreshAsync();   // the same quiet reload the timer does
    }
```
(`Reset()` is unchanged apart from now being followed by `StopLive()` above.)

- [ ] **Step 6: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests --filter RealtimeMessengerTests`
Expected: all 8 pass. Then `dotnet test tests/GymMembership.Tests` — everything else still green (`MessengerViewModelTests` constructs with four arguments).

- [ ] **Step 7: Commit.**
```bash
git add src tests
git commit -m "feat: messenger reloads on a realtime change signal; polling slows while connected"
```

---

### Task 4: Typing indicator

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`MessengerViewModel`)
- Modify: `src/GymMembership.App/Views/MessagesPage.xaml` (header)
- Test: `tests/GymMembership.Tests/RealtimeMessengerTests.cs` (append)

**Interfaces:**
- Consumes: `IRealtimeChat.Typing`, `WatchAsync`, `SendTypingAsync` from Task 3.
- Produces: `MessengerViewModel.OtherTyping : bool`, `StatusLine : string` (Task 5 extends it), settable `TypingHold`, `TypingEvery`.

- [ ] **Step 1: Write the failing tests.** Append inside `RealtimeMessengerTests`:

```csharp
    [Fact]
    public async Task The_open_conversation_is_watched_and_switching_changes_the_watched_channel()
    {
        var (_, _, rt, vm) = await Open();
        Assert.Equal(vm.Selected!.Thread.Key, rt.Watched.Last());
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.OpenCommand.ExecuteAsync(vm.Threads.Single());
        Assert.Equal(vm.Selected!.Thread.Key, rt.Watched.Last());
        Assert.StartsWith("support|", rt.Watched.Last());
    }

    [Fact]
    public async Task Typing_from_the_other_side_shows_then_hides_by_itself()
    {
        var (_, _, rt, vm) = await Open();
        Assert.False(vm.OtherTyping);
        rt.RaiseTyping(vm.Selected!.Thread.Key);
        Assert.True(vm.OtherTyping);
        Assert.Equal("typing…", vm.StatusLine);
        await Task.Delay(250);   // TypingHold is 80 ms in these tests
        Assert.False(vm.OtherTyping);
    }

    [Fact]
    public async Task Typing_in_a_conversation_that_is_not_open_is_ignored()
    {
        var (_, _, rt, vm) = await Open();
        rt.RaiseTyping("coach|someone|else");
        Assert.False(vm.OtherTyping);
    }

    [Fact]
    public async Task A_message_from_them_ends_the_typing_line()
    {
        var (gw, _, rt, member) = await Open();
        member.TypingHold = TimeSpan.FromSeconds(30);
        rt.RaiseTyping(member.Selected!.Thread.Key);
        Assert.True(member.OtherTyping);

        var (_, _, _, lena) = await Open("coach@demo.io", gw);
        lena.Draft = "here"; await lena.SendCommand.ExecuteAsync(null);

        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        await member.AutoRefreshAsync();
        Assert.False(member.OtherTyping);
    }

    [Fact]
    public async Task Typing_is_sent_at_most_once_in_the_window_and_never_for_an_empty_box()
    {
        var (_, _, rt, vm) = await Open();
        vm.TypingEvery = TimeSpan.FromHours(1);
        vm.Draft = "h"; vm.Draft = "he"; vm.Draft = "hel";
        Assert.Equal(new[] { vm.Selected!.Thread.Key }, rt.TypingSent);
        vm.Draft = "";
        Assert.Single(rt.TypingSent);
    }

    [Fact]
    public async Task With_no_wait_every_keystroke_is_a_ping()
    {
        var (_, _, rt, vm) = await Open();
        vm.TypingEvery = TimeSpan.Zero;
        vm.Draft = "h"; vm.Draft = "he";
        Assert.Equal(2, rt.TypingSent.Count);
    }

    [Fact]
    public async Task Restoring_a_saved_draft_in_another_conversation_is_not_typing_and_clears_the_line()
    {
        var (_, _, rt, vm) = await Open("carlo@demo.io");
        vm.TypingEvery = TimeSpan.Zero;
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.OpenCommand.ExecuteAsync(vm.Threads.Single());
        vm.Draft = "my payment is stuck";
        var pings = rt.TypingSent.Count;
        rt.RaiseTyping(vm.Selected!.Thread.Key);
        Assert.True(vm.OtherTyping);

        await vm.ShowTabCommand.ExecuteAsync("coach");   // leave: the draft is kept, the line goes
        Assert.False(vm.OtherTyping);
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.OpenCommand.ExecuteAsync(vm.Threads.Single());   // back: the draft returns by itself
        Assert.Equal("my payment is stuck", vm.Draft);
        Assert.Equal(pings, rt.TypingSent.Count);
    }

    [Fact]
    public async Task A_conversation_you_cannot_write_in_sends_no_typing()
    {
        var (_, _, rt, vm) = await Open();
        vm.TypingEvery = TimeSpan.Zero;
        vm.Selected = new ThreadItem(new ChatThread { Kind = "coach", CoachId = "x", MemberId = "y", WithId = "x", CanSend = false });
        var before = rt.TypingSent.Count;
        vm.Draft = "can't";
        Assert.Equal(before, rt.TypingSent.Count);
    }
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter RealtimeMessengerTests`
Expected: build error (`OtherTyping`, `StatusLine`, `TypingEvery`, `TypingHold` missing).

- [ ] **Step 3: Implement.** In `MessengerViewModel`:

Fields (next to `changeTicket`):
```csharp
    int typingTicket;
    DateTime lastTypingSent = DateTime.MinValue;
    bool restoringDraft;
```
Properties (next to `RefreshEvery`):
```csharp
    public TimeSpan TypingHold { get; set; } = TimeSpan.FromSeconds(4);    // "typing…" goes away this long after the last ping
    public TimeSpan TypingEvery { get; set; } = TimeSpan.FromSeconds(2);   // at most one ping this often while you type
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusLine))] private bool otherTyping;
    /// <summary>Under the name in the chat header: "typing…" now; Task 5 adds online / last active.</summary>
    public string StatusLine => OtherTyping ? "typing…" : "";
```
In `StartLiveAsync` add `rt.Typing += OnRealtimeTyping;` next to the other subscriptions, in `StopLive` add `rt.Typing -= OnRealtimeTyping;` and `StopTyping();`. After `await rt.StartAsync(myId);` add `if (Selected is { } open) await rt.WatchAsync(open.Thread.Key);` (inside the `try`). Add methods:

```csharp
    void OnRealtimeTyping(string key) => Post(() =>
    {
        if (Selected?.Thread.Key != key) return;   // typing in a conversation that is not on screen
        OtherTyping = true;
        _ = HideTypingLaterAsync();
    });

    async Task HideTypingLaterAsync()
    {
        var ticket = ++typingTicket;
        await Task.Delay(TypingHold);
        if (ticket == typingTicket) OtherTyping = false;
    }

    void StopTyping() { typingTicket++; OtherTyping = false; }

    // what you type is a ping to the other side, at most one in TypingEvery; text put back from a saved draft is not typing
    partial void OnDraftChanged(string value)
    {
        if (restoringDraft || !listening || string.IsNullOrEmpty(value) || Selected is not { } item || !item.Thread.CanSend) return;
        var now = DateTime.UtcNow;
        if (now - lastTypingSent < TypingEvery) return;
        lastTypingSent = now;
        _ = SendTypingQuietlyAsync(item.Thread.Key);
    }

    async Task SendTypingQuietlyAsync(string key) { try { await rt.SendTypingAsync(key); } catch { /* a missed ping is nothing */ } }
    async Task WatchQuietlyAsync(string? key) { try { await rt.WatchAsync(key); } catch { /* typing just will not show */ } }
```
In `OnSelectedChanged`, replace the draft lines so the restore does not look like typing and the watch moves:

```csharp
        if (oldValue?.Thread.Key == newValue?.Thread.Key) return;   // the same conversation, just refreshed
        if (oldValue is not null) drafts[oldValue.Thread.Key] = Draft;
        restoringDraft = true;
        Draft = newValue is null ? "" : drafts.GetValueOrDefault(newValue.Thread.Key, "");
        restoringDraft = false;
        PendingPhoto = null;   // a half-chosen photo is dropped when you move to another conversation
        StopTyping();
        if (listening) _ = WatchQuietlyAsync(newValue?.Thread.Key);
```
In `MergeLatestAsync`, in the `if (!known.TryGetValue(...)) Bubbles.Add(b);` branch: replace with

```csharp
            if (!known.TryGetValue(b.Line.MessageId, out var old)) { Bubbles.Add(b); if (!b.Mine) StopTyping(); }   // they sent it, so they are no longer typing
```
(`Selected` already notifies `StatusLine` in Task 5; for now `OtherTyping` does.)

- [ ] **Step 4: Show it on the page.** In `MessagesPage.xaml`, inside the header `VerticalStackLayout` (after the `Selected.Subtitle` label, line 115) add:

```xml
                        <Label Text="{Binding StatusLine}" Style="{StaticResource Caption}" IsVisible="{Binding StatusLine, Converter={StaticResource HasText}}"
                               SemanticProperties.Description="{Binding StatusLine}" />
```

- [ ] **Step 5: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass, including `XamlBindingTests` (it checks `StatusLine` exists).

- [ ] **Step 6: Commit.**
```bash
git add src tests
git commit -m "feat: typing indicator in the chat header"
```

---

### Task 5: Online dot and "Active … ago"

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`ThreadItem`, `MessengerViewModel`)
- Modify: `src/GymMembership.App/Controls/ThreadRowView.xaml`
- Test: `tests/GymMembership.Tests/RealtimeMessengerTests.cs` (append)

**Interfaces:**
- Consumes: `ChatThread.ShowsPresence`, `WithLastSeen`, `ActiveAgo` (Task 2); `IRealtimeChat.OnlineChanged` (Task 3); `StatusLine` (Task 4).
- Produces: `ThreadItem.IsOnline : bool`; `StatusLine` now also reads "Online" / "Active … ago".

- [ ] **Step 1: Write the failing tests.** Append:

```csharp
    [Fact]
    public async Task A_coach_who_is_online_gets_a_dot_in_the_list_and_Online_in_the_header()
    {
        var (_, _, rt, vm) = await Open();
        var lena = vm.Selected!.Thread.WithId!;
        Assert.False(vm.Threads.Single().IsOnline);
        rt.RaiseOnline(lena, "someone-else");
        Assert.True(vm.Threads.Single().IsOnline);
        Assert.Equal("Online", vm.StatusLine);
        rt.RaiseOnline();
        Assert.False(vm.Threads.Single().IsOnline);
        Assert.Equal("", vm.StatusLine);   // never seen: nothing is better than a guess
    }

    [Fact]
    public async Task When_they_are_not_online_the_header_says_when_they_were_last_active()
    {
        var (_, _, _, vm) = await Open();
        vm.Selected!.Thread.WithLastSeen = DateTime.UtcNow.AddHours(-3);
        Assert.Equal("Active 3 h ago", vm.StatusLine);   // read each time it is asked, so no event is needed in the test
    }

    [Fact]
    public async Task Typing_wins_over_online_in_the_header()
    {
        var (_, _, rt, vm) = await Open();
        rt.RaiseOnline(vm.Selected!.Thread.WithId!);
        rt.RaiseTyping(vm.Selected.Thread.Key);
        Assert.Equal("typing…", vm.StatusLine);
    }

    [Fact]
    public async Task A_members_own_front_desk_thread_never_shows_presence_even_if_its_id_is_online()
    {
        var (_, _, rt, vm) = await Open("carlo@demo.io");
        await vm.ShowTabCommand.ExecuteAsync("support");
        var desk = vm.Threads.Single();
        Assert.Null(desk.Thread.WithId);
        rt.RaiseOnline("", "u-emp", "u-admin");
        Assert.False(desk.IsOnline);
        await vm.OpenCommand.ExecuteAsync(desk);
        Assert.Equal("", vm.StatusLine);
    }

    [Fact]
    public async Task Front_desk_sees_a_member_as_online()
    {
        var gw = new DemoGateway();
        var (_, _, _, carlo) = await Open("carlo@demo.io", gw);
        await carlo.ShowTabCommand.ExecuteAsync("support");
        carlo.Draft = "hello"; await carlo.SendCommand.ExecuteAsync(null);

        var (_, _, rt, desk) = await Open("employee@demo.io", gw);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        rt.RaiseOnline("u-carlo");
        Assert.True(desk.Threads.Single().IsOnline);
        Assert.Equal("Online", desk.StatusLine);
    }

    [Fact]
    public async Task Leaving_the_page_forgets_who_was_online()
    {
        var (_, _, rt, vm) = await Open();
        rt.RaiseOnline(vm.Selected!.Thread.WithId!);
        vm.OnDisappearing();
        Assert.False(vm.Threads.Single().IsOnline);
        Assert.Equal("", vm.StatusLine);
    }
```
(The demo member id for Carlo is `u-carlo`, from `DemoGateway` `AddUser("u-carlo", ...)`.)

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter RealtimeMessengerTests`
Expected: build error (`IsOnline` missing).

- [ ] **Step 3: Implement.** In `ThreadItem` add: `[ObservableProperty] private bool isOnline;   // the other person is in the app right now (shown only where the thread allows it)`.

In `MessengerViewModel`: field `IReadOnlySet<string> online = new HashSet<string>();`. Change `StatusLine` to

```csharp
    public string StatusLine => OtherTyping ? "typing…" : PresenceText;
    string PresenceText => Selected?.Thread is { ShowsPresence: true } t
        ? (online.Contains(t.WithId!) ? "Online" : ActiveAgo.Text(t.WithLastSeen, DateTime.UtcNow))
        : "";
```
Add `nameof(StatusLine)` to the `NotifyPropertyChangedFor` list of the `selected` property. Subscribe `rt.OnlineChanged += OnRealtimeOnline;` in `StartLiveAsync` and unsubscribe in `StopLive`, where also `online = new HashSet<string>(); ApplyOnline();`. Add:

```csharp
    void OnRealtimeOnline(IReadOnlySet<string> ids) => Post(() => { online = ids; ApplyOnline(); });

    // the dot and the header text, for the people whose thread allows it
    void ApplyOnline()
    {
        foreach (var item in Threads) item.IsOnline = item.Thread is { ShowsPresence: true } t && online.Contains(t.WithId!);
        OnPropertyChanged(nameof(StatusLine));
    }
```
At the end of `Rebuild()` (after `OnPropertyChanged(nameof(EmptyListText));`) add `ApplyOnline();` so rows built by a reload get their dot.

- [ ] **Step 4: Dot and word in the list row.** In `ThreadRowView.xaml`, change the avatar cell (line 17) to a small grid with the dot and add the word under the name:

```xml
            <Grid>
                <c:Avatar Letters="{Binding Letters}" Url="{Binding AvatarUrl}" Diameter="44" VerticalOptions="Center" />
                <BoxView IsVisible="{Binding IsOnline}" WidthRequest="12" HeightRequest="12" CornerRadius="6" HorizontalOptions="End" VerticalOptions="End"
                         Color="{AppThemeBinding Light={StaticResource PlateGreenLight}, Dark={StaticResource PlateGreenDark}}" />
            </Grid>
```
Then in the name `VerticalStackLayout` after the preview label add:

```xml
                <Label Text="Online" Style="{StaticResource Caption}" IsVisible="{Binding IsOnline}" />
```
The word makes it readable without colour.

- [ ] **Step 5: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass.

- [ ] **Step 6: Commit.**
```bash
git add src tests
git commit -m "feat: online dot and last active in the conversation list and header"
```

---

### Task 6: The last-seen heartbeat

**Files:**
- Modify: `src/GymMembership.Core/Services/ProfileService.cs` (interface + class)
- Modify: `src/GymMembership.Core/Demo/DemoGateway.cs:1056` (before `case "mark_password_changed"`)
- Modify: `src/GymMembership.App/Controls/HeaderBar.xaml.cs:22-38`
- Test: `tests/GymMembership.Tests/ProfileTests.cs` (append one test)

**Interfaces:**
- Produces: `IProfileService.TouchSeenAsync() : Task<AppResult<bool>>`.

- [ ] **Step 1: Write the failing test.** Append inside `class ProfileTests` in `ProfileTests.cs` (it already has the helper `As(email)` returning the gateway and a `ProfileService`):

```csharp
    [Fact]
    public async Task Touching_last_seen_succeeds()
    {
        var (_, svc) = await As("member@demo.io");
        Assert.True((await svc.TouchSeenAsync()).Ok);
    }
```

- [ ] **Step 2: Run to verify it fails.**
Run: `dotnet test tests/GymMembership.Tests --filter Touching_last_seen`
Expected: build error `TouchSeenAsync` not defined.

- [ ] **Step 3: Implement.** In `IProfileService` add `/// <summary>Tells the server you are in the app (at most once every 5 minutes it counts). The result can be ignored.</summary> Task<AppResult<bool>> TouchSeenAsync();`. In `ProfileService` add:

```csharp
    public Task<AppResult<bool>> TouchSeenAsync() => Safe.RunAsync(async () => { await gw.RpcAsync("touch_last_seen"); return true; });
```
In `DemoGateway.Call` add before `case "mark_password_changed":`:
```csharp
            case "touch_last_seen": return null;   // the demo has no one to show it to
```
In `HeaderBar.Attach`, replace the timer block:

```csharp
		// look again once a minute while the app is open; every fifth look also tells the server you are here (last active)
		var profile = sp.GetRequiredService<IProfileService>();
		var ticks = 0;
		var timer = Application.Current!.Dispatcher.CreateTimer();
		timer.Interval = TimeSpan.FromSeconds(60);
		timer.Tick += (_, _) => { _ = vm.RefreshAsync(); if (++ticks % 5 == 0) _ = profile.TouchSeenAsync(); };
		timer.Start();
		shell.Loaded += (_, _) => _ = profile.TouchSeenAsync();   // and once when the app opens
		shell.Unloaded += (_, _) => timer.Stop();
```
Add `using GymMembership.Core.Services;` if missing. (The server ignores a touch within 5 minutes of the last, so a restarted shell cannot spam it.)

- [ ] **Step 4: Run to verify it passes.**
Run: `dotnet test tests/GymMembership.Tests`
Expected: all pass. A server that lacks `0017` answers "not found"; `Safe.RunAsync` turns that into a failed result nobody reads.

- [ ] **Step 5: Commit.**
```bash
git add src tests
git commit -m "feat: tell the server you are in the app, for last active"
```

---

### Task 7: The Supabase implementation and wiring

**Files:**
- Modify: `src/GymMembership.Core/Backend/SupabaseGateway.cs` (add two `internal` members after `CurrentUserId`)
- Create: `src/GymMembership.Core/Backend/SupabaseRealtimeChat.cs`
- Modify: `src/GymMembership.App/MauiProgram.cs:47` (after `IChatService`)

**Interfaces:**
- Consumes: `IRealtimeChat` (Task 3).
- Produces: `SupabaseRealtimeChat(SupabaseGateway)`.
- Library facts (checked in `supabase.realtime` 8.3.1's XML docs): `client.Realtime.Channel(string)`; `channel.Register(PostgresChangesOptions)`; `channel.AddPostgresChangeHandler(ListenType, handler)`; `channel.Register<T>(bool broadcastSelf, bool broadcastAck) where T : BaseBroadcast`; `broadcast.Send(string, object)`; `broadcast.AddBroadcastEventHandler`; `channel.Register<T>(string presenceKey) where T : BasePresence`; `presence.Track(object)`, `presence.Untrack()`, `presence.AddPresenceEventHandler(EventType.Sync, ...)`, `presence.CurrentState`; `channel.Subscribe()` / `Unsubscribe()`; `client.Remove(channel)`; `client.SetAuth(jwt)`; `client.ConnectAsync()` / `Disconnect()`; `client.AddStateChangedHandler`. The exact generic shapes are confirmed by the compiler in Step 3.

- [ ] **Step 1: Gateway accessors.** In `SupabaseGateway`, after `public string? CurrentUserId => ...`:

```csharp
    // for SupabaseRealtimeChat, which lives in this assembly: the connected client and the current sign-in token
    internal async Task<Client> ClientAsync() => await ReadyAsync();
    internal string? AccessToken => _client.Auth.CurrentSession?.AccessToken;
```

- [ ] **Step 2: Write the implementation.** Create `src/GymMembership.Core/Backend/SupabaseRealtimeChat.cs`:

```csharp
using GymMembership.Core.Services;
using Newtonsoft.Json;
using Supabase.Realtime;
using Supabase.Realtime.Broadcast;
using Supabase.Realtime.Interfaces;
using Supabase.Realtime.Models;
using Supabase.Realtime.Presence;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

namespace GymMembership.Core.Backend;

/// <summary>
/// Supabase Realtime for Messages. Row changes are only a hint (the app asks the server again); typing is a broadcast with no content;
/// online is presence on one channel, "online".
/// ponytail: the online channel is public, so anyone holding the app's public key can list the ids (UUIDs, no names) of everyone online.
/// A private channel with a policy on realtime.messages is the upgrade if that ever matters.
/// ponytail: the token Realtime holds is the one from sign-in; after about an hour the server may drop the row-change channel while
/// Connected still says true, and the page then relies on its 30 second timer until the next page visit. Refresh with SetAuth if that bites.
/// </summary>
public sealed class SupabaseRealtimeChat(SupabaseGateway gateway) : IRealtimeChat
{
    sealed class TypingPayload { [JsonProperty("from")] public string From { get; set; } = ""; }
    sealed class TypingBroadcast : BaseBroadcast<TypingPayload> { }
    sealed class Here : BasePresence { [JsonProperty("at")] public string At { get; set; } = ""; }

    readonly SemaphoreSlim gate = new(1, 1);
    RealtimeChannel? changes, online, typing;
    RealtimeBroadcast<TypingBroadcast>? typingBroadcast;
    RealtimePresence<Here>? presence;
    string? userId, watching;
    bool hooked;   // the socket state handler is added once, not on every visit to the page

    public bool Connected { get; private set; }
    public event Action? ConnectionChanged;
    public event Action? Changed;
    public event Action<string>? Typing;
    public event Action<IReadOnlySet<string>>? OnlineChanged;

    void SetConnected(bool on) { if (Connected == on) return; Connected = on; ConnectionChanged?.Invoke(); }

    public async Task StartAsync(string user)
    {
        await gate.WaitAsync();
        try
        {
            if (userId == user && Connected) return;
            await StopCoreAsync();
            var rt = (await gateway.ClientAsync()).Realtime;
            if (gateway.AccessToken is { } token) rt.SetAuth(token);   // so the server filters changes by this user's row access
            await rt.ConnectAsync();
            if (!hooked) { rt.AddStateChangedHandler((_, state) => SetConnected(state == Constants.SocketState.Open && userId is not null)); hooked = true; }

            changes = rt.Channel("messenger-changes");
            changes.Register(new PostgresChangesOptions("public", "messages"));
            changes.Register(new PostgresChangesOptions("public", "support_messages"));
            changes.AddPostgresChangeHandler(ListenType.All, (_, _) => Changed?.Invoke());
            await changes.Subscribe();

            online = rt.Channel("online");
            var here = presence = online.Register<Here>(user);
            here.AddPresenceEventHandler(IRealtimePresence.EventType.Sync, (_, _) => OnlineChanged?.Invoke(here.CurrentState.Keys.ToHashSet()));
            await online.Subscribe();
            here.Track(new Here { At = DateTime.UtcNow.ToString("o") });

            userId = user;
            SetConnected(true);
        }
        catch { await StopCoreAsync(); throw; }   // the view model treats a throw as "not connected"
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync(); } finally { gate.Release(); }
    }

    async Task StopCoreAsync()
    {
        SetConnected(false);
        userId = null; watching = null;
        try { presence?.Untrack(); } catch { /* the socket may already be gone */ }
        foreach (var c in new[] { changes, online, typing })
            try { if (c is not null) { c.Unsubscribe(); (await gateway.ClientAsync()).Realtime.Remove(c); } } catch { /* same */ }
        changes = online = typing = null; presence = null; typingBroadcast = null;
        try { (await gateway.ClientAsync()).Realtime.Disconnect(); } catch { /* same */ }
    }

    public async Task WatchAsync(string? key)
    {
        await gate.WaitAsync();
        try
        {
            if (key == watching || userId is null) return;
            if (typing is not null)
            {
                try { typing.Unsubscribe(); (await gateway.ClientAsync()).Realtime.Remove(typing); } catch { /* same */ }
                typing = null; typingBroadcast = null;
            }
            watching = key;
            if (key is null) return;
            var rt = (await gateway.ClientAsync()).Realtime;
            typing = rt.Channel("typing:" + key.Replace('|', ':'));
            typingBroadcast = typing.Register<TypingBroadcast>(broadcastSelf: false);
            typingBroadcast.AddBroadcastEventHandler((_, _) => Typing?.Invoke(key));
            await typing.Subscribe();
        }
        finally { gate.Release(); }
    }

    public async Task SendTypingAsync(string key)
    {
        if (key != watching || typingBroadcast is not { } b || userId is null) return;
        await b.Send("typing", new TypingBroadcast { Payload = new TypingPayload { From = userId } });
    }
}
```

- [ ] **Step 3: Build and let the compiler check the library names.**
Run: `dotnet build src/GymMembership.Core`
Expected: a clean build. If a member name or generic shape differs from the code above, the compiler says which one; correct it from `~/.nuget/packages/supabase.realtime/8.3.1/lib/netstandard2.1/Supabase.Realtime.xml` (search the member name) and the examples at `https://github.com/supabase-community/supabase-csharp/blob/master/packages/Realtime/README.md`. Keep the behaviour in this file's comments; change only names.

- [ ] **Step 4: Register it.** In `MauiProgram.cs`, after `s.AddSingleton<IChatService, ChatService>();` add:

```csharp
		// live updates need the real project; the demo and the tests use the do-nothing version
		s.AddSingleton<IRealtimeChat>(sp => sp.GetRequiredService<ISupabaseGateway>() is SupabaseGateway g ? new SupabaseRealtimeChat(g) : NoRealtime.Instance);
```

- [ ] **Step 5: Build the apps and run all tests.**
Run: `dotnet build gym-membership.slnx` (then the Windows target the README documents) and `dotnet test tests/GymMembership.Tests`
Expected: builds succeed; all tests pass. The Android build is `dotnet build src/GymMembership.App -f net10.0-android` (use the README's exact command if it differs).

- [ ] **Step 6: Commit.**
```bash
git add src
git commit -m "feat: Supabase Realtime for messages, typing and online status"
```

---

### Task 8: Push 0017 and check it against the real project  (STOP: needs the owner)

**Files:**
- Create: `scripts/check-realtime.mjs`

This task writes to the live database, so nothing here runs without the owner saying yes in the chat.

- [ ] **Step 1: Ask the owner to push `0017`.** Tell them: "`0017` changes your live database (adds the live-message publication, the last-seen table, and replaces `chat_threads`). Run the pre-check below, then `! npx supabase db push`." Pre-check for them to run in the SQL editor: `select count(*) from pg_publication_tables where pubname = 'supabase_realtime' and tablename in ('messages','support_messages');` Expected `0` (so the `add table` cannot fail with "already member"). Wait for them to confirm the push output.

- [ ] **Step 2: Write the live check.** Create `scripts/check-realtime.mjs`:

```js
// Live check of phase 4c against the real project. Needs three accounts and writes ONE real message to the front desk thread of account A:
//   A = a member (sends the message), B = front desk staff (must receive the change), C = a different member (must NOT receive it).
//   SUPABASE_URL SUPABASE_ANON_KEY  A_EMAIL A_PASSWORD  B_EMAIL B_PASSWORD  C_EMAIL C_PASSWORD   node scripts/check-realtime.mjs
// Secrets are never printed.
const base = (process.env.SUPABASE_URL ?? '').replace(/\/+$/, ''), key = process.env.SUPABASE_ANON_KEY ?? '';
const need = ['A', 'B', 'C'].flatMap(x => [`${x}_EMAIL`, `${x}_PASSWORD`]);
if (!base || !key || need.some(n => !process.env[n])) { console.error('Set SUPABASE_URL, SUPABASE_ANON_KEY and ' + need.join(', ')); process.exit(2); }

const signIn = async x => {
  const r = await fetch(`${base}/auth/v1/token?grant_type=password`, { method: 'POST', headers: { apikey: key, 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: process.env[`${x}_EMAIL`], password: process.env[`${x}_PASSWORD`] }) });
  const j = await r.json(); if (!j.access_token) throw new Error(`${x} sign-in failed (${r.status})`);
  return { token: j.access_token, id: j.user.id };
};
const listen = (who, topic) => new Promise((resolve, reject) => {
  const ws = new WebSocket(base.replace(/^http/, 'ws') + `/realtime/v1/websocket?apikey=${key}&vsn=1.0.0`);
  const seen = []; let ready = false;
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.event === 'phx_reply' && m.ref === '1') { if (m.payload.status === 'ok') { ready = true; resolve({ ws, seen }); } else reject(new Error(`${who} join: ${JSON.stringify(m.payload).slice(0, 160)}`)); }
    if (m.event === 'postgres_changes') seen.push(m.payload.data?.table);
  };
  ws.onopen = () => ws.send(JSON.stringify({ topic, event: 'phx_join', ref: '1', payload: { access_token: who.token, config: {
    postgres_changes: [{ event: '*', schema: 'public', table: 'messages' }, { event: '*', schema: 'public', table: 'support_messages' }] } } }));
  setTimeout(() => !ready && reject(new Error('join timed out')), 10000);
});

const [A, B, C] = await Promise.all(['A', 'B', 'C'].map(signIn));
const [lb, lc] = await Promise.all([listen(B, 'realtime:check-b'), listen(C, 'realtime:check-c')]);
await new Promise(r => setTimeout(r, 1500));   // let the subscriptions settle
const t0 = Date.now();
const res = await fetch(`${base}/rest/v1/rpc/send_support_message`, { method: 'POST', headers: { apikey: key, Authorization: `Bearer ${A.token}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ p_member: A.id, p_body: 'realtime check ' + new Date().toISOString() }) });
if (!res.ok) { console.log('FAIL  send_support_message', res.status, (await res.text()).slice(0, 160)); process.exit(1); }
await new Promise(r => setTimeout(r, 3000));
let failed = 0;
const check = (name, ok, detail = '') => { console.log(`${ok ? 'ok   ' : 'FAIL '} ${name}${detail}`); if (!ok) failed++; };
check('front desk (B) is told about the new support message', lb.seen.includes('support_messages'), ` (seen: ${lb.seen.join(',') || 'nothing'})`);
check('an unrelated member (C) is told nothing', lc.seen.length === 0, ` (seen: ${lc.seen.join(',') || 'nothing'})`);
console.log(`signal arrived within ${3000} ms window; sent at +${Date.now() - t0 - 3000} ms`);
lb.ws.close(); lc.ws.close();
process.exit(failed ? 1 : 0);
```

- [ ] **Step 3: Ask before running it.** Say what it does ("it sends one real message, 'realtime check <time>', into account A's front desk thread, and needs accounts for a member, front desk staff and a second member") and wait for a yes and for the three sets of credentials to be put in the environment by the owner (`! $env:A_EMAIL=...` lines; never pasted into chat). Then run: `node scripts/check-realtime.mjs`
Expected: `ok   front desk (B) is told ...` and `ok   an unrelated member (C) is told nothing`. If B is told nothing, check that `0017` is applied (`select * from pg_publication_tables where pubname='supabase_realtime'`) and that B has `support:reply`. If C is told something, **stop**: Realtime is not enforcing row access and the design in the spec section 3.1 must be revisited before shipping.

- [ ] **Step 4: Commit the script.**
```bash
git add scripts/check-realtime.mjs
git commit -m "test: live two-account check that chat changes reach only the right people"
```

---

### Task 9: Look at it, review it, record it

**Files:**
- Modify: `docs/superpowers/specs/2026-10-16-phase4c-realtime-design.md` (section 3.3, 3.4, 5)
- Modify: `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 4c recorded)
- Modify: `docs/progress-2026-10-16.md` (phase table row 4c)

- [ ] **Step 1: Check on Windows and the Android emulator with two real accounts.** Sign in as a member on the Windows build and as that member's coach (or front desk) on the Android emulator, against the real project (the `supabase.json` that is committed as a placeholder must hold the real values for this; do not commit them). Verify and write down what you saw:
  1. A message sent on one device appears on the other in about a second with no waiting.
  2. "typing…" shows while the other person types and clears within about 4 s of stopping, and when their message arrives.
  3. The green dot and the word "Online" show for a person who has the app open, and "Active … ago" shows once they close it.
  4. Switch the Windows machine to airplane mode: the page keeps working; messages then arrive within 6 s of reconnecting (the page polls faster when Realtime is down).
  5. Sign out and in as another person with Messages open: nothing of the first person remains.
  6. Open Messages as a member: the "Front desk" row has no dot and no "Active" text.
  Realtime that does not connect on Android is a result, not a failure: leave the 6 s polling and report it to the owner.

- [ ] **Step 2: Whole-branch checks.** Run `dotnet test tests/GymMembership.Tests` and `cd supabase/tests/local && npm test`; both must show 0 failures. Build the Android and Windows targets. Use superpowers:verification-before-completion: paste the real counts in the final message.

- [ ] **Step 3: Independent review.** Run `ponytail:ponytail-review` (CLAUDE.md) and a correctness review (`code-review`) on the diff from `main..HEAD` limited to the 4c commits; fix what they find with a test each.

- [ ] **Step 4: Correct and record the docs.** In the 4c spec: section 3.3 and 3.4 say the column is `profiles.last_seen_at`; change to the locked table `user_last_seen` with the reason (0006 table-wide select), add "last seen only while the hire is current" and the `online` channel ceiling (ids visible to key holders), and fill section 5's three open items with what Task 8 and Step 1 found. In the redesign spec and the progress doc mark 4c done (or "done, polling fallback on Android" if that is the result) and list the new deferred items (token refresh for Realtime; foreground-resume heartbeat; Realtime for the bell and amenity status).

- [ ] **Step 5: Commit.**
```bash
git add docs
git commit -m "docs: phase 4c recorded"
```
