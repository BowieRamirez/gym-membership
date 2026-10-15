# Role Redesign, Phase 4a: Messenger Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. UI task (4): invoke the `frontend-design:frontend-design` skill first. Finish with `ponytail:ponytail-review` (CLAUDE.md).

**Goal:** One Messenger-style chat for members, coaches and front desk: a conversation list with avatars, last message, time and unread badges; chat bubbles with sent, delivered and read status; a sticky message box; members get Coach and Staff conversations; front desk gets an inbox of member conversations they can take and close; everyone can search people and message text. The existing coach chat becomes reliable: bounded queries, full history by paging, no wrong-conversation flashes.

**Architecture:** One migration (`0015`) adds delivery tracking, a front-desk conversation (`support_threads`, `support_messages`) and a handful of functions that do the heavy reading on the server (conversation summaries with unread counts, a page of messages, mark delivered and read, search). The app gets a `ChatService` that replaces `MessageService`, a `MessengerViewModel`, and a rewritten `MessagesPage` with a two-pane layout on desktop and list-then-chat on phones. New messages arrive by polling (every few seconds while the page is open); Realtime comes in plan 4c.

**Tech Stack:** Supabase Postgres (embedded-Postgres local tests), .NET 10 MAUI (Windows + Android), CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 4 of section 4; database items 3.1 to 3.3).

## Why phase 4 is three plans

Phase 4 in the spec is the biggest piece of the redesign. It is split so each plan ends with working software:

| Plan | Contents |
| --- | --- |
| **4a (this plan)** | Database support, `ChatService`, the new Messenger UI, front-desk conversations, message status, search, polling. |
| 4b (next) | Photos in chat: `chat-attachments` bucket, `attachment_path` columns, picker, thumbnails. |
| 4c (after) | Supabase Realtime for instant delivery, typing indicator, online and last-active status, `last_seen_at`. It needs a feasibility probe of Realtime on Windows and Android first, so it must not block 4a and 4b. |

## Database changes in this phase (the owner asked to be told)

| Change | Kind |
| --- | --- |
| `messages`: new column `delivered_at` | additive |
| **New table** `support_threads` (one per member: who took it, open or closed, last message time) | additive |
| **New table** `support_messages` (the member-to-front-desk conversation; text only, 2000 characters at most) | additive |
| New functions: `chat_threads`, `chat_messages`, `mark_chat_read`, `mark_chats_delivered`, `send_support_message`, `claim_support_thread`, `set_support_status`, `chat_search` | additive |
| New permission `support:reply` for admin and front desk | reference data |

Nothing is dropped. Support tables are written only through functions. **`0012`, `0013` and `0014` are not on the real Supabase project yet; push them first, then `0015`, in order.** No storage bucket and no Realtime change in this plan.

## Global Constraints

