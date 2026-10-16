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
- **Last active:** new column `profiles.last_seen_at`. A new function `touch_last_seen()` sets it to `now()` for the caller, at most once per 5 minutes (a call sooner than that changes nothing). The app calls it when it comes to the foreground and every 5 minutes while open.
- `last_seen_at` is **not** readable through a normal profile read. It is returned only by `chat_threads`, only for the other person in a conversation the caller belongs to, and never for a member's view of "Front desk".
- When the other person is not online the header says "Active 2 h ago" (in words), or nothing if they have never been seen.

### 3.4 Data changes (`0017`)

| Change | Detail |
| --- | --- |
| Publication | `messages`, `support_messages` added to `supabase_realtime` |
| Column | `profiles.last_seen_at timestamptz` (null until first seen); not in any column grant for direct read |
| Function | `touch_last_seen()`, `security definer`, own row only, rate limited to once per 5 minutes |
| Function | `chat_threads` also returns `with_last_seen` for non-support (coach) threads and for staff looking at a member, otherwise null |

Notifications and amenity status are **not** added to the publication in this phase. The bell keeps its one-minute refresh.

### 3.5 Code shape

- `GymMembership.Core`: a small `IRealtimeChat` service with `Subscribe`, `SendTyping`, `Track`, and events `Changed`, `Typing(conversationKey)`, `PresenceChanged`. A fake for tests. The Supabase implementation sits next to `SupabaseGateway` (which currently sets `AutoConnectRealtime = false`; the connection is opened by this service, only while needed).
- `MessengerViewModel` gets the debounce, the typing and presence state, and the timer interval. The page only passes the interval to `BasePage`.
- Realtime failure never shows an error. It only means the app is "not connected" and polls faster.

## 4. Testing

- **xUnit** with the fake service: change signal causes one reload after the debounce; a burst causes one reload; typing hides after the timeout and when a message arrives; typing is sent at most every 2 seconds; the timer is 30 s connected and 6 s not; sign-out unsubscribes; a Realtime error leaves Messages working.
- **Database checks** (`supabase/tests/local`): `touch_last_seen` updates only the caller and not twice within 5 minutes; `chat_threads` shows `with_last_seen` only to people in the conversation; a member sees null for "Front desk"; an unrelated user cannot read another's `last_seen_at`. The publication change is checked by name.
- **Live check** after `0017` is pushed (asked first): two real accounts, a message in each direction, typing, presence, and RLS (a user who is not in the conversation receives no change events).
- **By hand** on Windows and on the Android emulator, with the polling fallback checked by turning Realtime off.

## 5. Things to verify during the build (not yet known)

1. The Supabase C# client (`Supabase` 8.3.1) connects and receives change events with the user token on Windows and on Android.
2. Change events really are filtered by the user's row access on the real project.
3. A dropped connection (airplane mode, sleep) is noticed and the app falls back to the 6 second timer, then reconnects.

If 1 fails on Android, the polling fallback ships and only typing and last-active are dropped, as the redesign spec says.

## 6. Not in this phase

Push notifications while the app is closed, Realtime for the bell and amenity status, and tap-to-enlarge or any other photo item from the deferred list.
