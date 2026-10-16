# Phase 4c: instant messages, typing, online status

*Written 16 Oct 2026. Part of the role redesign (`2026-10-15-role-redesign-design.md`, decision 6 "Live updates"). Builds on phases 4a (messenger) and 4b (photos).*

## 1. Goal

A message sent from one account appears on the other person's screen within about a second, without waiting for the 6 second refresh. The other person can also see that you are typing, and whether you are online or when you were last active.

**Success:** with two real accounts on the real project, a sent message shows up on the other side in about a second; "…is typing" shows and clears; the online dot and "last active" are right. If Realtime cannot connect, Messages still works exactly as today.

## 2. What was already decided

- Typing and online status use Realtime broadcast and presence. They need no tables.
- The chat tables go into the `supabase_realtime` publication (redesign spec section 3.3).
- If Realtime proves unreliable, polling stays (redesign spec section 6, "Risks").
- Chosen for delivery: **database change events** (not a broadcast ping from the sender), so the server decides who is told and a lost ping cannot hide a message.

**Checked on 16 Oct 2026** against the real project: two connections joined a broadcast and presence channel in about 0.45 s, a ping from one reached the other, and both saw each other's presence. This was from Windows through Node with the public key, so it proves the project's Realtime service, **not** the MAUI client, Android, or change events (the chat tables are not published yet).

## 3. Design

### 3.1 Instant delivery

- Migration `0017` adds `public.messages` and `public.support_messages` to the `supabase_realtime` publication.
- While Messages is open, the app subscribes to new and changed rows (inserts and updates, so Sent/Delivered/Read also change live) on both tables with the signed-in user's token, so the server only sends changes for rows that user may read.
- A change event is **only a signal**. Nothing in its payload is trusted or shown. After about 300 ms (so a burst becomes one reload) the app calls the page's existing `AutoRefreshAsync`. This keeps one load path, the one already tested and reviewed in 4a and 4b.
- The 6 second timer stays as a safety net: 30 seconds while Realtime is connected, 6 seconds when it is not (never connected, dropped, or signed out of it).
- The page unsubscribes when it closes, and on sign-out.

### 3.2 Typing

- Sent as Realtime broadcast on a channel named from the conversation's ids. The ping holds no message text and is not stored.
- While you type, the app sends at most one ping every 2 seconds. The other side shows "… is typing" and hides it after about 4 seconds without a ping, or when a message from that person arrives.
- Privacy choice: the channel is public but named from ids nobody can list, and carries nothing but "typing". A private channel with a database policy was rejected as more setup for almost no extra protection.

### 3.3 Online and last active

- **Online:** while the app is open the user's id is tracked on a presence channel. A green dot shows beside the other person in the conversation list and chat header when they are present. The dot also has a text label ("Online") for accessibility, not colour alone.
- **Who sees whom:** only the two people in a conversation. Member and coach see each other. Front desk sees a member. A member sees **no** indicator for "Front desk", because staff change.
- **Last active:** new table `user_last_seen (user_id, seen_at)`. A new function `touch_last_seen()` sets the caller's row to `now()`, at most once per 5 minutes (a call sooner than that changes nothing). The app calls it when it opens and on every fifth minute of the header's one-minute timer while it stays open. (Changed from the first draft, which put a `last_seen_at` column on `profiles`: migration `0006` grants `select` on every table to signed-in users, so a profiles column would have been readable by anyone who can read that profile row, staff included. A table with no grant and no policy can only be read by the `security definer` function `chat_threads`.)
- Last seen is **not** readable by any direct query. It is returned only by `chat_threads`, only for the other person in a coach chat **while the hire is current**, and to front desk for a member; never for a member's view of "Front desk", and nothing for an ended hire.
- **Ceiling:** online status is presence on one public channel, `online`, so anyone holding the app's public key can list the ids (UUIDs, no names) of everyone online. The screens only show a dot for people in your conversations. A private channel with a policy on `realtime.messages` is the upgrade if this ever matters.
- When the other person is not online the header says "Active 2 h ago" (in words), or nothing if they have never been seen.

### 3.4 Data changes (`0017`)

| Change | Detail |
| --- | --- |
| Publication | `messages`, `support_messages` added to `supabase_realtime` |
| Table | `user_last_seen (user_id uuid pk, seen_at timestamptz)`; row level security on, no grant and no policy for `anon` or `authenticated` |
| Function | `touch_last_seen()`, `security definer`, own row only, rate limited to once per 5 minutes |
| Function | `chat_threads` also returns `with_last_seen` (the last column) for coach threads with a current hire and for staff looking at a member, otherwise null; replaced with a drop and create because the return type changed |

Notifications and amenity status are **not** added to the publication in this phase. The bell keeps its one-minute refresh.

### 3.5 Code shape

- `GymMembership.Core`: a small `IRealtimeChat` service with `StartAsync`, `StopAsync`, `WatchAsync(conversationKey)`, `SendTypingAsync`, `Connected`, and events `Changed`, `Typing(conversationKey)`, `OnlineChanged(ids)`, `ConnectionChanged`. `NoRealtime` for demo mode; a fake in the tests. The Supabase implementation sits next to `SupabaseGateway` (which currently sets `AutoConnectRealtime = false`; the connection is opened by this service, only while needed).
- `MessengerViewModel` gets the debounce, the typing and presence state, and the timer interval. The page only passes the interval to `BasePage`.
- Realtime failure never shows an error. It only means the app is "not connected" and polls faster.

## 4. Testing

- **xUnit** with the fake service: change signal causes one reload after the debounce; a burst causes one reload; typing hides after the timeout and when a message arrives; typing is sent at most every 2 seconds; the timer is 30 s connected and 6 s not; sign-out unsubscribes; a Realtime error leaves Messages working.
- **Database checks** (`supabase/tests/local`): `touch_last_seen` updates only the caller and not twice within 5 minutes; `chat_threads` shows `with_last_seen` only to people in the conversation; a member sees null for "Front desk"; an unrelated user cannot read another's `last_seen_at`. The publication change is checked by name.
- **Live check** after `0017` is pushed (asked first): two real accounts, a message in each direction, typing, presence, and RLS (a user who is not in the conversation receives no change events).
- **By hand** on Windows and on the Android emulator, with the polling fallback checked by turning Realtime off.

## 5. Things to verify during the build

1. **Open.** The Supabase C# client (`Supabase` 8.3.1) connects and receives change events with the user token on Windows and on Android. The code compiles and both apps build; it has not yet been run inside the app on a device.
2. **Verified 16 Oct 2026** on the real project with `scripts/check-realtime.mjs`: front desk was told of a new support message, and an unrelated member was told nothing, so change events follow the user's row access.
3. **Open.** A dropped connection (airplane mode, sleep) is noticed and the app falls back to the 6 second timer, then reconnects. Covered with a fake in the unit tests; not yet tried on a device.

If 1 fails on Android, the polling fallback ships and only typing and last-active are dropped, as the redesign spec says.

## 6. Not in this phase

Push notifications while the app is closed, Realtime for the bell and amenity status, and tap-to-enlarge or any other photo item from the deferred list.