- Migrations are additive; `0015` may be edited in place until the owner pushes it.
- Privacy: a coach chat is readable only by its coach and its member (not by admins or staff); a support thread is readable only by its member and by staff with `support:reply`. A member never sees which staff member answered (they see "Front desk"); staff see names.
- Reading is bounded: no screen downloads a whole table of messages. A page is at most 100 messages (default 50).
- Colors and type come from the existing tokens; WCAG AA must hold (`ContrastTests` stays green). Status is shown in words, never by color alone.
- Trim-safe bindings only: no `RelativeSource AncestorType`; list buttons and taps use handlers through `BasePage.Row<TVm>`. Controls update their children from `BindableProperty` callbacks.
- Every new Core behavior has an xUnit test against `DemoGateway`; every new database rule has a test in `supabase/tests/local/rules.mjs`; every new request shape is checked in `SupabaseGatewayTests`.
- Pages that refresh themselves use the quiet `AutoRefreshAsync` pattern from phase 3: it never clears an error line or a draft, shows no new error, and swaps only rows that changed.
- Never push or merge without the owner saying so. Never commit `docs/screenshotsforexample/` or `supabase.json.bak`. `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo-mode checks and put it back.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Why the current chat is unreliable (found by reading the code)

1. `MessageService.ThreadsAsync` downloads every message you have ever received just to learn who you talk to. PostgREST silently caps a response at its row limit (1000 by default), so past that point conversations vanish and history is cut off without any error.
2. `MessageService.MessagesAsync` also downloads the whole conversation, unordered by the server and unlimited.
3. `MessagesViewModel` starts loading a conversation with a fire-and-forget call when the selection changes and has no "newest request wins" guard, so a slow answer for conversation A can land after you switched to B and show A's messages under B.
4. Nothing ever sets `read_at`, so read status cannot exist.

Tasks 1 to 3 fix these at the root.

## Decisions made while planning (change any and this plan changes)

| Question | Decision |
| --- | --- |
| Delivered vs read | **Delivered** when the recipient's app has loaded their conversation list; **read** when they have the conversation open. Shown under the sender's bubbles as "Sent", "Delivered" or "Read". |
| Who can write to front desk | Any signed-in member, including guests with no active plan (a guest with a payment problem is exactly who needs help). Coach chats keep today's rule: an active hire and an active plan. |
| Who is alerted about a member's message | Everyone on staff, or only the person who took the conversation once someone has. A staff reply alerts the member. Both use the existing bell. |
| Taking a conversation | Any staff member can take an unclaimed conversation; only the one who took it (or an admin) can release it. Anyone with `support:reply` can still answer any thread. |
| Closing | Staff can close and reopen. A member writing again reopens it. Closing does not block the member from writing. |
| Search | Matches the other person's name or any message text, minimum two characters, newest match first; one row per conversation. Available to every role (it only ever sees your own conversations). |
| Reading new messages | The open chat refreshes every 5 seconds and the list every 10 seconds while the page is open. |
| Layout | Desktop: list (340 wide) beside the chat. Phone: list first, then the chat with a Back button. The layout is chosen by device type, not by window width. |
| Old types | `IMessageService`, `MessageService`, `MessagesViewModel`, `ThreadRow`, `MessageRow` are replaced, not kept beside the new ones. |

## Review Focus

Failure modes the happy path will not show. Each has a test in the task named.

1. Switching conversations quickly: a slow answer for the first must never show under the second. [Task 3]
2. More than 50 messages: only the newest 50 load, "Load earlier messages" adds the next page, with no duplicates, and the list of conversations is unaffected. [Task 1, 2, 3]
3. A guest member can write to front desk; a member with no coach sees an empty Coach tab and a working Staff tab. [Task 1, 3]
4. Privacy: a member cannot read another member's thread or conversation, a coach cannot read another coach's, and staff cannot read coach chats. [Task 1]
5. Offline send: the draft stays in the box with an error line, no phantom bubble appears, and the background refresh stays silent. [Task 3]

---

### Task 1: Migration `0015` and its database tests

**Files:**
- Create: `supabase/migrations/0015_messenger.sql`
- Modify: `supabase/tests/local/rules.mjs` (append above the final summary line)

**Interfaces:**
- Produces:
  - `messages.delivered_at timestamptz`.
  - Tables `support_threads (member_id pk, claimed_by, status 'open'|'closed', last_message_at, created_at)` and `support_messages (message_id, member_id, sender_id, body, sent_at, delivered_at, read_at)`.
  - `chat_threads()` returns `(kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, with_avatar text, last_body text, last_at timestamptz, last_from_me boolean, unread integer, can_send boolean, status text, claimed_by uuid, claimed_by_name text)`; `kind` is `'coach'` or `'support'`.
  - `chat_messages(p_kind text, p_coach uuid, p_member uuid, p_before bigint default null, p_limit int default 50)` returns `(message_id bigint, sender_id uuid, body text, sent_at timestamptz, delivered_at timestamptz, read_at timestamptz, sender_name text)`, newest first.
  - `mark_chat_read(p_kind text, p_coach uuid, p_member uuid)`, `mark_chats_delivered()`, `send_support_message(p_member uuid, p_body text) returns bigint`, `claim_support_thread(p_member uuid, p_claim boolean)`, `set_support_status(p_member uuid, p_open boolean)`, `chat_search(p_query text)` returns `(kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, snippet text, sent_at timestamptz)`.
  - Permission `support:reply`.

- [ ] **Step 0: Create the branch**

```bash
git checkout role-redesign-phase3 && git checkout -b role-redesign-phase4a
```

- [ ] **Step 1: Write the failing tests**

Append to `supabase/tests/local/rules.mjs` above `console.log(\`\n${pass} passed, ${failed} failed\`);`. The earlier sections define `admin`, `desk` (employee), `coach`, `alex`, `bea` (members), and the helpers `run`, `code`, `test`, `db`. Set-up rows are written as the superuser with `db.query`; behaviour is checked as each person with `run`. If a set-up insert trips an earlier rule (for example one active hire per member), read that rule and adjust only the fixture, not the assertions.

```js
console.log('\nmessenger (0015)');
const say = (who, kind, coachId, memberId, body) => kind === 'support'
  ? run(who, `select send_support_message($1, $2)`, [memberId, body])
  : db.query(`insert into messages (coach_id, member_id, sender_id, body) values ($1, $2, $3, $4)`, [coachId, memberId, who, body]);
const threads = async uid => (await run(uid, `select * from chat_threads()`)).rows;
const page = (uid, kind, c, m, before = null, limit = 50) => run(uid, `select * from chat_messages($1, $2, $3, $4, $5)`, [kind, c, m, before, limit]);

// a coach chat between the coach and Bea, with a hire so it is open
await db.query(`insert into coach_hires (coach_id, member_id) values ($1, $2) on conflict do nothing`, [coach, bea]);
for (let i = 1; i <= 3; i++) await say(i % 2 ? bea : coach, 'coach', coach, bea, `coach chat ${i}`);

await test('the conversation list shows the last message, who sent it and the unread count, and nothing the person is not part of', async () => {
  const t = (await threads(bea)).find(r => r.kind === 'coach' && r.coach_id === coach);
  assert.equal(t.last_body, 'coach chat 3'); assert.equal(t.last_from_me, true); assert.equal(t.unread, 1);   // the coach's message 2 is waiting
  assert.equal(t.with_id, coach);
  const c = (await threads(coach)).find(r => r.kind === 'coach' && r.member_id === bea);
  assert.equal(c.unread, 2); assert.equal(c.with_id, bea);
  assert.equal((await threads(alex)).filter(r => r.kind === 'coach' && (r.member_id === bea)).length, 0);   // Alex is not in that chat
});
await test('staff and admin cannot read a coach chat, and a member cannot read someone else\'s', async () => {
  assert.equal((await threads(desk)).filter(r => r.kind === 'coach').length, 0);
  assert.match(await code(page(desk, 'coach', coach, bea)), /42501/);
  assert.match(await code(page(admin, 'coach', coach, bea)), /42501/);
  assert.match(await code(page(alex, 'coach', coach, bea)), /42501/);
  assert.equal((await page(bea, 'coach', coach, bea)).rowCount, 3);
});
await test('a page is newest first, bounded, and the next page starts after the last id (150 messages, no duplicates)', async () => {
  for (let i = 4; i <= 150; i++) await say(i % 2 ? bea : coach, 'coach', coach, bea, `coach chat ${i}`);
  const first = (await page(bea, 'coach', coach, bea)).rows;
  assert.equal(first.length, 50); assert.equal(first[0].body, 'coach chat 150');
  const second = (await page(bea, 'coach', coach, bea, first.at(-1).message_id)).rows;
  assert.equal(second.length, 50); assert.equal(second[0].body, 'coach chat 100');
  const rest = (await page(bea, 'coach', coach, bea, second.at(-1).message_id, 500)).rows;   // 500 is clamped to 100
  assert.equal(rest.length, 50);
  assert.equal(new Set([...first, ...second, ...rest].map(r => r.message_id)).size, 150);
});
await test('opening a conversation marks what you received delivered and read, and clears its alert; the sender sees it', async () => {
  await db.query(`delete from user_notifications where user_id = $1`, [bea]);
  await db.query(`insert into user_notifications (user_id, type, title, reference) values ($1, 'message', 'New message', jsonb_build_object('coach_id', $2::text, 'member_id', $3::text))`, [bea, coach, bea]);
  await run(bea, `select mark_chat_read('coach', $1, $2)`, [coach, bea]);
  assert.equal((await threads(bea)).find(r => r.kind === 'coach').unread, 0);
  const mine = (await page(coach, 'coach', coach, bea, null, 2)).rows;   // the coach's view of Bea's newest messages
  assert.ok(mine.every(r => r.read_at === null || r.sender_id === coach));   // the coach's own messages were read by Bea
  const fromCoach = (await page(coach, 'coach', coach, bea)).rows.filter(r => r.sender_id === coach);
  assert.ok(fromCoach.every(r => r.delivered_at && r.read_at));
  assert.equal((await db.query(`select 1 from user_notifications where user_id = $1 and read_at is null`, [bea])).rowCount, 0);
  assert.match(await code(run(alex, `select mark_chat_read('coach', $1, $2)`, [coach, bea])), /42501/);
});
await test('delivered is set when the list is loaded, read only when the chat is opened', async () => {
  await say(coach, 'coach', coach, bea, 'one more');
  await run(bea, `select mark_chats_delivered()`);
  const m = (await page(coach, 'coach', coach, bea, null, 1)).rows[0];
  assert.equal(m.body, 'one more'); assert.ok(m.delivered_at); assert.equal(m.read_at, null);
});

// front desk
await test('any member, even a guest, can write to the front desk; staff are alerted; the thread appears in both lists', async () => {
  await db.query(`delete from user_notifications where user_id = $1`, [desk]);
  const id = (await run(alex, `select send_support_message($1, $2)`, [alex, '  my payment is stuck  '])).rows[0].send_support_message;
  assert.ok(id);
  const mine = (await threads(alex)).find(r => r.kind === 'support');
  assert.equal(mine.with_name, 'Front desk'); assert.equal(mine.last_body, 'my payment is stuck'); assert.equal(mine.last_from_me, true); assert.equal(mine.can_send, true);
  const inbox = (await threads(desk)).find(r => r.kind === 'support' && r.member_id === alex);
  assert.equal(inbox.with_name, 'Alex'); assert.equal(inbox.unread, 1); assert.equal(inbox.status, 'open'); assert.equal(inbox.claimed_by, null);
  assert.equal((await db.query(`select 1 from user_notifications where user_id = $1 and type = 'message'`, [desk])).rowCount, 1);
});
await test('a member sees one front-desk thread even with no messages; coaches and members cannot read or write other threads', async () => {
  assert.equal((await threads(bea)).filter(r => r.kind === 'support').length, 1);
  assert.equal((await threads(coach)).filter(r => r.kind === 'support').length, 0);
  assert.match(await code(run(bea, `select send_support_message($1, 'hi')`, [alex])), /42501/);
  assert.match(await code(run(coach, `select send_support_message($1, 'hi')`, [alex])), /42501/);
  assert.match(await code(page(bea, 'support', null, alex)), /42501/);
  assert.equal((await page(alex, 'support', null, alex)).rowCount, 1);
});
await test('empty and over-long messages are refused', async () => {
  assert.match(await code(run(alex, `select send_support_message($1, '   ')`, [alex])), /22023:empty_message/);
  assert.match(await code(run(alex, `select send_support_message($1, $2)`, [alex, 'x'.repeat(2001)])), /22023:message_too_long/);
});
await test('staff reply: the member is alerted and sees "Front desk"; staff see who answered; reading marks it read', async () => {
  await db.query(`delete from user_notifications where user_id = $1`, [alex]);
  await run(desk, `select send_support_message($1, 'looking into it')`, [alex]);
  assert.equal((await db.query(`select 1 from user_notifications where user_id = $1 and type = 'message'`, [alex])).rowCount, 1);
  const asMember = (await page(alex, 'support', null, alex)).rows;
  assert.equal(asMember[0].body, 'looking into it'); assert.equal(asMember[0].sender_name, null);
  const asStaff = (await page(desk, 'support', null, alex)).rows;
  const deskName = (await db.query(`select full_name from profiles where id = $1`, [desk])).rows[0].full_name;
  assert.equal(asStaff[0].sender_name, deskName);
  assert.equal((await threads(alex)).find(r => r.kind === 'support').unread, 1);
  await run(alex, `select mark_chat_read('support', null, $1)`, [alex]);
  assert.equal((await threads(alex)).find(r => r.kind === 'support').unread, 0);
  const seen = (await page(desk, 'support', null, alex)).rows.find(r => r.body === 'looking into it');
  assert.ok(seen.read_at);
});
await test('taking a conversation: one person at a time, release by that person or an admin; only the one who took it is alerted', async () => {
  await run(desk, `select claim_support_thread($1, true)`, [alex]);
  assert.equal((await threads(desk)).find(r => r.kind === 'support' && r.member_id === alex).claimed_by, desk);
  assert.match(await code(run(admin, `select claim_support_thread($1, true)`, [alex])), /P0002/);   // already taken
  assert.match(await code(run(alex, `select claim_support_thread($1, true)`, [alex])), /42501/);
  await db.query(`delete from user_notifications where user_id in ($1, $2)`, [desk, admin]);
  await run(alex, `select send_support_message($1, 'any news?')`, [alex]);
  assert.equal((await db.query(`select 1 from user_notifications where user_id = $1 and type = 'message'`, [desk])).rowCount, 1);
  assert.equal((await db.query(`select 1 from user_notifications where user_id = $1 and type = 'message'`, [admin])).rowCount, 0);
  await run(admin, `select claim_support_thread($1, false)`, [alex]);   // an admin may release
  assert.equal((await threads(desk)).find(r => r.kind === 'support' && r.member_id === alex).claimed_by, null);
});
await test('closing and reopening: staff can; a new message from the member reopens it', async () => {
  await run(desk, `select set_support_status($1, false)`, [alex]);
  assert.equal((await threads(desk)).find(r => r.kind === 'support' && r.member_id === alex).status, 'closed');
  assert.match(await code(run(alex, `select set_support_status($1, true)`, [alex])), /42501/);
  await run(alex, `select send_support_message($1, 'sorry, one more thing')`, [alex]);
  assert.equal((await threads(desk)).find(r => r.kind === 'support' && r.member_id === alex).status, 'open');
});
await test('search finds a person by name and a conversation by message text, only among your own, and treats % and _ as plain characters', async () => {
  const byName = (await run(coach, `select * from chat_search('bea')`)).rows;
  assert.ok(byName.some(r => r.kind === 'coach' && r.member_id === bea && r.with_name === 'Bea'));
  const byText = (await run(bea, `select * from chat_search('chat 77')`)).rows;
  assert.equal(byText.length, 1); assert.match(byText[0].snippet, /coach chat 77/);
  assert.equal((await run(alex, `select * from chat_search('chat 77')`)).rowCount, 0);   // not Alex's conversation
  assert.equal((await run(bea, `select * from chat_search('c')`)).rowCount, 0);          // under two characters
  assert.equal((await run(bea, `select * from chat_search('%')`)).rowCount, 0);
  assert.equal((await run(desk, `select * from chat_search('payment is stuck')`)).rowCount, 1);   // staff find front-desk text
});
await test('support permission exists for admin and front desk only', async () => {
  const p = await db.query(`select r.name from role_permissions rp join roles r using (role_id) join permissions p using (permission_id) where p.resource = 'support' and p.action = 'reply' order by r.name`);
  assert.deepEqual(p.rows.map(r => r.name), ['admin', 'employee']);
  assert.equal((await db.query(`select 1 from messages where false`)).rowCount, 0);
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `npm --prefix supabase/tests/local test`
Expected: the new tests FAIL (`function chat_threads() does not exist`, ...); the earlier 76 still pass.

- [ ] **Step 3: Write the migration**

Create `supabase/migrations/0015_messenger.sql`:

```sql
-- 0015: messenger (role redesign, phase 4a)
--
-- Delivery tracking on coach chats, a front-desk conversation per member, and functions that do the reading on the server:
-- a summary of your conversations, one page of messages, mark delivered and read, and search. The app never downloads a whole
-- table of messages any more.

alter table public.messages add column delivered_at timestamptz;

-- ---------------------------------------------------------------------
-- Front desk conversations: one thread per member
-- ---------------------------------------------------------------------

create table public.support_threads (
	member_id       uuid        primary key references public.members (user_id) on delete cascade,
	claimed_by      uuid        references public.profiles (id),
	status          text        not null default 'open' check (status in ('open', 'closed')),
	last_message_at timestamptz not null default now(),
	created_at      timestamptz not null default now()
);

create table public.support_messages (
	message_id   bigint      generated always as identity primary key,
	member_id    uuid        not null references public.members (user_id) on delete cascade,
	sender_id    uuid        not null references public.profiles (id),
	body         text        not null check (char_length(btrim(body)) > 0 and char_length(body) <= 2000),
	sent_at      timestamptz not null default now(),
	delivered_at timestamptz,
	read_at      timestamptz
);

create index support_messages_thread_idx on public.support_messages (member_id, message_id desc);

alter table public.support_threads  enable row level security;
alter table public.support_messages enable row level security;
revoke all on public.support_threads, public.support_messages from anon, authenticated;
grant select on public.support_threads, public.support_messages to authenticated;

create policy support_threads_select on public.support_threads for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.has_permission('support:reply')));
create policy support_messages_select on public.support_messages for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.has_permission('support:reply')));

insert into public.permissions (resource, action, description) values
	('support', 'reply', 'Answer members'' messages to the front desk');

insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p on p.resource = 'support' and p.action = 'reply'
where r.name in ('admin', 'employee');

-- ---------------------------------------------------------------------
-- Reading
-- ---------------------------------------------------------------------

-- Your conversations, newest first: coach chats you are part of (for every hire, current or ended, and for anyone already written to),
-- your own front-desk thread if you are a member, and the whole inbox if you may answer members.
create function public.chat_threads()
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, with_avatar text, last_body text, last_at timestamptz,
               last_from_me boolean, unread integer, can_send boolean, status text, claimed_by uuid, claimed_by_name text)
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
		select 'coach'::text, p.coach_id, p.member_id,
		       case when p.coach_id = v_uid then p.member_id else p.coach_id end,
		       pr.full_name, pr.avatar_path,
		       lm.body, lm.sent_at, coalesce(lm.sender_id = v_uid, false),
		       (select count(*)::integer from public.messages x
		         where x.coach_id = p.coach_id and x.member_id = p.member_id and x.sender_id <> v_uid and x.read_at is null),
		       (public.coaches_member(p.coach_id, p.member_id) and public.is_active_member(p.member_id)),
		       null::text, null::uuid, null::text
		from (
			select h.coach_id, h.member_id from public.coach_hires h where h.coach_id = v_uid or h.member_id = v_uid
			union
			select m.coach_id, m.member_id from public.messages m where m.coach_id = v_uid or m.member_id = v_uid
		) p
		join public.profiles pr on pr.id = case when p.coach_id = v_uid then p.member_id else p.coach_id end
		left join lateral (select x.body, x.sent_at, x.sender_id from public.messages x
		                   where x.coach_id = p.coach_id and x.member_id = p.member_id order by x.message_id desc limit 1) lm on true

		union all

		-- a member's own front-desk thread (always there, even with no messages yet)
		select 'support'::text, null::uuid, mb.user_id, null::uuid, 'Front desk'::text, null::text,
		       lm.body, lm.sent_at, coalesce(lm.sender_id = v_uid, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = mb.user_id and s.sender_id <> v_uid and s.read_at is null),
		       true, coalesce(t.status, 'open'), null::uuid, null::text
		from public.members mb
		left join public.support_threads t on t.member_id = mb.user_id
		left join lateral (select s.body, s.sent_at, s.sender_id from public.support_messages s where s.member_id = mb.user_id order by s.message_id desc limit 1) lm on true
		where mb.user_id = v_uid

		union all

		-- the inbox, for staff who may answer
		select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
		       lm.body, lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.sender_id = t.member_id and s.read_at is null),
		       true, t.status, t.claimed_by, cb.full_name
		from public.support_threads t
		join public.profiles pr on pr.id = t.member_id
		left join public.profiles cb on cb.id = t.claimed_by
		left join lateral (select s.body, s.sent_at, s.sender_id from public.support_messages s where s.member_id = t.member_id order by s.message_id desc limit 1) lm on true
		where public.has_permission('support:reply') and t.member_id <> v_uid
	) q
	order by q.last_at desc nulls last;
end;
$$;

-- One page of a conversation, newest first. p_before is the id of the oldest message already shown.
create function public.chat_messages(p_kind text, p_coach uuid, p_member uuid, p_before bigint default null, p_limit integer default 50)
returns table (message_id bigint, sender_id uuid, body text, sent_at timestamptz, delivered_at timestamptz, read_at timestamptz, sender_name text)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v_uid uuid := auth.uid();
	v_limit integer := least(greatest(coalesce(p_limit, 50), 1), 100);
begin
	perform public.require_active_user();

	if p_kind = 'coach' then
		if v_uid is distinct from p_coach and v_uid is distinct from p_member then perform public.fail('forbidden', '42501'); end if;
		return query
			select m.message_id, m.sender_id, m.body, m.sent_at, m.delivered_at, m.read_at, null::text
			from public.messages m
			where m.coach_id = p_coach and m.member_id = p_member and (p_before is null or m.message_id < p_before)
			order by m.message_id desc limit v_limit;
	elsif p_kind = 'support' then
		if v_uid is distinct from p_member and not public.has_permission('support:reply') then perform public.fail('forbidden', '42501'); end if;
		return query
			select s.message_id, s.sender_id, s.body, s.sent_at, s.delivered_at, s.read_at,
			       -- a member never learns which staff member answered; staff do
			       case when v_uid = p_member or s.sender_id = p_member then null else (select pr.full_name from public.profiles pr where pr.id = s.sender_id) end
			from public.support_messages s
			where s.member_id = p_member and (p_before is null or s.message_id < p_before)
			order by s.message_id desc limit v_limit;
	else
		perform public.fail('bad_input', '22023');
	end if;
end;
$$;

-- ---------------------------------------------------------------------
-- Status: delivered and read
-- ---------------------------------------------------------------------

-- You opened a conversation: what you received there is delivered and read, and its alert in the bell is cleared.
create function public.mark_chat_read(p_kind text, p_coach uuid, p_member uuid) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
begin
	perform public.require_active_user();

	if p_kind = 'coach' then
		if v_uid is distinct from p_coach and v_uid is distinct from p_member then perform public.fail('forbidden', '42501'); end if;
		update public.messages
		set delivered_at = coalesce(delivered_at, now()), read_at = coalesce(read_at, now())
		where coach_id = p_coach and member_id = p_member and sender_id <> v_uid and read_at is null;
		update public.user_notifications set read_at = now()
		where user_id = v_uid and type = 'message' and read_at is null
		  and reference @> jsonb_build_object('coach_id', p_coach::text, 'member_id', p_member::text);
	elsif p_kind = 'support' then
		if v_uid is distinct from p_member and not public.has_permission('support:reply') then perform public.fail('forbidden', '42501'); end if;
		update public.support_messages
		set delivered_at = coalesce(delivered_at, now()), read_at = coalesce(read_at, now())
		where member_id = p_member and read_at is null
		  and ((v_uid = p_member and sender_id <> p_member) or (v_uid <> p_member and sender_id = p_member));
		update public.user_notifications set read_at = now()
		where user_id = v_uid and type = 'message' and read_at is null
		  and reference @> jsonb_build_object('support_member', p_member::text);
	else
		perform public.fail('bad_input', '22023');
	end if;
end;
$$;

-- Your conversation list was loaded: everything addressed to you is at least delivered.
create function public.mark_chats_delivered() returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
begin
	perform public.require_active_user();

	update public.messages set delivered_at = now()
	where (coach_id = v_uid or member_id = v_uid) and sender_id <> v_uid and delivered_at is null;

	update public.support_messages set delivered_at = now()
	where delivered_at is null and sender_id <> v_uid
	  and (member_id = v_uid or (public.has_permission('support:reply') and sender_id = member_id));
end;
$$;

-- ---------------------------------------------------------------------
-- Front desk conversation
-- ---------------------------------------------------------------------

-- A member writes to the front desk (p_member is themselves), or staff answer a member.
create function public.send_support_message(p_member uuid, p_body text) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_body text := btrim(p_body);
	v_id bigint;
	v_claimed uuid;
	v_name text;
begin
	perform public.require_active_user();
	if v_body is null or v_body = '' then perform public.fail('empty_message', '22023'); end if;
	if char_length(v_body) > 2000 then perform public.fail('message_too_long', '22023'); end if;

	if v_uid = p_member then
		if not exists (select 1 from public.members where user_id = v_uid) then perform public.fail('forbidden', '42501'); end if;
	elsif public.has_permission('support:reply') then
		if not exists (select 1 from public.members where user_id = p_member) then perform public.fail('not_found', '22023'); end if;
	else
		perform public.fail('forbidden', '42501');
	end if;

	insert into public.support_threads (member_id) values (p_member) on conflict (member_id) do nothing;
	insert into public.support_messages (member_id, sender_id, body) values (p_member, v_uid, v_body) returning message_id into v_id;

	if v_uid = p_member then
		update public.support_threads set last_message_at = now(), status = 'open' where member_id = p_member returning claimed_by into v_claimed;
		select full_name into v_name from public.profiles where id = v_uid;
		if v_claimed is not null and exists (select 1 from public.profiles where id = v_claimed and is_active) then
			perform public.notify(v_claimed, 'message', 'New message from ' || v_name, left(v_body, 80), jsonb_build_object('support_member', p_member::text));
		else
			perform public.notify_staff('message', 'New message from ' || v_name, left(v_body, 80), jsonb_build_object('support_member', p_member::text));
		end if;
	else
		update public.support_threads set last_message_at = now() where member_id = p_member;
		perform public.notify(p_member, 'message', 'Message from the front desk', left(v_body, 80), jsonb_build_object('support_member', p_member::text));
	end if;

	return v_id;
end;
$$;

-- Take or release a conversation. Taking is for staff when nobody has it; releasing is for whoever took it, or an admin.
create function public.claim_support_thread(p_member uuid, p_claim boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v public.support_threads;
begin
	perform public.require_permission('support:reply');
	select * into v from public.support_threads where member_id = p_member for update;
	if not found then perform public.fail('not_found', '22023'); end if;

	if p_claim then
		if v.claimed_by is not null and v.claimed_by <> auth.uid() then perform public.fail('already_processed', 'P0002'); end if;
		update public.support_threads set claimed_by = auth.uid() where member_id = p_member;
	else
		if v.claimed_by is not null and v.claimed_by <> auth.uid() and not public.has_role('admin') then perform public.fail('forbidden', '42501'); end if;
		update public.support_threads set claimed_by = null where member_id = p_member;
	end if;
end;
$$;

create function public.set_support_status(p_member uuid, p_open boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_permission('support:reply');
	update public.support_threads set status = case when p_open then 'open' else 'closed' end where member_id = p_member;
	if not found then perform public.fail('not_found', '22023'); end if;
end;
$$;

-- ---------------------------------------------------------------------
-- Search: the other person's name, or any message text, among your own conversations only
-- ---------------------------------------------------------------------

create function public.chat_search(p_query text)
returns table (kind text, coach_id uuid, member_id uuid, with_id uuid, with_name text, snippet text, sent_at timestamptz)
language plpgsql stable security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v_q text := btrim(coalesce(p_query, ''));
	v_like text;
begin
	perform public.require_active_user();
	if char_length(v_q) < 2 then return; end if;
	-- % and _ typed by the user are plain characters
	v_like := '%' || replace(replace(replace(v_q, '\', '\\'), '%', '\%'), '_', '\_') || '%';

	return query
	select * from (
		select distinct on (t.kind, t.coach_id, t.member_id)
		       t.kind, t.coach_id, t.member_id, t.with_id, t.with_name,
		       coalesce(hit.body, t.last_body) as snippet, coalesce(hit.sent_at, t.last_at) as sent_at
		from public.chat_threads() t
		left join lateral (
			select x.body, x.sent_at from (
				select m.body, m.sent_at from public.messages m
				where t.kind = 'coach' and m.coach_id = t.coach_id and m.member_id = t.member_id and m.body ilike v_like escape '\'
				union all
				select s.body, s.sent_at from public.support_messages s
				where t.kind = 'support' and s.member_id = t.member_id and s.body ilike v_like escape '\'
			) x order by x.sent_at desc limit 1
		) hit on true
		where hit.body is not null or t.with_name ilike v_like escape '\'
		order by t.kind, t.coach_id, t.member_id
	) r
	order by r.sent_at desc nulls last
	limit 30;
end;
$$;

revoke all on function public.chat_threads(), public.chat_messages(text, uuid, uuid, bigint, integer),
	public.mark_chat_read(text, uuid, uuid), public.mark_chats_delivered(), public.send_support_message(uuid, text),
	public.claim_support_thread(uuid, boolean), public.set_support_status(uuid, boolean), public.chat_search(text)
	from public, anon, authenticated;
grant execute on function public.chat_threads(), public.chat_messages(text, uuid, uuid, bigint, integer),
	public.mark_chat_read(text, uuid, uuid), public.mark_chats_delivered(), public.send_support_message(uuid, text),
	public.claim_support_thread(uuid, boolean), public.set_support_status(uuid, boolean), public.chat_search(text)
	to authenticated;
```

- [ ] **Step 4: Run to verify they pass**

Run: `npm --prefix supabase/tests/local test`
Expected: `ok   0015_messenger.sql` during setup, then every test passes (76 earlier plus the 12 new). If a test fails, read the failure before touching the migration. Likely suspects: an earlier rule rejecting the set-up rows (one active hire per member); the `\` escape characters inside the SQL string (the file uses `standard_conforming_strings`, so `'\'` is a single backslash); the order of columns in the three-way `union all` (types must line up, including the `null::text` and `null::uuid` placeholders); the `reference @>` match, whose values are stored as text in `notify_new_message` (`jsonb_build_object('coach_id', new.coach_id, ...)` stores uuids as JSON strings, which is what `p_coach::text` matches).

- [ ] **Step 5: Commit**

```bash
git add supabase/migrations/0015_messenger.sql supabase/tests/local/rules.mjs
git commit -m "feat(db): messenger - delivery status, front desk conversations, paged reads and search (0015)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Models, `ChatService` and the demo gateway

**Files:**
- Modify: `src/GymMembership.Core/Models/Models.cs` (`ChatMessage` gains `DeliveredAt`; new `ChatThread`, `ChatLine`, `ChatSearchHit`)
- Modify: `src/GymMembership.Core/Services/Features.cs` (replace the `IMessageService` block with `IChatService` and `ChatService`), `src/GymMembership.Core/Services/Foundation.cs` (error text), `src/GymMembership.Core/Services/Services.cs` (`INotificationService.Refresh`)
- Modify: `src/GymMembership.Core/Demo/DemoGateway.cs`
- Modify: `src/GymMembership.App/MauiProgram.cs` (registration), `src/GymMembership.Core/ViewModels/MemberViewModels.cs` and `StaffViewModels.cs` (the two `messages.Pending` users)
- Create: `tests/GymMembership.Tests/ChatServiceTests.cs`
- Modify: `tests/GymMembership.Tests/NewFeatureTests.cs`, `PaymentTests.cs`, `SupabaseGatewayTests.cs` (the four old `MessageService` uses)

**Interfaces:**
- Produces:
  - `ChatThread : BaseModel` (JSON columns of `chat_threads`) with `Key`, `IsSupport`, `HasUnread`, `UnreadText`, `Preview`, `ClaimedByMe(string uid)`.
  - `ChatLine : BaseModel` (columns of `chat_messages`).
  - `record ChatBubble(ChatLine Line, bool Mine)` with `Status` (`"Sent"`, `"Delivered"`, `"Read"`; empty for received messages).
  - `ChatSearchHit : BaseModel`.
  - `INotificationService.Refresh()` raises `Changed` (so the bell updates after a chat is read).
  - `IChatService`: `(string CoachId, string? MemberId)? Pending {get;set;}`, `Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync()`, `Task<AppResult<ChatPage>> MessagesAsync(ChatThread t, long? beforeId = null, int limit = 50)`, `Task<AppResult<bool>> SendAsync(ChatThread t, string body)`, `Task<AppResult<bool>> MarkReadAsync(ChatThread t)`, `Task<AppResult<bool>> MarkDeliveredAsync()`, `Task<AppResult<bool>> ClaimAsync(ChatThread t, bool claim)`, `Task<AppResult<bool>> SetOpenAsync(ChatThread t, bool open)`, `Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query)`; and `record ChatPage(IReadOnlyList<ChatBubble> Bubbles, bool HasMore)` (bubbles oldest first).

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/ChatServiceTests.cs`. The demo accounts: `member@demo.io` (Alex Tan, hired by Lena Cruz, with three seeded messages), `bea@demo.io`, `coach@demo.io` (Lena), `employee@demo.io` (Marco Santos), `admin@demo.io`. The tests below describe the behavior; Step 5 makes the demo satisfy them.

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class ChatServiceTests
{
    static async Task<(DemoGateway Gw, ChatService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new ChatService(gw, new NotificationService(gw)));
    }

    [Fact]
    public async Task A_member_sees_the_coach_chat_with_its_last_message_and_a_front_desk_thread()
    {
        var (_, svc) = await As("member@demo.io");
        var threads = (await svc.ThreadsAsync()).Value!;

        var coach = threads.Single(t => !t.IsSupport);
        Assert.Equal("Lena Cruz", coach.WithName);
        Assert.Equal("Perfect, see you on the session.", coach.LastBody);
        Assert.True(coach.LastFromMe);
        Assert.True(coach.CanSend);
        var desk = threads.Single(t => t.IsSupport);
        Assert.Equal("Front desk", desk.WithName);
        Assert.Null(desk.LastBody);   // nothing written yet, but the thread is always there
    }

    [Fact]
    public async Task Messages_come_back_oldest_first_with_a_status_on_the_ones_you_sent()
    {
        var (_, svc) = await As("member@demo.io");
        var coach = (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport);

        var page = (await svc.MessagesAsync(coach)).Value!;

        Assert.False(page.HasMore);
        Assert.Equal(page.Bubbles.OrderBy(b => b.Line.SentAt).Select(b => b.Line.MessageId), page.Bubbles.Select(b => b.Line.MessageId));
        var mine = page.Bubbles.First(b => b.Mine);
        Assert.Contains(mine.Status, new[] { "Sent", "Delivered", "Read" });
        Assert.Equal("", page.Bubbles.First(b => !b.Mine).Status);   // status is shown on what you sent, not what you received
    }

    [Fact]
    public async Task A_long_conversation_loads_a_page_at_a_time_without_duplicates()
    {
        var (gw, svc) = await As("member@demo.io");
        var coach = (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport);
        for (var i = 0; i < 120; i++) await svc.SendAsync(coach, $"message {i}");

        var first = (await svc.MessagesAsync(coach)).Value!;
        Assert.Equal(50, first.Bubbles.Count);
        Assert.True(first.HasMore);
        Assert.Equal("message 119", first.Bubbles[^1].Line.Body);   // newest at the bottom

        var second = (await svc.MessagesAsync(coach, first.Bubbles[0].Line.MessageId)).Value!;
        Assert.Equal(50, second.Bubbles.Count);
        Assert.True(second.HasMore);
        var third = (await svc.MessagesAsync(coach, second.Bubbles[0].Line.MessageId)).Value!;
        Assert.False(third.HasMore);   // 3 seeded + 120 sent = 123; the last page is the 23 oldest
        Assert.Equal(123, first.Bubbles.Count + second.Bubbles.Count + third.Bubbles.Count);
        Assert.Equal(123, first.Bubbles.Concat(second.Bubbles).Concat(third.Bubbles).Select(b => b.Line.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Sending_to_a_coach_needs_text_and_an_active_hire_and_never_adds_a_phantom_message_when_it_fails()
    {
        var (gw, svc) = await As("member@demo.io");
        var coach = (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport);
        Assert.Equal("empty_message", (await svc.SendAsync(coach, "   ")).Message);
        Assert.Equal("message_too_long", (await svc.SendAsync(coach, new string('x', 2001))).Message);

        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await svc.SendAsync(coach, "hello")).Error);
        gw.FailWith = null;
        Assert.DoesNotContain((await svc.MessagesAsync(coach)).Value!.Bubbles, b => b.Line.Body == "hello");
    }

    [Fact]
    public async Task Reading_a_conversation_shows_Read_to_the_sender_and_clears_the_unread_badge()
    {
        var (gw, alex) = await As("member@demo.io");
        var toCoach = (await alex.ThreadsAsync()).Value!.Single(t => !t.IsSupport);
        await alex.SendAsync(toCoach, "see you at 9");

        var (_, coach) = await As("coach@demo.io", gw);
        var theirs = (await coach.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan");
        Assert.True(theirs.Unread >= 1);
        await coach.MarkReadAsync(theirs);
        Assert.Equal(0, (await coach.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan").Unread);

        var (_, alexAgain) = await As("member@demo.io", gw);
        var sent = (await alexAgain.MessagesAsync((await alexAgain.ThreadsAsync()).Value!.Single(t => !t.IsSupport))).Value!.Bubbles.Last(b => b.Mine);
        Assert.Equal("Read", sent.Status);
    }

    [Fact]
    public async Task Loading_the_list_marks_what_you_received_as_delivered_but_not_read()
    {
        var (gw, coach) = await As("coach@demo.io");
        var toAlex = (await coach.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan");
        await coach.SendAsync(toAlex, "bring shoes");

        var (_, alex) = await As("member@demo.io", gw);
        await alex.MarkDeliveredAsync();

        var (_, coachAgain) = await As("coach@demo.io", gw);
        var line = (await coachAgain.MessagesAsync(toAlex)).Value!.Bubbles.Last(b => b.Mine);
        Assert.Equal("Delivered", line.Status);
    }

    [Fact]
    public async Task A_member_writes_to_the_front_desk_staff_see_it_in_the_inbox_and_answer_and_the_member_never_learns_who()
    {
        var (gw, bea) = await As("bea@demo.io");
        var deskThread = (await bea.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.True((await bea.SendAsync(deskThread, "my payment is stuck")).Ok);

        var (_, staff) = await As("employee@demo.io", gw);
        var inbox = (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport && t.WithName == "Bea Lim");
        Assert.Equal(1, inbox.Unread);
        Assert.Equal("open", inbox.Status);
        Assert.True((await staff.SendAsync(inbox, "checking now")).Ok);
        var asStaff = (await staff.MessagesAsync(inbox)).Value!.Bubbles;
        Assert.Equal("Marco Santos", asStaff.Last().Line.SenderName);   // staff see who answered

        var (_, beaAgain) = await As("bea@demo.io", gw);
        var asMember = (await beaAgain.MessagesAsync((await beaAgain.ThreadsAsync()).Value!.Single(t => t.IsSupport))).Value!.Bubbles;
        Assert.Equal("checking now", asMember.Last().Line.Body);
        Assert.Null(asMember.Last().Line.SenderName);                    // a member sees "Front desk" only
    }

    [Fact]
    public async Task Staff_take_close_and_reopen_a_conversation_and_a_member_cannot()
    {
        var (gw, bea) = await As("bea@demo.io");
        await bea.SendAsync((await bea.ThreadsAsync()).Value!.Single(t => t.IsSupport), "hello");
        var (_, staff) = await As("employee@demo.io", gw);
        var inbox = (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport);

        Assert.True((await staff.ClaimAsync(inbox, true)).Ok);
        var taken = (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.Equal("Marco Santos", taken.ClaimedByName);

        var (_, admin) = await As("admin@demo.io", gw);
        Assert.Equal(AppErrorKind.Conflict, (await admin.ClaimAsync(inbox, true)).Error);   // already taken
        Assert.True((await admin.ClaimAsync(inbox, false)).Ok);                                // an admin may release

        Assert.True((await staff.SetOpenAsync(inbox, false)).Ok);
        Assert.Equal("closed", (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport).Status);

        var (_, member) = await As("bea@demo.io", gw);
        var mine = (await member.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.Equal(AppErrorKind.Unauthorized, (await member.SetOpenAsync(mine, false)).Error);
    }

    [Fact]
    public async Task Search_finds_people_and_message_text_among_your_own_conversations_only()
    {
        var (_, coach) = await As("coach@demo.io");
        Assert.Contains((await coach.SearchAsync("alex")).Value!, h => h.WithName == "Alex Tan");
        var byText = (await coach.SearchAsync("flat-soled")).Value!;
        Assert.Single(byText);
        Assert.Contains("flat-soled", byText[0].Snippet);
        Assert.Empty((await coach.SearchAsync("a")).Value!);        // under two characters
        Assert.Empty((await coach.SearchAsync("%")).Value!);

        var (_, bea) = await As("bea@demo.io");
        Assert.Empty((await bea.SearchAsync("flat-soled")).Value!); // not Bea's conversation
    }

    [Fact]
    public async Task Opening_from_Coaches_or_Trainees_selects_that_conversation_first()
    {
        var (_, svc) = await As("member@demo.io");
        svc.Pending = ("u-coach1", null);   // "me, as the member"
        Assert.Equal("u-coach1", svc.Pending!.Value.CoachId);
    }
}
```

In `SupabaseGatewayTests.cs` inside the class add:

```csharp
    [Fact]
    public async Task The_conversation_list_and_a_page_of_messages_are_read_through_functions_and_parse()
    {
        var gw = await SignedInAsync();
        var svc = new ChatService(gw, new NotificationService(gw));

        _reply = _ => (200, """[{"kind":"coach","coach_id":"c-1","member_id":"u-1","with_id":"c-1","with_name":"Lena Cruz","with_avatar":null,"last_body":"hi","last_at":"2026-10-15T01:00:00+00:00","last_from_me":false,"unread":2,"can_send":true,"status":null,"claimed_by":null,"claimed_by_name":null}]""");
        var thread = (await svc.ThreadsAsync()).Value!.Single();
        Assert.Equal("/rest/v1/rpc/chat_threads", Last.Path);
        Assert.Equal(("Lena Cruz", 2, "coach|c-1|u-1"), (thread.WithName, thread.Unread, thread.Key));

        _reply = _ => (200, """[{"message_id":7,"sender_id":"c-1","body":"hello","sent_at":"2026-10-15T01:00:00+00:00","delivered_at":null,"read_at":null,"sender_name":null}]""");
        var page = (await svc.MessagesAsync(thread, 99, 20)).Value!;
        Assert.Equal("/rest/v1/rpc/chat_messages", Last.Path);
        Assert.Contains("\"p_kind\":\"coach\"", Last.Body);
        Assert.Contains("\"p_before\":99", Last.Body);
        Assert.Contains("\"p_limit\":20", Last.Body);
        Assert.Equal("hello", page.Bubbles.Single().Line.Body);
    }

    [Fact]
    public async Task Sending_to_a_coach_inserts_a_message_and_sending_to_the_front_desk_calls_the_function()
    {
        var gw = await SignedInAsync();
        var svc = new ChatService(gw, new NotificationService(gw));
        _reply = _ => (201, "[]");
        await svc.SendAsync(new ChatThread { Kind = "coach", CoachId = "c-1", MemberId = "u-1" }, "  hello ");
        Assert.Equal("/rest/v1/messages", Last.Path);
        Assert.Contains("\"body\":\"hello\"", Last.Body);

        _reply = _ => (200, "42");
        await svc.SendAsync(new ChatThread { Kind = "support", MemberId = "u-1" }, "help");
        Assert.Equal("/rest/v1/rpc/send_support_message", Last.Path);
        Assert.Contains("\"p_member\":\"u-1\"", Last.Body);
    }
```

Replace the four old `MessageService` uses: `NewFeatureTests.cs` lines near 122 and 138 (open the file; they test sending to a hired coach and that an ended hire refuses new messages while history stays), `PaymentTests.cs` near line 203 (a guest cannot message a coach: `Unauthorized`), `SupabaseGatewayTests.cs` near line 421 (the `Note("messages", ...)` smoke line). Port each to `ChatService`: find the coach thread with `ThreadsAsync`, then `SendAsync(thread, "...")` / `MessagesAsync(thread)`, keeping every assertion's meaning. A guest sending to a coach must still return `AppErrorKind.Unauthorized`; an ended hire must still refuse new messages while `MessagesAsync` still returns the history.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter "ChatServiceTests|conversation_list|Sending_to_a_coach_inserts"`
Expected: build FAILS (`ChatService`, `ChatThread`, ... do not exist).

- [ ] **Step 3: Models**

In `src/GymMembership.Core/Models/Models.cs`, in `ChatMessage` add the column after `ReadAt`:

```csharp
  [Column("delivered_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? DeliveredAt { get; set; }
```

and append:

```csharp
// one row of chat_threads(): a coach chat ("coach") or a front-desk thread ("support"). A function result, never written.
public class ChatThread : BaseModel
{
  [Column("kind")] public string Kind { get; set; } = "coach";
  [Column("coach_id")] public string? CoachId { get; set; }
  [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("with_id")] public string? WithId { get; set; }
  [Column("with_name")] public string WithName { get; set; } = "";
  [Column("with_avatar")] public string? WithAvatar { get; set; }
  [Column("last_body")] public string? LastBody { get; set; }
  [Column("last_at")] public DateTime? LastAt { get; set; }
  [Column("last_from_me")] public bool LastFromMe { get; set; }
  [Column("unread")] public int Unread { get; set; }
  [Column("can_send")] public bool CanSend { get; set; }
  [Column("status")] public string? Status { get; set; }
  [Column("claimed_by")] public string? ClaimedBy { get; set; }
  [Column("claimed_by_name")] public string? ClaimedByName { get; set; }

  public string Key => $"{Kind}|{CoachId}|{MemberId}";
  public bool IsSupport => Kind == "support";
  public bool HasUnread => Unread > 0;
  public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
  public string Preview => LastBody is null ? "No messages yet" : LastFromMe ? "You: " + LastBody : LastBody;
}

// one row of chat_messages(): a message with delivery times, and (front desk, staff view only) who sent it
public class ChatLine : BaseModel
{
  [Column("message_id")] public long MessageId { get; set; }
  [Column("sender_id")] public string SenderId { get; set; } = "";
  [Column("body")] public string Body { get; set; } = "";
  [Column("sent_at")] public DateTime SentAt { get; set; }
  [Column("delivered_at")] public DateTime? DeliveredAt { get; set; }
  [Column("read_at")] public DateTime? ReadAt { get; set; }
  [Column("sender_name")] public string? SenderName { get; set; }
}

public class ChatSearchHit : BaseModel
{
  [Column("kind")] public string Kind { get; set; } = "coach";
  [Column("coach_id")] public string? CoachId { get; set; }
  [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("with_id")] public string? WithId { get; set; }
  [Column("with_name")] public string WithName { get; set; } = "";
  [Column("snippet")] public string? Snippet { get; set; }
  [Column("sent_at")] public DateTime? SentAt { get; set; }
}
```

- [ ] **Step 4: Service**

Add to `INotificationService` (and `NotificationService`) in `Services.cs`: `void Refresh();` with `public void Refresh() => Changed?.Invoke();`.

In `Foundation.cs`: add `"message_too_long"` to the `ErrorMapper` Validation token list and to `ErrorText.For(kind, raw)`: `"message_too_long" => "Keep a message under 2000 characters.",`.

In `src/GymMembership.Core/Services/Features.cs` replace the whole "Messages (member and coach)" block (from the `ThreadRow` record to the end of `MessageService`) with:

```csharp
// ===================== Messenger =====================
public sealed record ChatBubble(ChatLine Line, bool Mine)
{
    /// <summary>Shown under what you sent: Sent, Delivered or Read. Empty for what you received.</summary>
    public string Status => !Mine ? "" : Line.ReadAt is not null ? "Read" : Line.DeliveredAt is not null ? "Delivered" : "Sent";
}

/// <summary>One page of a conversation, oldest first. HasMore says an earlier page exists.</summary>
public sealed record ChatPage(IReadOnlyList<ChatBubble> Bubbles, bool HasMore);

public interface IChatService
{
    /// <summary>Set before navigating to Messages so that conversation opens first. MemberId null means "me, as the member".</summary>
    (string CoachId, string? MemberId)? Pending { get; set; }
    Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync();
    /// <summary>A page of at most 100 messages. beforeId is the id of the oldest message already on screen.</summary>
    Task<AppResult<ChatPage>> MessagesAsync(ChatThread thread, long? beforeId = null, int limit = 50);
    Task<AppResult<bool>> SendAsync(ChatThread thread, string body);
    Task<AppResult<bool>> MarkReadAsync(ChatThread thread);
    Task<AppResult<bool>> MarkDeliveredAsync();
    Task<AppResult<bool>> ClaimAsync(ChatThread thread, bool claim);
    Task<AppResult<bool>> SetOpenAsync(ChatThread thread, bool open);
    Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query);
}

public sealed class ChatService(ISupabaseGateway gw, INotificationService notifications) : IChatService
{
    public (string CoachId, string? MemberId)? Pending { get; set; }

    public Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync() => Safe.RunAsync<IReadOnlyList<ChatThread>>(async () =>
        await gw.RpcAsync<List<ChatThread>>("chat_threads") ?? []);

    public Task<AppResult<ChatPage>> MessagesAsync(ChatThread thread, long? beforeId = null, int limit = 50) => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var take = Math.Clamp(limit, 1, 100);
        var args = new Dictionary<string, object> { ["p_kind"] = thread.Kind, ["p_member"] = thread.MemberId, ["p_limit"] = take + 1 };   // one extra row tells whether an earlier page exists
        if (thread.CoachId is not null) args["p_coach"] = thread.CoachId;
        if (beforeId is { } before) args["p_before"] = before;
        var rows = await gw.RpcAsync<List<ChatLine>>("chat_messages", args) ?? [];
        var hasMore = rows.Count > take;
        var bubbles = rows.Take(take).Reverse().Select(l => new ChatBubble(l, l.SenderId == uid)).ToList();
        return new ChatPage(bubbles, hasMore);
    });

    public async Task<AppResult<bool>> SendAsync(ChatThread thread, string body)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0) return AppResult<bool>.Fail(AppErrorKind.Validation, "empty_message");
        if (text.Length > 2000) return AppResult<bool>.Fail(AppErrorKind.Validation, "message_too_long");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            if (thread.IsSupport) await gw.RpcAsync<long>("send_support_message", new() { ["p_member"] = thread.MemberId, ["p_body"] = text });
            else await gw.InsertAsync(new ChatMessage { CoachId = thread.CoachId!, MemberId = thread.MemberId, SenderId = uid, Body = text });
            return true;
        });
    }

    public Task<AppResult<bool>> MarkReadAsync(ChatThread thread) => Safe.RunAsync(async () =>
    {
        var args = new Dictionary<string, object> { ["p_kind"] = thread.Kind, ["p_member"] = thread.MemberId };
        if (thread.CoachId is not null) args["p_coach"] = thread.CoachId;
        await gw.RpcAsync("mark_chat_read", args);
        notifications.Refresh();   // the alert for this chat is cleared, so the bell can update
        return true;
    });

    public Task<AppResult<bool>> MarkDeliveredAsync() => Safe.RunAsync(async () => { await gw.RpcAsync("mark_chats_delivered"); return true; });

    public Task<AppResult<bool>> ClaimAsync(ChatThread thread, bool claim) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("claim_support_thread", new() { ["p_member"] = thread.MemberId, ["p_claim"] = claim }); return true; });

    public Task<AppResult<bool>> SetOpenAsync(ChatThread thread, bool open) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("set_support_status", new() { ["p_member"] = thread.MemberId, ["p_open"] = open }); return true; });

    public Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query) => Safe.RunAsync<IReadOnlyList<ChatSearchHit>>(async () =>
        string.IsNullOrWhiteSpace(query) ? [] : await gw.RpcAsync<List<ChatSearchHit>>("chat_search", new() { ["p_query"] = query.Trim() }) ?? []);
}
```

`RpcAsync<long>("send_support_message", ...)` needs the real function's `bigint` result; the demo returns a `long`. Rename `IMessageService` to `IChatService` in `MauiProgram.cs` (`s.AddSingleton<IChatService, ChatService>();`) and in the two view models that take it: `CoachesViewModel(ICoachService svc, IChatService messages, IAppNavigator nav)` in `MemberViewModels.cs` and `TraineesViewModel(ICoachService svc, IChatService messages, IAppNavigator nav)` in `StaffViewModels.cs` (their `messages.Pending = ...` lines stay as they are). Delete `MessagesViewModel` from `MemberViewModels.cs` (Task 3 adds its replacement; until then the app project does not build, which is fine: the test project does not reference it).

- [ ] **Step 5: Demo gateway**

In `src/GymMembership.Core/Demo/DemoGateway.cs`:
1. Add models `SupportThread` and `SupportMessage` (`BaseModel` classes with `[Table]`, `[PrimaryKey]` and the column attributes matching `0015`; `SupportThread.MemberId` is the key) at the bottom of `Models.cs`, and add `"support:reply"` to the `["admin"]` and `["employee"]` permission lists.
2. Read filters in `Allowed`: `SupportThread t => t.MemberId == uid || Has("support:reply")`, `SupportMessage m => m.MemberId == uid || Has("support:reply")`.
3. Seed: make the three existing Alex/Lena messages delivered and read where the recipient has read them (set `DeliveredAt`/`ReadAt` on the two Alex-sent ones, so Lena has read them), and leave Lena's reply (the second message) unread by Alex. Do not seed any front-desk thread (the tests above rely on Bea's being empty).
4. In the RPC switch add the eight functions with the same behaviour as `0015` (names, argument keys `p_kind`, `p_coach`, `p_member`, `p_before`, `p_limit`, `p_body`, `p_claim`, `p_open`, `p_query`, same error codes: `forbidden` for not a participant, `empty_message`, `message_too_long`, `already_processed` when claimed by someone else, `not_found`, `bad_input`). Return a `List<ChatThread>` / `List<ChatLine>` / `List<ChatSearchHit>` for the three reads, a `long` for `send_support_message`, `null` otherwise. Mirror `0015`'s rules exactly: a coach chat exists for every hire and every written-to pair; `CanSend` is "active hire and active plan"; the support thread of a member is always listed; the inbox lists every thread to staff with `support:reply`; a member never sees `SenderName`; reading marks delivered and read; `mark_chats_delivered` marks everything addressed to the caller delivered; sending as the member reopens the thread and alerts staff (`NotifyStaff("message", ...)`) or only the person who took it; staff replies alert the member; `chat_search` needs two characters and escapes nothing (the demo uses `Contains` with `StringComparison.OrdinalIgnoreCase`, which already treats `%` as plain text).
5. `ChatMessage` inserts through `InsertAsync` stay as they are (the existing check in `Allowed`/insert for `ChatMessage` already enforces hire and plan).

- [ ] **Step 6: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS. Any failure in `ChatServiceTests` means the demo differs from `0015`: fix the demo, not the test, unless the test contradicts `0015`.

- [ ] **Step 7: Commit**

```bash
git add src/GymMembership.Core src/GymMembership.App/MauiProgram.cs tests/GymMembership.Tests
git commit -m "feat: chat service with paged messages, delivery status, front desk conversations and search

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The Messenger view model

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (add `ThreadItem` and `MessengerViewModel`)
- Create: `tests/GymMembership.Tests/MessengerViewModelTests.cs`

**Interfaces:**
- Consumes: `IChatService` (Task 2), `IProfileService.AvatarUrlAsync`.
- Produces:
  - `ThreadItem : ObservableObject` wrapping a `ChatThread` (`Thread`) with `AvatarUrl`, `Letters`, and pass-through properties.
  - `MessengerViewModel(IChatService chat, IProfileService profile, IAuthService auth)`: `Threads` (the visible list), `Bubbles`, `Selected : ThreadItem?`, `Draft`, `HasTabs`, `Tab` (`"coach"`/`"support"`), `CoachTabBadge`, `StaffTabBadge`, `HasFilters`, `Filter` (`"open"`/`"mine"`/`"closed"`), `SearchText`, `Hits`, `IsSearching`, `ShowList`, `ShowChat`, `IsCompact`, `ShowingChat`, `HasMore`, `CanSend`, `CanTake`, `CanRelease`, `CanClose`, `CanReopen`, `EmptyListText`, commands `OpenCommand(ThreadItem)`, `OpenHitCommand(ChatSearchHit)`, `BackCommand`, `SendCommand`, `LoadEarlierCommand`, `ShowTabCommand(string)`, `ShowFilterCommand(string)`, `TakeCommand`, `ReleaseCommand`, `CloseChatCommand`, `ReopenCommand`; and the quiet `AutoRefreshAsync`.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/MessengerViewModelTests.cs`:

```csharp
using System.Collections.Specialized;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MessengerViewModelTests
{
    static async Task<(DemoGateway Gw, ChatService Chat, MessengerViewModel Vm)> Open(string email, DemoGateway? gw = null, bool compact = false)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw));
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw)) { IsCompact = compact };
        await vm.OnAppearingAsync();
        return (gw, chat, vm);
    }

    [Fact]
    public async Task A_member_opens_on_the_coach_tab_with_the_first_conversation_open_and_marked_read()
    {
        var (_, _, vm) = await Open("member@demo.io");
        Assert.True(vm.HasTabs);
        Assert.Equal("coach", vm.Tab);
        Assert.Equal("Lena Cruz", vm.Selected!.Thread.WithName);
        Assert.NotEmpty(vm.Bubbles);
        Assert.Equal(0, vm.Threads.Single().Thread.Unread);   // opening it read it
    }

    [Fact]
    public async Task The_staff_tab_shows_the_front_desk_thread_and_its_badge_counts_unread()
    {
        var (gw, _, vm) = await Open("bea@demo.io");
        Assert.Empty(vm.Threads);                                   // Bea has no coach: the Coach tab is empty
        Assert.Contains("coach", vm.EmptyListText, StringComparison.OrdinalIgnoreCase);
        await vm.ShowTabCommand.ExecuteAsync("support");
        Assert.Equal("Front desk", vm.Threads.Single().Thread.WithName);

        vm.Draft = "my payment is stuck";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Draft);
        Assert.Equal("my payment is stuck", vm.Bubbles.Last().Line.Body);
        Assert.Equal("Sent", vm.Bubbles.Last().Status);

        var (_, chat, desk) = await Open("employee@demo.io", gw);
        Assert.False(desk.HasTabs);
        Assert.True(desk.HasFilters);
        Assert.Equal("Bea Lim", desk.Threads.Single().Thread.WithName);
    }

    [Fact]
    public async Task Staff_take_close_and_reopen_from_the_chat_header()
    {
        var (gw, _, bea) = await Open("bea@demo.io");
        await bea.ShowTabCommand.ExecuteAsync("support");
        bea.Draft = "hello"; await bea.SendCommand.ExecuteAsync(null);

        var (_, _, desk) = await Open("employee@demo.io", gw);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        Assert.True(desk.CanTake); Assert.False(desk.CanRelease);

        await desk.TakeCommand.ExecuteAsync(null);
        Assert.True(desk.CanRelease); Assert.False(desk.CanTake);
        Assert.Equal("open", desk.Filter);   // the filter does not jump by itself; a taken chat shows under Mine (checked next)
        await desk.ShowFilterCommand.ExecuteAsync("mine");
        Assert.Single(desk.Threads);

        await desk.CloseChatCommand.ExecuteAsync(null);
        await desk.ShowFilterCommand.ExecuteAsync("closed");
        Assert.Single(desk.Threads);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        Assert.True(desk.CanReopen);
    }

    // a chat service whose message pages the test releases by hand, to make two conversations answer out of order
    sealed class SlowChat : IChatService
    {
        public (string CoachId, string? MemberId)? Pending { get; set; }
        public readonly Queue<(ChatThread Thread, TaskCompletionSource<AppResult<ChatPage>> Reply)> Waiting = new();
        public List<ChatThread> Threads = [];
        public Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync() => Task.FromResult(AppResult<IReadOnlyList<ChatThread>>.Success(Threads));
        public Task<AppResult<ChatPage>> MessagesAsync(ChatThread t, long? beforeId = null, int limit = 50)
        { var tcs = new TaskCompletionSource<AppResult<ChatPage>>(); Waiting.Enqueue((t, tcs)); return tcs.Task; }
        public Task<AppResult<bool>> SendAsync(ChatThread t, string body) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> MarkReadAsync(ChatThread t) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> MarkDeliveredAsync() => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> ClaimAsync(ChatThread t, bool claim) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> SetOpenAsync(ChatThread t, bool open) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string q) => Task.FromResult(AppResult<IReadOnlyList<ChatSearchHit>>.Success([]));
    }

    static ChatPage PageOf(string body) => new([new ChatBubble(new ChatLine { MessageId = 1, SenderId = "x", Body = body, SentAt = DateTime.UtcNow }, false)], false);

    [Fact]
    public async Task A_slow_answer_for_the_first_conversation_never_shows_under_the_second()
    {
        var chat = new SlowChat();
        chat.Threads = [new ChatThread { Kind = "coach", CoachId = "c-1", MemberId = "m-1", WithName = "Anna", CanSend = true },
                        new ChatThread { Kind = "coach", CoachId = "c-2", MemberId = "m-1", WithName = "Ben", CanSend = true }];
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw));
        var loading = vm.OnAppearingAsync();
        await Task.Delay(50);
        var first = chat.Waiting.Dequeue();          // Anna's messages are being fetched
        var anna = vm.Threads.First(t => t.Thread.WithName == "Anna"); var ben = vm.Threads.First(t => t.Thread.WithName == "Ben");

        var openBen = vm.OpenCommand.ExecuteAsync(ben);   // the person switches before Anna's answer arrives
        await Task.Delay(50);
        var second = chat.Waiting.Dequeue();
        second.Reply.SetResult(AppResult<ChatPage>.Success(PageOf("Ben's message")));
        await openBen;
        first.Reply.SetResult(AppResult<ChatPage>.Success(PageOf("Anna's message")));   // the stale answer lands last
        await loading;
        await Task.Delay(50);

        Assert.Equal("Ben", vm.Selected!.Thread.WithName);
        Assert.Equal(["Ben's message"], vm.Bubbles.Select(b => b.Line.Body));
    }

    [Fact]
    public async Task Earlier_messages_are_added_above_without_duplicates_and_the_button_goes_away_at_the_start()
    {
        var (_, chat, vm) = await Open("member@demo.io");
        for (var i = 0; i < 70; i++) { vm.Draft = $"m{i}"; await vm.SendCommand.ExecuteAsync(null); }
        await vm.OnAppearingAsync();   // reload: only the newest page
        Assert.Equal(50, vm.Bubbles.Count);
        Assert.True(vm.HasMore);

        await vm.LoadEarlierCommand.ExecuteAsync(null);

        Assert.Equal(73, vm.Bubbles.Count);   // 3 seeded + 70 sent
        Assert.False(vm.HasMore);
        Assert.Equal(73, vm.Bubbles.Select(b => b.Line.MessageId).Distinct().Count());
        Assert.Equal("m69", vm.Bubbles.Last().Line.Body);
    }

    [Fact]
    public async Task Offline_the_draft_stays_with_an_error_line_and_no_phantom_bubble_appears()
    {
        var (gw, _, vm) = await Open("member@demo.io");
        var before = vm.Bubbles.Count;
        vm.Draft = "are we still on?";
        gw.FailWith = new HttpRequestException("offline");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("are we still on?", vm.Draft);
        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.Equal(before, vm.Bubbles.Count);
    }

    [Fact]
    public async Task The_automatic_refresh_adds_new_messages_keeps_the_draft_and_the_error_and_stays_silent_offline()
    {
        var (gw, _, member) = await Open("member@demo.io");
        member.Draft = "typing...";
        member.ErrorMessage = "an error being read";

        var (_, coachChat, _) = await Open("coach@demo.io", gw);   // Lena answers on another screen
        await coachChat.SendAsync((await coachChat.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan"), "see you at 9");
        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);

        var actions = new List<NotifyCollectionChangedAction>();
        member.Bubbles.CollectionChanged += (_, e) => actions.Add(e.Action);
        await member.AutoRefreshAsync();

        Assert.Equal("see you at 9", member.Bubbles.Last().Line.Body);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);   // the chat was not rebuilt
        Assert.Equal("typing...", member.Draft);
        Assert.Equal("an error being read", member.ErrorMessage);

        gw.FailWith = new HttpRequestException("offline");
        await member.AutoRefreshAsync();
        Assert.Equal("an error being read", member.ErrorMessage);   // no new error while offline
    }

    [Fact]
    public async Task On_a_phone_the_list_comes_first_then_the_chat_and_Back_returns()
    {
        var (_, _, vm) = await Open("member@demo.io", compact: true);
        Assert.True(vm.ShowList); Assert.False(vm.ShowChat);
        await vm.OpenCommand.ExecuteAsync(vm.Threads.First());
        Assert.False(vm.ShowList); Assert.True(vm.ShowChat);
        vm.BackCommand.Execute(null);
        Assert.True(vm.ShowList); Assert.False(vm.ShowChat);

        var (_, _, desktop) = await Open("member@demo.io", compact: false);
        Assert.True(desktop.ShowList); Assert.True(desktop.ShowChat);   // both panes at once
    }

    [Fact]
    public async Task Opening_from_Coaches_selects_that_conversation()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw)) { Pending = ("u-coach1", null) };
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal("Lena Cruz", vm.Selected!.Thread.WithName);
        Assert.Null(chat.Pending);   // used once
    }

    [Fact]
    public async Task Searching_filters_the_list_by_name_at_once_and_adds_message_matches()
    {
        var (_, _, vm) = await Open("coach@demo.io");
        vm.SearchText = "alex";
        await Task.Delay(700);   // the server search waits for a short pause in typing
        Assert.True(vm.IsSearching);
        Assert.All(vm.Threads, t => Assert.Contains("alex", t.Thread.WithName, StringComparison.OrdinalIgnoreCase));

        vm.SearchText = "flat-soled";
        await Task.Delay(700);
        Assert.Contains("flat-soled", vm.Hits.Single().Snippet);

        vm.SearchText = "";
        await Task.Delay(100);
        Assert.False(vm.IsSearching);
        Assert.Empty(vm.Hits);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter MessengerViewModelTests`
Expected: build FAILS (`MessengerViewModel`, `ThreadItem` do not exist).

- [ ] **Step 3: Implement**

Add to `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (after `CoachesViewModel`):

```csharp
/// <summary>A conversation row: the thread plus its picture, which is looked up after the list is shown.</summary>
public partial class ThreadItem(ChatThread thread) : ObservableObject
{
    public ChatThread Thread { get; } = thread;
    [ObservableProperty] private string? avatarUrl;
    public string Letters => Initials.From(Thread.WithName);
}

public partial class MessengerViewModel(IChatService chat, IProfileService profile, IAuthService auth) : LoadableViewModel
{
    List<ChatThread> all = [];
    string? myId;
    int openTicket, listTicket, searchTicket;

    public ObservableCollection<ThreadItem> Threads { get; } = new();
    public ObservableCollection<ChatBubble> Bubbles { get; } = new();
    public ObservableCollection<ChatSearchHit> Hits { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowChat), nameof(ShowList))] private bool isCompact;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowChat), nameof(ShowList))] private bool showingChat;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChat), nameof(CanSend), nameof(CanTake), nameof(CanRelease), nameof(CanClose), nameof(CanReopen))] private ThreadItem? selected;
    [ObservableProperty] private string draft = "";
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string tab = "coach";
    [ObservableProperty] private string filter = "open";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSearching))] private string searchText = "";
    [ObservableProperty] private bool hasTabs;
    [ObservableProperty] private bool hasFilters;
    [ObservableProperty] private int coachTabBadge;
    [ObservableProperty] private int staffTabBadge;

    public bool ShowList => !IsCompact || !ShowingChat;
    public bool ShowChat => !IsCompact || ShowingChat;
    public bool HasChat => Selected is not null;
    public bool IsSearching => SearchText.Trim().Length >= 1;
    public bool CanSend => Selected?.Thread.CanSend == true;
    public bool CanTake => HasFilters && Selected?.Thread is { IsSupport: true, ClaimedBy: null };
    public bool CanRelease => HasFilters && Selected?.Thread is { IsSupport: true, ClaimedBy: not null } t && t.ClaimedBy == myId;
    public bool CanClose => HasFilters && Selected?.Thread is { IsSupport: true, Status: "open" };
    public bool CanReopen => HasFilters && Selected?.Thread is { IsSupport: true, Status: "closed" };
    public string EmptyListText => HasFilters ? "No conversations here." : Tab == "coach" ? "You have no coach yet. Hire one from the Coaches page to start a conversation." : "";

    // ---- loading
    public override async Task OnAppearingAsync()
    {
        myId ??= auth.UserId;
        await LoadThreadsAsync(first: true);
        if (Selected is not null) await ShowAsync(Selected, markRead: true);
    }

    async Task LoadThreadsAsync(bool first)
    {
        var ticket = ++listTicket;
        var result = await chat.ThreadsAsync();
        if (!result.Ok) { if (first) ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        if (ticket != listTicket) return;
        all = result.Value!.ToList();
        // a member has their own "Front desk" thread and gets the Coach/Staff tabs; staff see member threads and get the Open/Mine/Closed filters; a coach gets neither
        var member = all.Any(t => t.IsSupport && t.WithName == "Front desk");
        HasTabs = member;
        HasFilters |= !member && all.Any(t => t.IsSupport);   // once on, stays on, so an emptied inbox keeps its filters
        CoachTabBadge = all.Where(t => !t.IsSupport).Sum(t => t.Unread);
        StaffTabBadge = all.Where(t => t.IsSupport).Sum(t => t.Unread);
        _ = chat.MarkDeliveredAsync();
        Rebuild();
        if (first) PickInitial();
        _ = ResolveAvatarsAsync();
    }

    void Rebuild()
    {
        IEnumerable<ChatThread> shown = HasFilters
            ? all.Where(t => t.IsSupport && Filter switch { "mine" => t.ClaimedBy == myId && t.Status == "open", "closed" => t.Status == "closed", _ => t.Status == "open" })
            : HasTabs ? all.Where(t => t.IsSupport == (Tab == "support")) : all;
        if (IsSearching) shown = shown.Where(t => t.WithName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        var rows = shown.ToList();
        Sync(Threads, rows.Select(t => Threads.FirstOrDefault(x => x.Thread.Key == t.Key && Same(x.Thread, t)) ?? new ThreadItem(t)).ToList(), x => x.Thread.Key, (a, b) => ReferenceEquals(a, b));
        if (Selected is not null) { var fresh = all.FirstOrDefault(t => t.Key == Selected.Thread.Key); if (fresh is not null && !Same(Selected.Thread, fresh)) { Selected = Threads.FirstOrDefault(x => x.Thread.Key == fresh.Key) ?? new ThreadItem(fresh); OnPropertyChanged(nameof(Selected)); } }
        OnPropertyChanged(nameof(EmptyListText));
    }

    static bool Same(ChatThread a, ChatThread b) => a.LastBody == b.LastBody && a.LastAt == b.LastAt && a.Unread == b.Unread && a.Status == b.Status && a.ClaimedBy == b.ClaimedBy && a.CanSend == b.CanSend && a.WithName == b.WithName;

    void PickInitial()
    {
        var want = chat.Pending; chat.Pending = null;
        if (want is { } w)
        {
            var hit = all.FirstOrDefault(t => !t.IsSupport && t.CoachId == w.CoachId && (w.MemberId is null || t.MemberId == w.MemberId));
            if (hit is not null) { Tab = "coach"; Rebuild(); Selected = Threads.FirstOrDefault(x => x.Thread.Key == hit.Key); return; }
        }
        if (Selected is null || Threads.All(x => x.Thread.Key != Selected.Thread.Key)) Selected = IsCompact ? null : Threads.FirstOrDefault();
    }

    async Task ResolveAvatarsAsync()
    {
        foreach (var item in Threads.Where(t => t.AvatarUrl is null && t.Thread.WithAvatar is not null).ToList())
            item.AvatarUrl = (await profile.AvatarUrlAsync(item.Thread.WithAvatar)).Value;
    }

    // ---- one conversation. The newest request wins: a slow answer for a conversation you have left is dropped.
    async Task ShowAsync(ThreadItem item, bool markRead)
    {
        var ticket = ++openTicket;
        var page = await chat.MessagesAsync(item.Thread);
        if (ticket != openTicket) return;
        if (!page.Ok) { ErrorMessage = ErrorText.For(page.Error, page.Message); return; }
        Bubbles.Clear();
        foreach (var b in page.Value!.Bubbles) Bubbles.Add(b);
        HasMore = page.Value.HasMore;
        if (markRead && item.Thread.Unread > 0) { await chat.MarkReadAsync(item.Thread); await LoadThreadsAsync(first: false); }
        else if (markRead) _ = chat.MarkReadAsync(item.Thread);
    }

    [RelayCommand]
    private async Task OpenAsync(ThreadItem item)
    {
        ErrorMessage = null;
        Selected = item;
        ShowingChat = true;
        Bubbles.Clear(); HasMore = false;
        await ShowAsync(item, markRead: true);
    }

    [RelayCommand]
    private async Task OpenHitAsync(ChatSearchHit hit)
    {
        SearchText = "";
        var thread = all.FirstOrDefault(t => t.Kind == hit.Kind && t.CoachId == hit.CoachId && t.MemberId == hit.MemberId);
        if (thread is null) return;
        if (HasTabs) Tab = thread.IsSupport ? "support" : "coach";
        Rebuild();
        var item = Threads.FirstOrDefault(x => x.Thread.Key == thread.Key);
        if (item is not null) await OpenAsync(item);
    }

    [RelayCommand] private void Back() { ShowingChat = false; }

    [RelayCommand]
    private async Task LoadEarlierAsync()
    {
        if (Selected is null || Bubbles.Count == 0) return;
        var item = Selected;
        var page = await chat.MessagesAsync(item.Thread, Bubbles[0].Line.MessageId);
        if (!page.Ok || !ReferenceEquals(item, Selected)) return;
        var have = Bubbles.Select(b => b.Line.MessageId).ToHashSet();
        var add = page.Value!.Bubbles.Where(b => !have.Contains(b.Line.MessageId)).ToList();
        for (var i = add.Count - 1; i >= 0; i--) Bubbles.Insert(0, add[i]);
        HasMore = page.Value.HasMore;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is not { } item || string.IsNullOrWhiteSpace(Draft)) return;
        if (!await RunAsync(() => chat.SendAsync(item.Thread, Draft))) return;   // the draft stays, with the error line
        Draft = "";
        await ShowAsync(item, markRead: false);
        await LoadThreadsAsync(first: false);
    }

    // ---- tabs, filters, search
    [RelayCommand] private Task ShowTabAsync(string tab) { Tab = tab; Selected = null; Bubbles.Clear(); Rebuild(); return SelectFirstAsync(); }
    [RelayCommand] private Task ShowFilterAsync(string filter) { Filter = filter; Rebuild(); return Task.CompletedTask; }

    async Task SelectFirstAsync()
    {
        if (!IsCompact && Threads.FirstOrDefault() is { } first) await OpenAsync(first);
    }

    partial void OnSearchTextChanged(string value)
    {
        Rebuild();
        var ticket = ++searchTicket;
        if (value.Trim().Length < 2) { Hits.Clear(); return; }
        _ = SearchLaterAsync(value, ticket);
    }

    async Task SearchLaterAsync(string text, int ticket)
    {
        await Task.Delay(350);   // wait for a pause in typing
        if (ticket != searchTicket) return;
        var result = await chat.SearchAsync(text);
        if (ticket != searchTicket || !result.Ok) return;
        Fill(Hits, result.Value!);
    }

    // ---- front desk
    [RelayCommand] private Task TakeAsync() => ClaimAsync(true);
    [RelayCommand] private Task ReleaseAsync() => ClaimAsync(false);
    async Task ClaimAsync(bool claim)
    {
        if (Selected is not { } item) return;
        if (await RunAsync(() => chat.ClaimAsync(item.Thread, claim))) await LoadThreadsAsync(first: false);
    }
    [RelayCommand] private Task CloseChatAsync() => SetOpenAsync(false);
    [RelayCommand] private Task ReopenAsync() => SetOpenAsync(true);
    async Task SetOpenAsync(bool open)
    {
        if (Selected is not { } item) return;
        if (await RunAsync(() => chat.SetOpenAsync(item.Thread, open))) await LoadThreadsAsync(first: false);
    }

    // ---- quiet refresh: new messages arrive, the list updates; a draft, an error line and the scroll position are left alone
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        await LoadThreadsAsync(first: false);
        if (Selected is not { } item) return;
        var ticket = openTicket;
        var page = await chat.MessagesAsync(item.Thread);
        if (!page.Ok || ticket != openTicket || !ReferenceEquals(item, Selected)) return;
        var known = Bubbles.ToDictionary(b => b.Line.MessageId);
        foreach (var b in page.Value!.Bubbles)
        {
            if (!known.TryGetValue(b.Line.MessageId, out var old)) Bubbles.Add(b);                              // a new message at the bottom
            else if (old.Status != b.Status) Bubbles[Bubbles.IndexOf(old)] = b;                                  // Sent becomes Delivered or Read
        }
        if (item.Thread.Unread > 0 || page.Value.Bubbles.Any(b => !b.Mine && b.Line.ReadAt is null)) _ = chat.MarkReadAsync(item.Thread);
    }
}
```

Notes for the implementer: `Sync` and `Fill` come from `LoadableViewModel`; `Initials` from `ProfileService.cs`. `auth.UserId` is on `IAuthService`. A staff member whose inbox has never had a thread sees no filter chips until the first member writes (acceptable: there is nothing to filter). The name "Front desk" is what `chat_threads` returns for a member's own thread, so a member whose own name is "Front desk" cannot exist (names are letters and 2 to 60 characters, but a person could in theory register that name; harmless: they would only get tabs, which they have anyway).

- [ ] **Step 4: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS. If `A_slow_answer_for_the_first_conversation...` fails, the ticket guard in `ShowAsync` is wrong: it must compare against the latest `openTicket`, and `OpenAsync` must start a new ticket before the first awaits. If the refresh test shows a `Reset`, `AutoRefreshAsync` rebuilt the collection instead of adding rows.

- [ ] **Step 5: Commit**

```bash
git add src/GymMembership.Core tests/GymMembership.Tests/MessengerViewModelTests.cs
git commit -m "feat: messenger view model - threads, tabs, filters, paging, search and quiet refresh

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The Messenger screen

**Invoke the `frontend-design:frontend-design` skill first.** Keep the existing look: flat panels, hairlines, Barlow, no shadows. Mine are ink bubbles with light text (the existing bubble style), theirs sit on the surface with a hairline; the unread badge reuses the red badge from the bell; status text is small and muted.

**Files:**
- Modify: `src/GymMembership.App/Views/MessagesPage.xaml` and `.xaml.cs` (rewritten), `src/GymMembership.App/StaffShell.xaml`
- Create: `src/GymMembership.App/Controls/ThreadRowView.xaml` and `.xaml.cs`

**Interfaces:**
- Consumes: `MessengerViewModel`, `ThreadItem`, `ChatBubble`, `ChatSearchHit` (Task 3), `Avatar`, `BasePage.Row<TVm>`, `AutoRefresh`.
- Produces: `ThreadRowView` with bindable `Selected` (highlights the row).

- [ ] **Step 1: The conversation row**

`src/GymMembership.App/Controls/ThreadRowView.xaml` (the binding context is a `ThreadItem`):

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:c="clr-namespace:GymMembership.App.Controls"
             xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
             x:Class="GymMembership.App.Controls.ThreadRowView" x:DataType="vm:ThreadItem">
    <Border Style="{StaticResource Panel}" Padding="12,10" x:Name="Frame">
        <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="12">
            <c:Avatar Letters="{Binding Letters}" Url="{Binding AvatarUrl}" Diameter="44" VerticalOptions="Center" />
            <VerticalStackLayout Grid.Column="1" Spacing="2" VerticalOptions="Center">
                <Label Text="{Binding Thread.WithName}" FontFamily="BarlowSemiBold" LineBreakMode="TailTruncation" />
                <Label Text="{Binding Thread.Preview}" Style="{StaticResource Muted}" LineBreakMode="TailTruncation" MaxLines="1" />
            </VerticalStackLayout>
            <VerticalStackLayout Grid.Column="2" Spacing="4" VerticalOptions="Center" HorizontalOptions="End">
                <Label Text="{Binding Thread.LastAt, Converter={StaticResource LocalDate}, ConverterParameter='d MMM'}" Style="{StaticResource Caption}" HorizontalOptions="End" />
                <Border IsVisible="{Binding Thread.HasUnread}" HorizontalOptions="End" MinimumWidthRequest="20" HeightRequest="20" Padding="6,0" StrokeThickness="0"
                        StrokeShape="RoundRectangle 10" BackgroundColor="{AppThemeBinding Light={StaticResource PlateRedLight}, Dark={StaticResource PlateRedDark}}"
                        SemanticProperties.Description="{Binding Thread.UnreadText, StringFormat='{0} unread'}">
                    <Label Text="{Binding Thread.UnreadText}" FontSize="12" FontFamily="BarlowSemiBold" HorizontalOptions="Center" VerticalOptions="Center"
                           TextColor="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource ChalkDark}}" />
                </Border>
            </VerticalStackLayout>
        </Grid>
    </Border>
</ContentView>
```

`ThreadRowView.xaml.cs` is a plain `partial class ThreadRowView : ContentView { public ThreadRowView() => InitializeComponent(); }` plus a `Selected` `BindableProperty` (bool) whose change callback sets `Frame.BackgroundColor` to the chalk token (`ChalkLight`/`ChalkDark` through `SetAppThemeColor`) when true, and back to the surface token when false.

- [ ] **Step 2: The page**

Rewrite `src/GymMembership.App/Views/MessagesPage.xaml` with `x:DataType="vm:MessengerViewModel"`, `Title="Messages"` and this structure (write the full XAML in the same style as `AmenitiesPage.xaml`):

- Root `Grid` with `ColumnDefinitions="{OnIdiom Phone='*', Default='360,*'}"`.
- **List pane** (`IsVisible="{Binding ShowList}"`, column 0): a search `Border`/`Entry` bound to `SearchText` (`Placeholder="Search people and messages"`, `SemanticProperties.Description="Search"`); the member tabs (two `Button`s "Coach" and "Staff", each showing its badge count from `CoachTabBadge` / `StaffTabBadge` when above 0; `IsVisible="{Binding HasTabs}"`; the active one filled, the other outlined, chosen with `DataTrigger`s on `Tab`); the staff filter chips (`Open`, `Mine`, `Closed`; `IsVisible="{Binding HasFilters}"`) built the same way on `Filter`; a `CollectionView` of `Threads` using `ThreadRowView` (tap through a `TapGestureRecognizer` handler `OnOpen`); an empty-view label bound to `EmptyListText`; and, when `IsSearching` has hits, a second `CollectionView` of `Hits` (name, snippet, tap handler `OnOpenHit`).
- **Chat pane** (`IsVisible="{Binding ShowChat}"`, `Grid.Column="{OnIdiom Phone=0, Default=1}"`), three rows: **header** with a Back button (`Command="{Binding BackCommand}"`, visible only when `IsCompact`), an `Avatar` and name, and for staff the buttons "Take" / "Release" / "Close chat" / "Reopen" (each shown by `CanTake`, `CanRelease`, `CanClose`, `CanReopen`; `Command` bindings straight to the view model); **messages**: a `CollectionView` of `Bubbles` with `ItemsUpdatingScrollMode="KeepLastItemInView"`, a header button "Load earlier messages" (`IsVisible="{Binding HasMore}"`, `Command="{Binding LoadEarlierCommand}"`) and the bubble template described below; **composer**: a `Border` with an `Entry` bound to `Draft` (`MaxLength="2000"`, `ReturnCommand="{Binding SendCommand}"`, `Placeholder="Write a message"`) and a Send `Button`, visible when `CanSend`, otherwise a muted label "You can read this conversation, but it is closed for new messages." The `Banner` for `ErrorMessage` sits above the composer.
- **Bubble template** (`x:DataType="svc:ChatBubble"`): the existing look from the old page (ink fill and light text for mine, hairline panel for theirs, `MaximumWidthRequest="460"`, `HorizontalOptions` switched by a `DataTrigger` on `Mine`), showing `Line.Body`, `Line.SentAt` (converter `LocalDate`, `'ddd d MMM, h:mm tt'`), the status word (`Status`, shown only when `Mine`), and, for staff viewing a front-desk thread, `Line.SenderName` above the text when it is not empty.

`MessagesPage.xaml.cs`:

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MessagesPage : BasePage
{
	public MessagesPage(MessengerViewModel vm)
	{
		InitializeComponent();
		vm.IsCompact = DeviceInfo.Idiom == DeviceIdiom.Phone;   // phone: list first, then the chat; desktop: both side by side
		BindingContext = vm;
	}

	// new messages arrive by looking again every few seconds while the page is open (Realtime comes later)
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(6);

	void OnOpen(object? sender, TappedEventArgs e) => Row<MessengerViewModel>(sender, v => v.OpenCommand);
	void OnOpenHit(object? sender, TappedEventArgs e) => Row<MessengerViewModel>(sender, v => v.OpenHitCommand);
	void OnTab(object? sender, EventArgs e) => ((MessengerViewModel)BindingContext).ShowTabCommand.Execute((sender as Button)?.CommandParameter);
	void OnFilter(object? sender, EventArgs e) => ((MessengerViewModel)BindingContext).ShowFilterCommand.Execute((sender as Button)?.CommandParameter);
}
```

(The tab and filter buttons carry `CommandParameter="coach"` / `"support"` / `"open"` / `"mine"` / `"closed"` as plain strings; `Clicked` reads it, so no `RelativeSource` binding is involved.)

- [ ] **Step 3: The front desk menu**

In `src/GymMembership.App/StaffShell.xaml` add, before the Settings item (and after the Admin items): `<FlyoutItem Title="Messages" Route="messages"><ShellContent ContentTemplate="{DataTemplate views:MessagesPage}" /></FlyoutItem>`. `MemberShell` and `CoachShell` already have a Messages item. The bottom bar on phones stays with the four daily screens; Messages is reached from the menu until the dashboard tiles arrive in phase 5.

- [ ] **Step 4: Build and run the tests**

```bash
dotnet test tests/GymMembership.Tests
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
dotnet build src/GymMembership.App -f net10.0-android
```
Expected: all pass, both builds succeed (`XamlBindingTests` now reads `ThreadRowView.xaml` and the new `MessagesPage.xaml`).

- [ ] **Step 5: Look at it (demo mode; Windows and the Android emulator)**

Move `src/GymMembership.App/Resources/Raw/supabase.json` to `supabase.json.bak` for the check and put it back afterwards. Windows: run `bin\Debug\net10.0-windows10.0.19041.0\win-x64\GymMembership.App.exe`; Android: build with `-p:EmbedAssembliesIntoApk=true`, uninstall the old app first, emulator `pixel_7_-_api_36_0`.

Check, in light and dark:
- **Member (Alex):** Coach tab lists Lena Cruz with "You: Perfect, see you on the session." and the chat opens beside it on Windows, in a second screen with a Back button on the phone. Bubbles: mine ink and right, hers hairline and left; the last of mine shows "Read" or "Delivered" or "Sent". Typing and Send adds the bubble and clears the box. The Staff tab shows "Front desk"; writing there works.
- **Bea (no coach):** the Coach tab says she has no coach yet; the Staff tab works. Sign in as front desk (Marco): Messages shows Bea's message with "Take"; taking it moves it under Mine; "Close chat" moves it under Closed; reopening works. Sign back in as Bea: the reply shows from "Front desk" only.
- **Coach (Lena):** the search box finds "alex" by name and "flat-soled" by message text and opens the right chat; clearing the box restores the list.
- Leave a chat open 10 seconds while another user writes into it: the new bubble appears without the list jumping, and a half-typed draft is untouched.
- The bell's badge drops after opening a chat that had an unread message alert.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: messenger screen with list, bubbles, tabs, front desk inbox and search

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

(`git status` must not list `supabase.json.bak` or `docs/screenshotsforexample/`.)

---

### Task 5: Wrap-up

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-role-redesign-design.md`

- [ ] **Step 1: Record what was built in the spec**

In section 4, after phase 4's sentence, add: `**Built in phase 4a:** migration 0015 (delivered_at, support_threads, support_messages, permission support:reply, and the functions chat_threads, chat_messages, mark_chat_read, mark_chats_delivered, send_support_message, claim_support_thread, set_support_status, chat_search); the new Messenger for all roles with list and chat, Coach and Staff tabs for members, a front desk inbox that can take and close conversations, sent/delivered/read status, search by name and message text, paged history, and 6-second polling; the old chat's unbounded reads and wrong-conversation race are gone. Photos are plan 4b; Realtime, typing and online status are plan 4c.`

- [ ] **Step 2: Full verification**

```bash
dotnet test tests/GymMembership.Tests
npm --prefix supabase/tests/local test
dotnet build src/GymMembership.App -f net10.0-android
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
```
Expected: every test passes (report the counts), 88 database checks pass, both builds succeed.

- [ ] **Step 3: Review**

Run `ponytail:ponytail-review` on `git diff role-redesign-phase3...HEAD`, apply what is worth applying, re-run Step 2. Then one fresh whole-branch review of the same range (review package, most capable model, the plan's Review Focus verbatim), with extra attention to `0015`: the three-way `union all` in `chat_threads` (column order and types), `reference @>` matching, who can read what (Review Focus 4: write the privacy checks the reviewer would try), the `#variable_conflict use_column` functions, the escape handling in `chat_search`, `claim_support_thread` races (two staff taking at once), and whether any function is callable by a user it should refuse.

- [ ] **Step 4: Commit and hand over**

```bash
git add docs/superpowers/specs/2026-10-15-role-redesign-design.md
git commit -m "docs: phase 4a recorded in the redesign spec

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Stop here. Do not push or merge. Tell the owner: branch `role-redesign-phase4a` (on top of the unmerged `role-redesign-phase3`) is ready; **`npx supabase db push` must run for `0012`, `0013`, `0014` and `0015`, in that order**, before the Messenger works against the real project; plans 4b (photos) and 4c (Realtime) are next.
