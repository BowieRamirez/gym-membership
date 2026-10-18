# Role Redesign, Phase 4b: Photos in Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. UI task (4): invoke the `frontend-design:frontend-design` skill first. Finish with `ponytail:ponytail-review` (CLAUDE.md).

**Goal:** Members, coaches and front desk can send a photo, with an optional caption, in any conversation they can already write in: member and coach to each other, member and front desk in both directions. The photo shows inside the bubble; a photo with no caption is a normal message.

**Architecture:** A new private storage bucket `chat-attachments` whose access rule lives in one database function (`can_use_chat_file`), used by both the upload and the read policy. `messages` and `support_messages` get an `attachment_path` column, a check that the path belongs to that conversation, and a relaxed "text or photo" rule. The app uploads first, then sends the message with the stored path; the service turns each path into a short-lived signed link when it loads a page of messages. Nothing else about the Messenger changes.

**Tech Stack:** Supabase Postgres + Storage (embedded-Postgres local tests), .NET 10 MAUI (Windows + Android), CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (decision 8 "Chat photos", database items 3.2 `messages.attachment_path`, 3.3 storage `chat-attachments`, 3.5 privacy rules). Builds on plan `2026-10-16-role-redesign-phase4a.md`.

## Where this sits in phase 4

| Plan | Contents |
| --- | --- |
| 4a (done) | Messenger core. |
| **4b (this plan)** | Photos in chat. |
| 4c (next) | Realtime, typing, online and last-active status (starts with a feasibility probe). |

## Database changes in this phase (the owner asked to be told)

| Change | Kind |
| --- | --- |
| **New storage bucket** `chat-attachments` (private, images only, 5 MB) | additive |
| `messages`: new column `attachment_path`; the text rule becomes "text or a photo"; a check that the photo path belongs to that conversation; the insert column grant gains `attachment_path` | additive / rule relaxed |
| `support_messages`: same column, same two rules | additive / rule relaxed |
| **New function** `can_use_chat_file(name, write)` and two storage policies on `storage.objects` that call it | additive |
| `chat_threads()` and `chat_messages()` re-created so the list shows "Photo" for a photo-only message and a page carries `attachment_path`; `send_support_message()` re-created with an optional photo argument (the old two-argument calls keep working) | functions replaced |

Nothing is dropped except the three function versions that are replaced in the same file. **`0012` to `0015` are not on the real Supabase project yet; push them first, then `0016`, in order.** No Realtime change in this plan.

## Global Constraints

- Migrations are additive in effect; `0016` may be edited in place until the owner pushes it.
- Privacy: a coach chat photo is readable only by that coach and that member (not by admins or staff); a front desk photo only by that member and staff with `support:reply`. Writing a photo needs the same rights as writing text: coach chats need an active hire and an active plan; a front desk photo needs to be that member or a staff member with `support:reply`. A guest or an ended hire can still read old photos.
- Images only: `.jpg`, `.jpeg`, `.png`, `.webp`, 1 byte to 5 MB, checked in the app before upload (message `bad_image`) and again by the bucket.
- One photo per message. A caption is optional and follows the existing 2000-character rule.
- Trim-safe bindings only: no `RelativeSource AncestorType`; controls update their children from `BindableProperty` callbacks. Colors and type come from the existing tokens; WCAG AA must hold (`ContrastTests` stays green). Status is shown in words.
- Every new Core behavior has an xUnit test against `DemoGateway`; every new database rule has a test in `supabase/tests/local/rules.mjs`; every new request shape is checked in `SupabaseGatewayTests`.
- Never push or merge without the owner saying so. Never commit `docs/screenshotsforexample/` or `supabase.json.bak`. `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo-mode checks and put it back.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Decisions made while planning (change any and this plan changes)

| Question | Decision |
| --- | --- |
| Photos per message | One. Several photos are several messages. |
| Where photos come from | The existing gallery picker (`IProofPicker`, "choose a photo"). A live camera shot is not added. |
| File names and folders | Coach chat: `{coach id}/{member id}/{time}-{name}`. Front desk: `support/{member id}/{time}-{name}`. The folder is the access rule. |
| Enlarging a photo | Not in this plan: the bubble shows a fixed-size crop. Tap-to-enlarge is a later polish item. |
| A failed send after a good upload | The file stays in the bucket unseen. Cleanup waits until storage use matters (same trade-off as profile pictures). |
| A half-chosen photo when you move to another conversation | It is dropped (the caption draft is kept per conversation, as in 4a; the photo is not). |
| Demo mode | The demo keeps uploaded photos in memory and serves them from a temp file, and its picker supplies a generated picture, so the whole flow can be seen without a database. |
| Photo link lifetime | Signed links last an hour and are reused for 50 minutes; a long-open chat re-signs when the page refreshes. |

## Review Focus

Failure modes the happy path will not show. Each has a test in the task named.

1. A photo-only message must never look empty: the list preview says "Photo", the bubble shows the picture. [Task 1, 2, 4]
2. A photo path from another conversation, or another person's folder, must be refused when attaching and when uploading. [Task 1, 2]
3. Staff, admins and other members must not be able to read a coach chat photo; other members must not read a front desk photo. [Task 1, 2]
4. A wrong file type or an oversize file is refused with the plain `bad_image` message before anything is uploaded. [Task 2, 3]
5. Offline or failed send: the caption and the chosen photo stay, no phantom bubble appears; a photo whose link cannot be made shows "Photo unavailable", not a blank hole. [Task 2, 3, 4]

---

### Task 1: Migration `0016` and its database tests

**Files:**
- Create: `supabase/migrations/0016_chat_photos.sql`
- Modify: `supabase/tests/local/rules.mjs` (append above the final summary line)

**Interfaces:**
- Produces:
  - Bucket `chat-attachments`; columns `messages.attachment_path`, `support_messages.attachment_path`.
  - `can_use_chat_file(p_name text, p_write boolean) returns boolean`.
  - `chat_messages(p_kind text, p_member uuid, p_coach uuid default null, p_before bigint default null, p_limit integer default 50)` now returns an extra last column `attachment_path text`.
  - `send_support_message(p_member uuid, p_body text, p_attachment text default null) returns bigint`.
  - `chat_threads()` unchanged in shape; `last_body` is `'Photo'` when the last message is a photo without text.

- [ ] **Step 0: Create the branch**

```bash
git checkout role-redesign-phase4a && git checkout -b role-redesign-phase4b
```

- [ ] **Step 1: Write the failing tests**

Append to `supabase/tests/local/rules.mjs` above `console.log(\`\n${pass} passed, ${failed} failed\`);`. Earlier sections define `admin`, `desk`, `coach`, `alex`, `bea`, `run`, `code`, `test`, `db`, `user`, `monthly`, `threads` and `page`. Fay is a new, fully set-up member (active plan and an active hire with the coach) so the tests do not depend on what earlier tests did to other members. Bea has a hire with the coach but no active plan.

```js
console.log('\nchat photos (0016)');
const fay = await user('fay@t.io', 'member', 'Fay');
await db.query(`insert into user_membership_packages (member_id, membership_package_id, status, starts_at, ends_at) values ($1, $2, 'active', now(), now() + interval '30 days')`, [fay, monthly]);
await db.query(`insert into coach_hires (coach_id, member_id) values ($1, $2)`, [coach, fay]);
const okFile = async (uid, name, write) => (await run(uid, `select can_use_chat_file($1, $2) as ok`, [name, write])).rows[0].ok;
const pic = (a, b) => `${a}/${b}/photo-1.jpg`;

await test('the chat-attachments bucket is private, images only, 5 MB', async () => {
  const b = (await db.query(`select public, file_size_limit, allowed_mime_types from storage.buckets where id = 'chat-attachments'`)).rows[0];
  assert.equal(b.public, false); assert.equal(Number(b.file_size_limit), 5242880);
  assert.deepEqual([...b.allowed_mime_types].sort(), ['image/jpeg', 'image/png', 'image/webp']);
});
await test('coach chat photos: only the two people read them; both can add while the hire and plan are active', async () => {
  const f = pic(coach, fay);
  for (const who of [coach, fay]) { assert.equal(await okFile(who, f, false), true); assert.equal(await okFile(who, f, true), true); }
  for (const who of [bea, alex, desk, admin]) assert.equal(await okFile(who, f, false), false);   // other members, front desk and even the admin
  assert.equal(await okFile(desk, f, true), false);
});
await test('a guest can read a coach chat photo but not add one; an ended hire is the same', async () => {
  const g = pic(coach, bea);   // Bea has a hire but no active plan
  assert.equal(await okFile(bea, g, false), true); assert.equal(await okFile(bea, g, true), false);
  await db.query(`update coach_hires set status = 'ended', ended_at = now() where member_id = $1 and status = 'active'`, [fay]);
  assert.equal(await okFile(fay, pic(coach, fay), false), true); assert.equal(await okFile(fay, pic(coach, fay), true), false);
  await db.query(`insert into coach_hires (coach_id, member_id) values ($1, $2)`, [coach, fay]);   // hired again for the tests below
});
await test('front desk photos: the member and staff with support:reply, nobody else', async () => {
  const f = `support/${fay}/photo-1.png`;
  for (const w of [true, false]) { assert.equal(await okFile(fay, f, w), true); assert.equal(await okFile(desk, f, w), true); }
  assert.equal(await okFile(admin, f, false), true);
  for (const who of [bea, alex, coach]) { assert.equal(await okFile(who, f, false), false); assert.equal(await okFile(who, f, true), false); }
});
await test('a name that is not exactly one of the two shapes is refused', async () => {
  for (const name of [`support/${fay}/../x.jpg`, `support/${fay}/.hidden.jpg`, `support/${fay}/a/b.jpg`, `${fay}/photo.jpg`, `support/not-a-uuid/x.jpg`,
                      `${coach}/${fay}/${'x'.repeat(121)}`, `${coach}/${fay}/x.jpg/`, `SUPPORT/${fay}/x.jpg`, ''])
    assert.equal(await okFile(fay, name, false), false, name);
});
await test('the storage policies apply the same rule to real uploads and reads', async () => {
  await db.query(`grant select, insert on storage.objects to authenticated`);   // the real project grants this; the local stub does not
  const add = (uid, name) => run(uid, `insert into storage.objects (bucket_id, name) values ('chat-attachments', $1)`, [name]);
  assert.equal(await code(add(fay, pic(coach, fay))), 'ok');
  assert.match(await code(add(bea, pic(coach, fay))), /42501/);
  assert.match(await code(add(fay, pic(coach, alex))), /42501/);
  assert.equal(await code(add(fay, `support/${fay}/p.png`)), 'ok');
  assert.equal((await run(coach, `select name from storage.objects where bucket_id = 'chat-attachments'`)).rowCount, 1);   // only the coach chat photo
  assert.equal((await run(desk, `select name from storage.objects where bucket_id = 'chat-attachments'`)).rowCount, 1);    // only the front desk photo
  assert.equal((await run(alex, `select name from storage.objects where bucket_id = 'chat-attachments'`)).rowCount, 0);
});
await test('a photo can be the whole message; a blank message with no photo is still refused', async () => {
  assert.equal(await code(run(fay, `insert into messages (coach_id, member_id, sender_id, body, attachment_path) values ($1, $2, $2, '', $3)`, [coach, fay, pic(coach, fay)])), 'ok');
  assert.match(await code(run(fay, `insert into messages (coach_id, member_id, sender_id, body) values ($1, $2, $2, '  ')`, [coach, fay])), /23514/);
});
await test('a photo from another conversation or a made-up path cannot be attached', async () => {
  const send = path => run(fay, `insert into messages (coach_id, member_id, sender_id, body, attachment_path) values ($1, $2, $2, 'x', $3)`, [coach, fay, path]);
  assert.match(await code(send(pic(coach, alex))), /23514/);
  assert.match(await code(send('../x.jpg')), /23514/);
  assert.match(await code(send(`support/${fay}/p.png`)), /23514/);
});
await test('front desk: a photo alone or with a caption, only into the right thread', async () => {
  const send = (uid, member, body, att) => run(uid, `select send_support_message(p_member => $1, p_body => $2, p_attachment => $3) as id`, [member, body, att]);
  assert.ok((await send(fay, fay, '', `support/${fay}/photo-2.png`)).rows[0].id);
  assert.match(await code(send(fay, fay, '', null)), /22023:empty_message/);
  assert.match(await code(send(fay, fay, 'hi', `support/${alex}/p.png`)), /42501/);
  assert.match(await code(send(fay, fay, 'hi', pic(coach, fay))), /42501/);
  assert.equal(await code(send(desk, fay, 'here you go', `support/${fay}/photo-3.png`)), 'ok');
  assert.match(await code(send(desk, fay, 'x', `support/${alex}/p.png`)), /42501/);
  assert.equal(await code(run(fay, `select send_support_message($1, 'text only still works')`, [fay])), 'ok');   // the two-argument call is unchanged
});
await test('pages carry the photo path, and the list says "Photo" when the last message has no text', async () => {
  const rows = (await page(fay, 'support', null, fay)).rows;
  assert.equal(rows.find(r => r.body === '').attachment_path, `support/${fay}/photo-2.png`);
  assert.equal(rows.find(r => r.body === 'here you go').attachment_path, `support/${fay}/photo-3.png`);
  assert.equal(rows.find(r => r.body === 'text only still works').attachment_path, null);

  await db.query(`delete from user_notifications where user_id = $1`, [desk]);
  await run(fay, `select send_support_message(p_member => $1, p_body => '', p_attachment => $2)`, [fay, `support/${fay}/photo-4.png`]);
  assert.equal((await threads(fay)).find(r => r.kind === 'support').last_body, 'Photo');
  assert.equal((await threads(desk)).find(r => r.kind === 'support' && r.member_id === fay).last_body, 'Photo');
  assert.equal((await threads(fay)).find(r => r.kind === 'coach').last_body, 'Photo');   // the coach chat's last message was the photo-only one above
  assert.equal((await db.query(`select body from user_notifications where user_id = $1 and type = 'message'`, [desk])).rows[0].body, 'Photo');
});

```

- [ ] **Step 2: Run to verify they fail**

Run: `npm --prefix supabase/tests/local test` (if it says a shared-memory block is in use, run `taskkill //F //IM postgres.exe` first).
Expected: the 10 new tests FAIL (`bucket` row missing, `function can_use_chat_file(...) does not exist`, ...); the 92 earlier pass.

- [ ] **Step 3: Write the migration**

Create `supabase/migrations/0016_chat_photos.sql`:

```sql
-- 0016: photos in chat (role redesign, phase 4b)
--
-- A photo is uploaded to the private chat-attachments bucket, then sent as a message that remembers its path.
-- Where a photo may live, and who may read or add it, is decided in one function used by both storage policies.

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('chat-attachments', 'chat-attachments', false, 5242880, array['image/png', 'image/jpeg', 'image/webp'])
on conflict (id) do update
set public = excluded.public, file_size_limit = excluded.file_size_limit, allowed_mime_types = excluded.allowed_mime_types;

-- ---------------------------------------------------------------------
-- Columns and rules
-- ---------------------------------------------------------------------

alter table public.messages         add column attachment_path text;
alter table public.support_messages add column attachment_path text;

-- the text may be empty when a photo is attached
alter table public.messages drop constraint messages_body_check;
alter table public.messages add constraint messages_text_or_photo check (length(trim(body)) > 0 or attachment_path is not null);
alter table public.support_messages drop constraint support_messages_body_check;
alter table public.support_messages add constraint support_messages_text_or_photo
	check ((char_length(btrim(body)) > 0 or attachment_path is not null) and char_length(body) <= 2000);

-- a photo belongs to the conversation it is sent in: {coach}/{member}/{file}, or support/{member}/{file}
alter table public.messages add constraint messages_photo_in_conversation
	check (attachment_path is null or attachment_path ~ ('^' || coach_id::text || '/' || member_id::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$'));
alter table public.support_messages add constraint support_messages_photo_in_thread
	check (attachment_path is null or attachment_path ~ ('^support/' || member_id::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$'));

grant insert (attachment_path) on public.messages to authenticated;

-- ---------------------------------------------------------------------
-- Who may use a file
-- ---------------------------------------------------------------------

-- Reading: the two people of a coach chat; the member and staff with support:reply for the front desk.
-- Adding: the same, and for a coach chat also an active hire and an active plan (as for text).
create function public.can_use_chat_file(p_name text, p_write boolean) returns boolean
language plpgsql stable security definer
set search_path = ''
as $$
declare
	v_uid  uuid := auth.uid();
	v_id   constant text := '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
	v_file constant text := '[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$';
	v_coach uuid;
	v_member uuid;
begin
	if v_uid is null or p_name is null or not public.is_active_user() then return false; end if;

	if p_name ~ ('^support/' || v_id || '/' || v_file) then
		v_member := split_part(p_name, '/', 2)::uuid;
		return v_member = v_uid or public.has_permission('support:reply');
	elsif p_name ~ ('^' || v_id || '/' || v_id || '/' || v_file) then
		v_coach := split_part(p_name, '/', 1)::uuid;
		v_member := split_part(p_name, '/', 2)::uuid;
		if v_uid <> v_coach and v_uid <> v_member then return false; end if;
		return not p_write or (public.coaches_member(v_coach, v_member) and public.is_active_member(v_member));
	end if;
	return false;
end;
$$;

revoke all on function public.can_use_chat_file(text, boolean) from public, anon, authenticated;
grant execute on function public.can_use_chat_file(text, boolean) to authenticated;

create policy "chat attachments: add to your conversation" on storage.objects for insert to authenticated
	with check (bucket_id = 'chat-attachments' and public.can_use_chat_file(name, true));
create policy "chat attachments: read your conversation" on storage.objects for select to authenticated
	using (bucket_id = 'chat-attachments' and public.can_use_chat_file(name, false));

-- ---------------------------------------------------------------------
-- Functions that show or send messages
-- ---------------------------------------------------------------------

-- Same as 0015 except that last_body says "Photo" when the last message has no text.
create or replace function public.chat_threads()
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
		select 'coach'::text as kind, p.coach_id as coach_id, p.member_id as member_id,
		       case when p.coach_id = v_uid then p.member_id else p.coach_id end as with_id,
		       pr.full_name as with_name, pr.avatar_path as with_avatar,
		       coalesce(nullif(lm.body, ''), case when lm.attachment_path is not null then 'Photo' end) as last_body,
		       lm.sent_at as last_at, coalesce(lm.sender_id = v_uid, false) as last_from_me,
		       (select count(*)::integer from public.messages x
		         where x.coach_id = p.coach_id and x.member_id = p.member_id and x.sender_id <> v_uid and x.read_at is null) as unread,
		       (public.coaches_member(p.coach_id, p.member_id) and public.is_active_member(p.member_id)) as can_send,
		       null::text as status, null::uuid as claimed_by, null::text as claimed_by_name
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
		       coalesce(nullif(lm.body, ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id = v_uid, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = mb.user_id and s.sender_id <> v_uid and s.read_at is null),
		       true, coalesce(t.status, 'open'), null::uuid, null::text
		from public.members mb
		left join public.support_threads t on t.member_id = mb.user_id
		left join lateral (select s.body, s.attachment_path, s.sent_at, s.sender_id from public.support_messages s where s.member_id = mb.user_id order by s.message_id desc limit 1) lm on true
		where mb.user_id = v_uid

		union all

		-- the inbox, for staff who may answer
		select 'support'::text, null::uuid, t.member_id, t.member_id, pr.full_name, pr.avatar_path,
		       coalesce(nullif(lm.body, ''), case when lm.attachment_path is not null then 'Photo' end), lm.sent_at, coalesce(lm.sender_id <> t.member_id, false),
		       (select count(*)::integer from public.support_messages s where s.member_id = t.member_id and s.sender_id = t.member_id and s.read_at is null),
		       true, t.status, t.claimed_by, cb.full_name
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

-- A page of messages now also says which photo each one carries (the return type changes, so the function is replaced).
drop function public.chat_messages(text, uuid, uuid, bigint, integer);
create function public.chat_messages(p_kind text, p_member uuid, p_coach uuid default null, p_before bigint default null, p_limit integer default 50)
returns table (message_id bigint, sender_id uuid, body text, sent_at timestamptz, delivered_at timestamptz, read_at timestamptz, sender_name text, attachment_path text)
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
			select m.message_id, m.sender_id, m.body, m.sent_at, m.delivered_at, m.read_at, null::text, m.attachment_path
			from public.messages m
			where m.coach_id = p_coach and m.member_id = p_member and (p_before is null or m.message_id < p_before)
			order by m.message_id desc limit v_limit;
	elsif p_kind = 'support' then
		if v_uid is distinct from p_member and not public.has_permission('support:reply') then perform public.fail('forbidden', '42501'); end if;
		return query
			select s.message_id, s.sender_id, s.body, s.sent_at, s.delivered_at, s.read_at,
			       -- a member never learns which staff member answered; staff do
			       case when v_uid = p_member or s.sender_id = p_member then null else (select pr.full_name from public.profiles pr where pr.id = s.sender_id) end,
			       s.attachment_path
			from public.support_messages s
			where s.member_id = p_member and (p_before is null or s.message_id < p_before)
			order by s.message_id desc limit v_limit;
	else
		perform public.fail('bad_input', '22023');
	end if;
end;
$$;

revoke all on function public.chat_messages(text, uuid, uuid, bigint, integer) from public, anon, authenticated;
grant execute on function public.chat_messages(text, uuid, uuid, bigint, integer) to authenticated;

-- A member writes to the front desk (p_member is themselves), or staff answer a member. The photo is optional.
drop function public.send_support_message(uuid, text);
create function public.send_support_message(p_member uuid, p_body text, p_attachment text default null) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_body text := coalesce(btrim(p_body), '');
	v_att text := nullif(btrim(p_attachment), '');
	v_id bigint;
	v_claimed uuid;
	v_name text;
	v_preview text;
begin
	perform public.require_active_user();
	if v_body = '' and v_att is null then perform public.fail('empty_message', '22023'); end if;
	if char_length(v_body) > 2000 then perform public.fail('message_too_long', '22023'); end if;

	if v_uid = p_member then
		if not exists (select 1 from public.members where user_id = v_uid) then perform public.fail('forbidden', '42501'); end if;
	elsif public.has_permission('support:reply') then
		if not exists (select 1 from public.members where user_id = p_member) then perform public.fail('not_found', '22023'); end if;
	else
		perform public.fail('forbidden', '42501');
	end if;

	-- the photo has to be in this member's front desk folder
	if v_att is not null and v_att !~ ('^support/' || p_member::text || '/[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}$') then perform public.fail('forbidden', '42501'); end if;

	insert into public.support_threads (member_id) values (p_member) on conflict (member_id) do nothing;
	insert into public.support_messages (member_id, sender_id, body, attachment_path) values (p_member, v_uid, v_body, v_att) returning message_id into v_id;
	v_preview := coalesce(nullif(left(v_body, 80), ''), 'Photo');

	if v_uid = p_member then
		update public.support_threads set last_message_at = now(), status = 'open' where member_id = p_member returning claimed_by into v_claimed;
		select full_name into v_name from public.profiles where id = v_uid;
		if v_claimed is not null and exists (select 1 from public.profiles where id = v_claimed and is_active) then
			perform public.notify(v_claimed, 'message', 'New message from ' || v_name, v_preview, jsonb_build_object('support_member', p_member::text));
		else
			perform public.notify_staff('message', 'New message from ' || v_name, v_preview, jsonb_build_object('support_member', p_member::text));
		end if;
	else
		update public.support_threads set last_message_at = now() where member_id = p_member;
		perform public.notify(p_member, 'message', 'Message from the front desk', v_preview, jsonb_build_object('support_member', p_member::text));
	end if;

	return v_id;
end;
$$;

revoke all on function public.send_support_message(uuid, text, text) from public, anon, authenticated;
grant execute on function public.send_support_message(uuid, text, text) to authenticated;
```

- [ ] **Step 4: Run to verify they pass**

Run: `npm --prefix supabase/tests/local test`
Expected: `ok   0016_chat_photos.sql` during setup, then every test passes (92 earlier plus the 10 new, 102). If one fails, read the failure before touching the migration. Likely suspects: the constraint names `messages_body_check` and `support_messages_body_check` (Postgres names an inline column check `{table}_{column}_check`; read `\d messages` in the harness if the `drop constraint` errors); the `okFile` boolean coming back as `null` instead of `false` (a function that returns null for a null name; the tests pass real strings); `chat_threads` complaining about the lateral select order after `create or replace` (the column list must match the original return table exactly).

- [ ] **Step 5: Commit**

```bash
git add supabase/migrations/0016_chat_photos.sql supabase/tests/local/rules.mjs
git commit -m "feat(db): photos in chat - chat-attachments bucket, attachment paths and access rule (0016)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Models, `ChatService.SendPhotoAsync` and the demo gateway

**Files:**
- Modify: `src/GymMembership.Core/Models/Models.cs` (`ChatMessage`, `SupportMessage`, `ChatLine` gain `AttachmentPath`)
- Modify: `src/GymMembership.Core/Services/Features.cs` (`ChatBubble`, `IChatService`, `ChatService`)
- Modify: `src/GymMembership.Core/Services/ProfileService.cs` (shared image check, shared `LinkCache`)
- Modify: `src/GymMembership.Core/Services/Foundation.cs` (`StoragePath.Safe`), `src/GymMembership.Core/Backend/SupabaseGateway.cs` (use it)
- Modify: `src/GymMembership.Core/Demo/DemoGateway.cs`
- Create: `tests/GymMembership.Tests/ChatPhotoTests.cs`
- Modify: `tests/GymMembership.Tests/SupabaseGatewayTests.cs`, `tests/GymMembership.Tests/MessengerViewModelTests.cs` (the `SlowChat` fake gains the new method)

**Interfaces:**
- Produces:
  - `ChatLine.AttachmentPath`, `ChatMessage.AttachmentPath`, `SupportMessage.AttachmentPath` (`string?`).
  - `ChatBubble(ChatLine Line, bool Mine, string? PhotoUrl = null)` with `Status` (as before), `HasText`, `ShowPhoto` (a link exists), `PhotoMissing` (a photo was sent but no link could be made).
  - `IChatService.SendPhotoAsync(ChatThread thread, string fileName, byte[] data, string caption)` returning `AppResult<bool>`.
  - `ProfileService.IsAcceptableImage(string fileName, byte[] data)` (static, public).
  - `StoragePath.Safe(string path)` (static, public): replaces characters outside `A-Za-z0-9._-` in the file part with `_`.
  - Demo: bucket `chat-attachments` keeps bytes in memory, enforces the same path rule as `can_use_chat_file`, and `SignedUrlAsync` returns a local file path for an allowed, existing photo.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/ChatPhotoTests.cs`. Demo people: Alex (`member@demo.io`, id `u-alex`, active plan, hired by Lena), Lena (`coach@demo.io`, `u-coach1`), Bea (`bea@demo.io`, `u-bea`, hired by Lena but no active plan), Carlo (`carlo@demo.io`, never hired), Marco (`employee@demo.io`, front desk), Dana (`admin@demo.io`).

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class ChatPhotoTests
{
    static readonly byte[] Photo = [1, 2, 3, 4];

    static async Task<(DemoGateway Gw, ChatService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new ChatService(gw, new NotificationService(gw)));
    }

    static async Task<ChatThread> CoachThread(ChatService svc, string with = "Lena Cruz") => (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport && t.WithName == with);
    static async Task<ChatThread> DeskThread(ChatService svc) => (await svc.ThreadsAsync()).Value!.Single(t => t.IsSupport);

    [Fact]
    public async Task A_member_sends_a_photo_to_the_coach_with_a_caption_and_both_see_it()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);

        Assert.True((await alex.SendPhotoAsync(thread, "Squat form.JPG", Photo, "  how is my depth?  ")).Ok);

        var mine = (await alex.MessagesAsync(thread)).Value!.Bubbles.Last();
        Assert.Equal("how is my depth?", mine.Line.Body);
        Assert.StartsWith("u-coach1/u-alex/", mine.Line.AttachmentPath);
        Assert.EndsWith("-Squat_form.JPG", mine.Line.AttachmentPath);
        Assert.True(mine.ShowPhoto); Assert.False(mine.PhotoMissing); Assert.True(mine.HasText);

        var (_, lena) = await As("coach@demo.io", gw);
        var theirs = (await lena.MessagesAsync(await CoachThread(lena, "Alex Tan"))).Value!.Bubbles.Last();
        Assert.True(theirs.ShowPhoto);
        Assert.False(theirs.Mine);
    }

    [Fact]
    public async Task A_photo_with_no_caption_is_a_message_and_the_list_says_Photo()
    {
        var (_, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);

        Assert.True((await alex.SendPhotoAsync(thread, "a.png", Photo, "")).Ok);

        var last = (await alex.MessagesAsync(thread)).Value!.Bubbles.Last();
        Assert.False(last.HasText); Assert.True(last.ShowPhoto);
        Assert.Equal("You: Photo", (await CoachThread(alex)).Preview);
        Assert.Equal(AppErrorKind.Validation, (await alex.SendAsync(thread, "  ")).Error);   // plain text still needs text
    }

    [Fact]
    public async Task A_wrong_file_type_an_empty_file_an_oversize_file_and_a_long_caption_are_refused_before_anything_is_uploaded()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);
        var before = (await alex.MessagesAsync(thread)).Value!.Bubbles.Count;

        foreach (var (name, data) in new (string, byte[])[] { ("notes.pdf", Photo), ("a.gif", Photo), ("a.jpg", []), ("a.png", new byte[ProfileService.MaxBytes + 1]), ("noextension", Photo) })
        {
            var r = await alex.SendPhotoAsync(thread, name, data, "");
            Assert.Equal(AppErrorKind.Validation, r.Error); Assert.Equal("bad_image", r.Message);
        }
        Assert.Equal("message_too_long", (await alex.SendPhotoAsync(thread, "a.png", Photo, new string('x', 2001))).Message);
        Assert.Equal(before, (await alex.MessagesAsync(thread)).Value!.Bubbles.Count);
    }

    [Fact]
    public async Task Nobody_outside_the_conversation_can_read_or_add_a_coach_chat_photo_not_even_staff()
    {
        var (gw, alex) = await As("member@demo.io");
        await alex.SendPhotoAsync(await CoachThread(alex), "a.png", Photo, "");
        var path = (await alex.MessagesAsync(await CoachThread(alex))).Value!.Bubbles.Last().Line.AttachmentPath!;

        foreach (var who in new[] { "carlo@demo.io", "employee@demo.io", "admin@demo.io", "bea@demo.io" })
        {
            await As(who, gw);
            Assert.Null(await gw.SignedUrlAsync("chat-attachments", path));
            await Assert.ThrowsAnyAsync<Exception>(() => gw.UploadAsync("chat-attachments", "u-coach1/u-alex/sneaky.png", Photo));
        }
        await As("coach@demo.io", gw);
        Assert.NotNull(await gw.SignedUrlAsync("chat-attachments", path));
    }

    [Fact]
    public async Task A_guest_and_an_ended_hire_can_read_old_photos_but_not_send_new_ones()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);
        await alex.SendPhotoAsync(thread, "a.png", Photo, "before");
        Assert.True((await new CoachService(gw).EndHireAsync((await gw.ListAsync<CoachHire>()).First(h => h.Status == "active").Id)).Ok);

        Assert.True((await alex.MessagesAsync(thread)).Value!.Bubbles.Last().ShowPhoto);                      // history stays
        Assert.Equal(AppErrorKind.Unauthorized, (await alex.SendPhotoAsync(thread, "b.png", Photo, "after")).Error);

        var (_, bea) = await As("bea@demo.io", gw);   // hired Lena but has no active plan
        Assert.Equal(AppErrorKind.Unauthorized, (await bea.SendPhotoAsync(await CoachThread(bea), "c.png", Photo, "")).Error);
    }

    [Fact]
    public async Task Front_desk_photos_go_both_ways_and_only_the_member_and_staff_see_them()
    {
        var (gw, bea) = await As("bea@demo.io");   // a guest may still write to the front desk
        Assert.True((await bea.SendPhotoAsync(await DeskThread(bea), "slip.png", Photo, "my payment slip")).Ok);
        var path = (await bea.MessagesAsync(await DeskThread(bea))).Value!.Bubbles.Last().Line.AttachmentPath!;
        Assert.StartsWith("support/u-bea/", path);

        var (_, marco) = await As("employee@demo.io", gw);
        var inbox = (await marco.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.Equal("my payment slip", inbox.LastBody);   // a caption wins over the word Photo
        Assert.True((await marco.MessagesAsync(inbox)).Value!.Bubbles.Last().ShowPhoto);
        Assert.True((await marco.SendPhotoAsync(inbox, "receipt.png", Photo, "")).Ok);

        var (_, beaAgain) = await As("bea@demo.io", gw);
        Assert.True((await beaAgain.MessagesAsync(await DeskThread(beaAgain))).Value!.Bubbles.Last().ShowPhoto);

        foreach (var who in new[] { "member@demo.io", "coach@demo.io", "carlo@demo.io" })
        {
            await As(who, gw);
            Assert.Null(await gw.SignedUrlAsync("chat-attachments", path));
        }
    }

    [Fact]
    public async Task A_failed_send_adds_no_message_and_a_missing_link_shows_as_unavailable_not_blank()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);
        var before = (await alex.MessagesAsync(thread)).Value!.Bubbles.Count;

        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await alex.SendPhotoAsync(thread, "a.png", Photo, "hi")).Error);
        gw.FailWith = null;
        Assert.Equal(before, (await alex.MessagesAsync(thread)).Value!.Bubbles.Count);

        var gone = new ChatBubble(new ChatLine { AttachmentPath = "u-coach1/u-alex/x.png" }, true, null);   // a photo was sent but no link could be made
        Assert.True(gone.PhotoMissing); Assert.False(gone.ShowPhoto);
        Assert.False(new ChatBubble(new ChatLine { Body = "hi" }, true).PhotoMissing);
    }

    [Fact]
    public void Storage_names_keep_the_folder_and_replace_odd_characters_in_the_file_part()
    {
        Assert.Equal("a/b/20261016-my_slip__1_.png", StoragePath.Safe("a/b/20261016-my slip (1).png"));
        Assert.Equal("plain.jpg", StoragePath.Safe("plain.jpg"));
    }
}
```

In `tests/GymMembership.Tests/SupabaseGatewayTests.cs`, inside the class add:

```csharp
    [Fact]
    public async Task Sending_a_photo_uploads_it_to_the_conversation_folder_then_sends_the_message_with_its_path()
    {
        var gw = await SignedInAsync();
        var svc = new ChatService(gw, new NotificationService(gw));
        _reply = s => s.Path.StartsWith("/storage/") ? (200, """{"Key":"x"}""") : (201, "[]");

        await svc.SendPhotoAsync(new ChatThread { Kind = "coach", CoachId = "c-1", MemberId = "u-1" }, "My Pic.png", [1, 2, 3], "look");
        var upload = _seen.First(s => s.Path.StartsWith("/storage/v1/object/chat-attachments/c-1/u-1/"));
        Assert.EndsWith("-My_Pic.png", upload.Path);
        Assert.Equal("/rest/v1/messages", Last.Path);
        Assert.Contains("\"attachment_path\":\"c-1/u-1/", Last.Body);
        Assert.Contains("\"body\":\"look\"", Last.Body);

        await svc.SendPhotoAsync(new ChatThread { Kind = "support", MemberId = "u-1" }, "a.jpg", [1], "");
        Assert.Contains("/storage/v1/object/chat-attachments/support/u-1/", _seen.Last(s => s.Path.StartsWith("/storage/")).Path);
        Assert.Equal("/rest/v1/rpc/send_support_message", Last.Path);
        Assert.Contains("\"p_attachment\":\"support/u-1/", Last.Body);
        Assert.Contains("\"p_body\":\"\"", Last.Body);
    }

    [Fact]
    public async Task A_page_with_a_photo_asks_for_a_signed_link_and_a_failed_link_does_not_fail_the_page()
    {
        var gw = await SignedInAsync();
        var svc = new ChatService(gw, new NotificationService(gw));
        const string rows = """[{"message_id":7,"sender_id":"c-1","body":"","sent_at":"2026-10-15T01:00:00+00:00","delivered_at":null,"read_at":null,"sender_name":null,"attachment_path":"c-1/u-1/p.png"}]""";
        var thread = new ChatThread { Kind = "coach", CoachId = "c-1", MemberId = "u-1" };

        _reply = s => s.Path.Contains("/object/sign/") ? (200, """{"signedURL":"/object/sign/chat-attachments/c-1/u-1/p.png?token=t"}""") : (200, rows);
        var bubble = (await svc.MessagesAsync(thread)).Value!.Bubbles.Single();
        Assert.Contains("token=t", bubble.PhotoUrl);
        Assert.Contains(_seen, s => s.Path == "/storage/v1/object/sign/chat-attachments/c-1/u-1/p.png");

        var svc2 = new ChatService(gw, new NotificationService(gw));   // no cached link
        _reply = s => s.Path.Contains("/object/sign/") ? (500, """{"error":"boom"}""") : (200, rows);
        var page = await svc2.MessagesAsync(thread);
        Assert.True(page.Ok);
        Assert.True(page.Value!.Bubbles.Single().PhotoMissing);
    }
```

In `MessengerViewModelTests.cs`, add to the `SlowChat` fake: `public Task<AppResult<bool>> SendPhotoAsync(ChatThread t, string fileName, byte[] data, string caption) => Task.FromResult(AppResult<bool>.Success(true));`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter "ChatPhotoTests|photo"`
Expected: build FAILS (`SendPhotoAsync`, `ShowPhoto`, `StoragePath` do not exist).

- [ ] **Step 3: Models**

In `src/GymMembership.Core/Models/Models.cs`:
- `ChatMessage`: add after `DeliveredAt`: `[Column("attachment_path", ignoreOnUpdate: true)] public string? AttachmentPath { get; set; }`
- `SupportMessage`: add `[Column("attachment_path")] public string? AttachmentPath { get; set; }`
- `ChatLine`: add `[Column("attachment_path")] public string? AttachmentPath { get; set; }`

- [ ] **Step 4: Shared helpers**

In `src/GymMembership.Core/Services/Foundation.cs` add:

```csharp
public static class StoragePath
{
    /// <summary>The folder stays; the file name keeps letters, digits, dot, dash and underscore (a camera file can hold spaces and accents the bucket refuses).</summary>
    public static string Safe(string path)
    {
        var slash = path.LastIndexOf('/');
        return path[..(slash + 1)] + System.Text.RegularExpressions.Regex.Replace(path[(slash + 1)..], @"[^A-Za-z0-9._-]", "_");
    }
}
```

In `src/GymMembership.Core/Backend/SupabaseGateway.cs` `UploadAsync`, replace the two lines that compute `slash` and `safe` with `var safe = StoragePath.Safe(path);` (the `Regex` using may become unused; remove it if the build warns).

In `src/GymMembership.Core/Services/ProfileService.cs`:
- Add inside `ProfileService`: `public static bool IsAcceptableImage(string fileName, byte[] data) => Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant()) && data.Length is > 0 and <= MaxBytes;` and use it in `SetAvatarAsync` in place of its inline check (`if (!IsAcceptableImage(fileName, data)) return AppResult<string>.Fail(AppErrorKind.Validation, "bad_image");`; keep `var ext = ...` for the file name).
- Move the signed-link cache into a small class and use it from `ProfileService.AvatarUrlAsync`:

```csharp
/// <summary>Signed links last an hour; one is reused for 50 minutes instead of asking the server on every redraw.</summary>
public sealed class LinkCache(ISupabaseGateway gw, string bucket)
{
    static readonly TimeSpan Life = TimeSpan.FromMinutes(50);
    readonly ConcurrentDictionary<string, (string Url, DateTime Until)> _links = new();

    public async Task<string?> UrlAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_links.TryGetValue(path, out var hit) && hit.Until > DateTime.UtcNow) return hit.Url;
        var url = await gw.SignedUrlAsync(bucket, path);
        if (url is not null) _links[path] = (url, DateTime.UtcNow + Life);
        return url;
    }
}
```

`ProfileService` then holds `readonly LinkCache _avatars = new(gw, "avatars");` and `AvatarUrlAsync` becomes `Safe.RunAsync<string?>(() => _avatars.UrlAsync(path))`; delete its old `_links` and `LinkLife`. The existing `ProfileTests` guard this refactor.

- [ ] **Step 5: Service**

In `src/GymMembership.Core/Services/Features.cs`:

```csharp
public sealed record ChatBubble(ChatLine Line, bool Mine, string? PhotoUrl = null)
{
    /// <summary>Shown under what you sent: Sent, Delivered or Read. Empty for what you received.</summary>
    public string Status => !Mine ? "" : Line.ReadAt is not null ? "Read" : Line.DeliveredAt is not null ? "Delivered" : "Sent";
    public bool HasText => !string.IsNullOrWhiteSpace(Line.Body);
    public bool ShowPhoto => !string.IsNullOrEmpty(PhotoUrl);
    /// <summary>A photo was sent but no link could be made for it: the bubble says so instead of leaving a blank.</summary>
    public bool PhotoMissing => Line.AttachmentPath is not null && !ShowPhoto;
}
```

Add to `IChatService`: `Task<AppResult<bool>> SendPhotoAsync(ChatThread thread, string fileName, byte[] data, string caption);`

In `ChatService`: add `readonly LinkCache photos = new(gw, "chat-attachments");` and change the page build in `MessagesAsync`:

```csharp
var shown = rows.Take(take).Reverse().ToList();
var urls = await Task.WhenAll(shown.Select(l => LinkOrNull(l.AttachmentPath)));   // one failed link must not fail the page
var bubbles = shown.Select((l, i) => new ChatBubble(l, l.SenderId == uid, urls[i])).ToList();
return new ChatPage(bubbles, rows.Count > take);
```

```csharp
async Task<string?> LinkOrNull(string? path)
{
    try { return await photos.UrlAsync(path); } catch { return null; }
}

public async Task<AppResult<bool>> SendPhotoAsync(ChatThread thread, string fileName, byte[] data, string caption)
{
    if (!ProfileService.IsAcceptableImage(fileName, data)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_image");
    var text = caption?.Trim() ?? "";
    if (text.Length > 2000) return AppResult<bool>.Fail(AppErrorKind.Validation, "message_too_long");
    return await Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var folder = thread.IsSupport ? $"support/{thread.MemberId}" : $"{thread.CoachId}/{thread.MemberId}";
        var name = Path.GetFileName(fileName); if (name.Length > 80) name = name[^80..];
        // ponytail: if the message fails after the upload, the file stays in the bucket unseen. Add cleanup when storage use matters.
        var path = await gw.UploadAsync("chat-attachments", $"{folder}/{DateTime.UtcNow:yyyyMMddHHmmssfff}-{name}", data);
        if (thread.IsSupport) await gw.RpcAsync("send_support_message", new() { ["p_member"] = thread.MemberId, ["p_body"] = text, ["p_attachment"] = path });
        else await gw.InsertAsync(new ChatMessage { CoachId = thread.CoachId!, MemberId = thread.MemberId, SenderId = uid, Body = text, AttachmentPath = path });
        return true;
    });
}
```

- [ ] **Step 6: Demo gateway**

In `src/GymMembership.Core/Demo/DemoGateway.cs`:

1. A store and the rule (demo ids look like `u-alex`, so the id pattern is wider than a uuid):

```csharp
    // ---------- chat photos (mirrors 0016: can_use_chat_file) ----------
    readonly Dictionary<string, byte[]> _files = new();
    const string FilePart = @"[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}";
    static readonly System.Text.RegularExpressions.Regex SupportFile = new(@$"^support/(?<m>[A-Za-z0-9_-]+)/{FilePart}$");
    static readonly System.Text.RegularExpressions.Regex CoachFile = new(@$"^(?<c>[A-Za-z0-9_-]+)/(?<m>[A-Za-z0-9_-]+)/{FilePart}$");

    bool CanUseChatFile(string path, bool write)
    {
        var uid = CurrentUserId;
        if (uid is null || !IsActiveUser) return false;
        if (SupportFile.Match(path) is { Success: true } s) return s.Groups["m"].Value == uid || Has("support:reply");
        if (CoachFile.Match(path) is { Success: true } c)
        {
            var coach = c.Groups["c"].Value; var member = c.Groups["m"].Value;
            if (uid != coach && uid != member) return false;
            return !write || (CoachesMember(coach, member) && ActiveMember(member));
        }
        return false;
    }
```

2. `UploadAsync` / `SignedUrlAsync` (replace the two existing one-liners; other buckets behave as before):

```csharp
    public Task<string> UploadAsync(string bucket, string path, byte[] data)
    {
        if (FailWith is not null) return Task.FromException<string>(FailWith);
        if (bucket != "chat-attachments") return Task.FromResult(path);
        path = StoragePath.Safe(path);
        if (!CanUseChatFile(path, write: true)) return Task.FromException<string>(Err(Rls));
        _files[path] = data;
        return Task.FromResult(path);
    }

    public Task<string?> SignedUrlAsync(string bucket, string path)
    {
        if (FailWith is not null) return Task.FromException<string?>(FailWith);
        if (bucket != "chat-attachments" || !CanUseChatFile(path, write: false) || !_files.TryGetValue(path, out var bytes)) return Task.FromResult<string?>(null);   // the demo keeps no other files
        // a plain file in the temp folder stands in for the signed link, so the picture shows without a server
        var file = Path.Combine(Path.GetTempPath(), $"gym-demo-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16]}{Path.GetExtension(path)}");
        if (!File.Exists(file)) File.WriteAllBytes(file, bytes);
        return Task.FromResult<string?>(file);
    }
```

3. Insert of `ChatMessage` (the `case ChatMessage c:` in `InsertAsync`): replace the blank-body check so that a photo may stand alone and must belong to this conversation:

```csharp
// the table's own checks: the sender is one of the two people, there is text or a photo, and the photo is in this conversation's folder
var photoOk = c.AttachmentPath is null || System.Text.RegularExpressions.Regex.IsMatch(c.AttachmentPath, $"^{c.CoachId}/{c.MemberId}/{FilePart}$");
if (c.SenderId != c.CoachId && c.SenderId != c.MemberId || (string.IsNullOrWhiteSpace(c.Body) && c.AttachmentPath is null) || !photoOk) throw Err("new row for relation \"messages\" violates check constraint (23514)");
```

4. Functions: in `chat_threads` build `LastBody` with a helper `static string? Preview(string? body, string? photo) => !string.IsNullOrWhiteSpace(body) ? body : photo is not null ? "Photo" : body;` and use it for the three branches (`last?.Body` becomes `last is null ? null : Preview(last.Body, last.AttachmentPath)`). In `chat_messages` add `AttachmentPath = m.AttachmentPath` to both projections. In `send_support_message`: read `p_attachment` (`Opt("p_attachment")`), allow an empty body when it is set (`if (string.IsNullOrEmpty(body) && att is null) throw Err("empty_message")`; the body is `(a["p_body"] as string)?.Trim() ?? ""`), require `att` to match `^support/{member}/{FilePart}$` else `throw Err("forbidden")` (after the who-may-write checks, like the SQL), store `AttachmentPath = att` on the `SupportMessage`, and use `Preview(body, att)` truncated to 80 characters for the alerts.

- [ ] **Step 7: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS. A failure in `ChatPhotoTests` means the demo differs from `0016`: fix the demo, not the test, unless the test contradicts `0016`.

- [ ] **Step 8: Commit**

```bash
git add src/GymMembership.Core tests/GymMembership.Tests
git commit -m "feat: send and show chat photos in the chat service and the demo

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Attaching a photo in the view model, and a demo picker

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`MessengerViewModel`)
- Create: `src/GymMembership.Core/Demo/DemoPhotoPicker.cs`
- Modify: `src/GymMembership.App/MauiProgram.cs`
- Create: `tests/GymMembership.Tests/MessengerPhotoTests.cs`
- Modify: `tests/GymMembership.Tests/MessengerViewModelTests.cs` (the helper and the three other places that construct the view model)

**Interfaces:**
- Consumes: `IChatService.SendPhotoAsync`, `ChatBubble.ShowPhoto`, `ProfileService.IsAcceptableImage`, `IProofPicker`.
- Produces: `MessengerViewModel(IChatService chat, IProfileService profile, IAuthService auth, IProofPicker picker)` with `PendingPhoto`, `HasPhoto`, `PhotoName`, `AttachCommand`, `RemovePhotoCommand`; `SendCommand` sends the chosen photo with `Draft` as its caption. `DemoPhotoPicker : IProofPicker` returns a generated PNG named `demo-photo.png`; `DemoPng.Make()` builds it.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/MessengerPhotoTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MessengerPhotoTests
{
    sealed class FakePicker : IProofPicker
    {
        public (string FileName, byte[] Data)? Next;
        public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult(Next);
    }

    static async Task<(DemoGateway Gw, FakePicker Picker, MessengerViewModel Vm)> Open(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var picker = new FakePicker { Next = ("squat.png", DemoPng.Make()) };
        var vm = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), picker);
        await vm.OnAppearingAsync();
        return (gw, picker, vm);
    }

    [Fact]
    public async Task Choosing_a_photo_shows_it_as_a_chip_and_sending_posts_it_with_the_caption_and_clears_both()
    {
        var (_, _, vm) = await Open("member@demo.io");

        await vm.AttachCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto); Assert.Equal("squat.png", vm.PhotoName);

        vm.Draft = "how is my depth?";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.HasPhoto); Assert.Equal("", vm.Draft);
        var last = vm.Bubbles.Last();
        Assert.True(last.ShowPhoto); Assert.Equal("how is my depth?", last.Line.Body); Assert.True(last.Mine);
        Assert.Equal("You: how is my depth?", vm.Threads.Single().Thread.Preview);
    }

    [Fact]
    public async Task A_photo_alone_can_be_sent_and_the_list_says_Photo()
    {
        var (_, _, vm) = await Open("member@demo.io");
        await vm.AttachCommand.ExecuteAsync(null);

        await vm.SendCommand.ExecuteAsync(null);   // no caption

        Assert.True(vm.Bubbles.Last().ShowPhoto); Assert.False(vm.Bubbles.Last().HasText);
        Assert.Equal("You: Photo", vm.Threads.Single().Thread.Preview);
    }

    [Fact]
    public async Task Remove_drops_the_chosen_photo_and_a_wrong_file_is_refused_with_a_plain_message()
    {
        var (_, picker, vm) = await Open("member@demo.io");
        await vm.AttachCommand.ExecuteAsync(null);
        vm.RemovePhotoCommand.Execute(null);
        Assert.False(vm.HasPhoto);

        picker.Next = ("notes.pdf", [1, 2, 3]);
        await vm.AttachCommand.ExecuteAsync(null);
        Assert.False(vm.HasPhoto);
        Assert.Equal("Choose a JPG, PNG or WebP picture under 5 MB.", vm.ErrorMessage);

        picker.Next = null;   // the person cancelled the picker
        vm.ErrorMessage = null;
        await vm.AttachCommand.ExecuteAsync(null);
        Assert.False(vm.HasPhoto); Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Offline_the_caption_and_the_chosen_photo_stay_and_no_bubble_appears()
    {
        var (gw, _, vm) = await Open("member@demo.io");
        var before = vm.Bubbles.Count;
        await vm.AttachCommand.ExecuteAsync(null);
        vm.Draft = "sending";
        gw.FailWith = new HttpRequestException("offline");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.True(vm.HasPhoto); Assert.Equal("sending", vm.Draft);
        Assert.Equal(before, vm.Bubbles.Count);
    }

    [Fact]
    public async Task A_chosen_photo_does_not_follow_you_into_another_conversation()
    {
        var (_, _, vm) = await Open("member@demo.io");
        await vm.AttachCommand.ExecuteAsync(null);

        await vm.ShowTabCommand.ExecuteAsync("support");   // the front desk conversation
        Assert.False(vm.HasPhoto);
        await vm.SendCommand.ExecuteAsync(null);           // nothing to send: no text and no photo
        Assert.Empty(vm.Bubbles);
    }

    [Fact]
    public async Task A_member_sends_a_photo_to_the_front_desk_and_staff_see_it()
    {
        var (gw, _, vm) = await Open("member@demo.io");
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.AttachCommand.ExecuteAsync(null);
        vm.Draft = "my slip";
        await vm.SendCommand.ExecuteAsync(null);

        await gw.SignOutAsync(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), new FakePicker());
        await desk.OnAppearingAsync();
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        Assert.True(desk.Bubbles.Last().ShowPhoto);
        Assert.Equal("my slip", desk.Bubbles.Last().Line.Body);
    }

    [Fact]
    public void The_demo_picture_is_a_real_png_the_app_accepts()
    {
        var png = DemoPng.Make();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.True(ProfileService.IsAcceptableImage("demo-photo.png", png));
        Assert.Equal("demo-photo.png", new DemoPhotoPicker().PickAsync().Result!.Value.FileName);
    }
}
```

In `tests/GymMembership.Tests/MessengerViewModelTests.cs` every `new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw))` (the `Open` helper and three tests) gets a fourth argument `new DemoPhotoPicker()`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter "MessengerPhotoTests|MessengerViewModelTests"`
Expected: build FAILS (`DemoPng`, `DemoPhotoPicker`, `AttachCommand`, the fourth constructor argument do not exist).

- [ ] **Step 3: The demo picture and picker**

Create `src/GymMembership.Core/Demo/DemoPhotoPicker.cs`:

```csharp
using System.IO.Compression;
using System.Text;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

/// <summary>Demo mode has no photo library to open, so "choose a photo" hands back a generated picture.</summary>
public sealed class DemoPhotoPicker : IProofPicker
{
    public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string, byte[])?>(("demo-photo.png", DemoPng.Make()));
}

/// <summary>A small colour-gradient PNG written by hand: signature, header, one compressed data chunk, end.</summary>
public static class DemoPng
{
    public static byte[] Make(int width = 320, int height = 200)
    {
        var raw = new byte[(width * 3 + 1) * height];   // each row starts with a filter byte (0 = none), then red, green, blue per pixel
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * (width * 3 + 1) + 1 + x * 3;
                raw[i] = (byte)(40 + 200 * x / width); raw[i + 1] = (byte)(90 + 120 * y / height); raw[i + 2] = 160;
            }
        using var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [.. Be(width), .. Be(height), 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", zipped.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(Be(data.Length)); s.Write(t); s.Write(data); s.Write(Be((int)Crc(t, data)));
    }

    static byte[] Be(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    static uint Crc(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a.Concat(b)) { c ^= x; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return ~c;
    }
}
```

In `src/GymMembership.App/MauiProgram.cs` replace `s.AddSingleton<IProofPicker, MauiProofPicker>();` with:

```csharp
		// the real photo library on a device; in demo mode a generated picture, so the screens can be tried without choosing a file
		s.AddSingleton<IProofPicker>(sp => sp.GetRequiredService<ISupabaseGateway>() is DemoGateway ? new DemoPhotoPicker() : new MauiProofPicker());
```

- [ ] **Step 4: The view model**

In `MessengerViewModel` (`src/GymMembership.Core/ViewModels/MemberViewModels.cs`):
1. Constructor: `public partial class MessengerViewModel(IChatService chat, IProfileService profile, IAuthService auth, IProofPicker picker) : LoadableViewModel`.
2. State and members:

```csharp
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPhoto), nameof(PhotoName))] private (string Name, byte[] Data)? pendingPhoto;
    public bool HasPhoto => PendingPhoto is not null;
    public string PhotoName => PendingPhoto?.Name ?? "";

    [RelayCommand]
    private async Task AttachAsync()
    {
        ErrorMessage = null;
        if (await picker.PickAsync() is not { } file) return;   // cancelled
        if (!ProfileService.IsAcceptableImage(file.FileName, file.Data)) { ErrorMessage = ErrorText.For(AppErrorKind.Validation, "bad_image"); return; }
        PendingPhoto = file;
    }

    [RelayCommand] private void RemovePhoto() => PendingPhoto = null;
```

3. In `OnSelectedChanged`, in the branch that runs when the conversation changes (after the draft is saved and restored), add `PendingPhoto = null;` (a half-chosen photo is dropped; Reset also sets it).
4. `Reset()` adds `PendingPhoto = null;`.
5. `SendAsync` becomes:

```csharp
    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is not { } item || (string.IsNullOrWhiteSpace(Draft) && PendingPhoto is null)) return;
        var sent = Draft; var photo = PendingPhoto;
        var ok = photo is { } p
            ? await RunAsync(() => chat.SendPhotoAsync(item.Thread, p.Name, p.Data, sent))
            : await RunAsync(() => chat.SendAsync(item.Thread, sent));
        if (!ok) return;   // the caption and the photo stay, with the error line
        drafts.Remove(item.Thread.Key);
        if (Selected?.Thread.Key == item.Thread.Key)
        {
            if (Draft == sent) Draft = "";
            if (PendingPhoto == photo) PendingPhoto = null;
        }
        await MergeLatestAsync(item);
        await LoadThreadsAsync(first: false);
    }
```

(`(string, byte[])?` equality compares the byte array by reference, which is what is wanted: "still the photo I sent".) In `MergeLatestAsync`, swap a message whose link changed as well as one whose status changed: `else if (old.Status != b.Status || old.PhotoUrl != b.PhotoUrl) Bubbles[Bubbles.IndexOf(old)] = b;`.

- [ ] **Step 5: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: attach a photo to a message in the messenger view model

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Photos on the Messenger screen

**Invoke the `frontend-design:frontend-design` skill first.** Keep the existing look: flat panels, hairlines, Barlow, no shadows. The photo sits at the top of the bubble with rounded corners and a hairline; the caption under it; the chosen-photo chip above the message box is a small panel with a plain word label and a Remove button.

**Files:**
- Modify: `src/GymMembership.App/Views/MessagesPage.xaml`

**Interfaces:**
- Consumes: `ChatBubble.PhotoUrl`, `ShowPhoto`, `PhotoMissing`, `HasText`; `MessengerViewModel.AttachCommand`, `RemovePhotoCommand`, `HasPhoto`, `PhotoName`, `CanSend`.

- [ ] **Step 1: The bubble**

In the bubble template, inside the bubble's `VerticalStackLayout`, between the staff sender-name label and the body label, add:

```xml
<!-- a photo, when the message has one: a fixed crop with rounded corners; if no link could be made, say so instead of leaving a gap -->
<Border IsVisible="{Binding ShowPhoto}" StrokeThickness="0" StrokeShape="RoundRectangle 10" HorizontalOptions="Start">
    <Image Source="{Binding PhotoUrl}" Aspect="AspectFill" WidthRequest="240" HeightRequest="170" SemanticProperties.Description="Photo" />
</Border>
<Label Text="Photo unavailable" Style="{StaticResource Caption}" IsVisible="{Binding PhotoMissing}">
    <Label.Triggers>
        <DataTrigger TargetType="Label" Binding="{Binding Mine}" Value="True">
            <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource ChalkDark}}" />
        </DataTrigger>
    </Label.Triggers>
</Label>
```

and give the body label `IsVisible="{Binding HasText}"` so a photo without a caption shows no empty line.

- [ ] **Step 2: The message box**

Replace the composer `Grid` (row 3, visible when `CanSend`) with a two-row grid: row 0 is the chosen-photo chip, row 1 the usual box with a Photo button on its left:

```xml
<Grid Grid.Row="3" RowDefinitions="Auto,Auto" RowSpacing="8" Padding="16,10,16,14" IsVisible="{Binding CanSend}">
    <Border Style="{StaticResource Panel}" Padding="12,8" IsVisible="{Binding HasPhoto}">
        <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
            <Label Text="{Binding PhotoName, StringFormat='Photo ready to send: {0}'}" VerticalOptions="Center" LineBreakMode="MiddleTruncation" />
            <Button Grid.Column="1" Text="Remove" Style="{StaticResource SmallOutlineButton}" Command="{Binding RemovePhotoCommand}" />
        </Grid>
    </Border>
    <Grid Grid.Row="1" ColumnDefinitions="Auto,*,Auto" ColumnSpacing="10">
        <Button Text="Photo" Style="{StaticResource SmallOutlineButton}" Command="{Binding AttachCommand}" VerticalOptions="Center"
                SemanticProperties.Description="Choose a photo to send" />
        <Border Grid.Column="1" Style="{StaticResource Field}">
            <Entry Text="{Binding Draft}" Placeholder="Write a message" MaxLength="2000" ReturnCommand="{Binding SendCommand}" SemanticProperties.Description="Message" />
        </Border>
        <Button Grid.Column="2" Text="Send" Command="{Binding SendCommand}" />
    </Grid>
</Grid>
```

The read-only label in row 3 stays as it is.

- [ ] **Step 3: Build and run the tests**

```bash
dotnet test tests/GymMembership.Tests
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
dotnet build src/GymMembership.App -f net10.0-android
```
Expected: all pass (`XamlBindingTests` reads the new bindings), both builds succeed.

- [ ] **Step 4: Look at it (demo mode; Windows and the Android emulator)**

Move `src/GymMembership.App/Resources/Raw/supabase.json` to `supabase.json.bak` for the check and put it back afterwards. Windows: run `bin\Debug\net10.0-windows10.0.19041.0\win-x64\GymMembership.App.exe`; Android: build with `-p:EmbedAssembliesIntoApk=true`, uninstall the old app first, emulator `pixel_7_-_api_36_0` (Android is at `C:\Program Files (x86)\Android\android-sdk`; the first launch after boot takes about 45 seconds).

Check:
- **Member (Alex):** open Lena's chat, press **Photo**: the chip "Photo ready to send: demo-photo.png" appears with Remove. Type a caption, Send: the bubble shows the picture (rounded, 240 wide) with the caption under it and "Sent". Press Photo and Send with no caption: a picture-only bubble, and the list row reads "You: Photo".
- **Coach (Lena):** sign in and open the same chat: Alex's photo bubble is on the left with the picture. Reply with a photo.
- **Front desk (Bea on the Staff tab, then Marco):** Bea sends a photo; Marco's inbox row reads "Photo" or the caption, the chat shows the picture, and Marco can reply with a photo; Bea sees it as "Front desk".
- **Remove** clears the chip. Switching conversations clears it.
- If the demo picture shows as "Photo unavailable" on Android only, that is the demo's temp-file link not loading on that platform; record it as a ruling and check the picture on Windows (the real project serves https links).
- Light and dark both look right; the bubble on the phone is not wider than the screen.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: photos in chat bubbles and a photo button in the message box

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

(`git status` must not list `supabase.json.bak` or `docs/screenshotsforexample/`.)

---

### Task 5: Wrap-up

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-role-redesign-design.md`

- [ ] **Step 1: Record what was built in the spec**

In section 4, after the "Built in phase 4a" sentence, add: `**Built in phase 4b:** migration 0016 (private chat-attachments bucket, attachment_path on messages and support_messages with a "text or photo" rule and a check that the photo belongs to that conversation, can_use_chat_file and its two storage policies, and chat_threads, chat_messages and send_support_message updated); one photo per message with an optional caption in coach chats and the front desk thread, both directions; the list says "Photo" for a photo-only message; guests and ended hires can still read old photos; staff and admins cannot read coach chat photos. Photos come from the gallery picker; tap-to-enlarge is not built.`

- [ ] **Step 2: Full verification**

```bash
dotnet test tests/GymMembership.Tests
npm --prefix supabase/tests/local test
dotnet build src/GymMembership.App -f net10.0-android
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
```
Expected: every test passes (report the counts), 102 database checks pass, both builds succeed.

- [ ] **Step 3: Review**

Run `ponytail:ponytail-review` on `git diff role-redesign-phase4a...HEAD`, apply what is worth applying, re-run Step 2. Then one fresh whole-branch review of the same range (review package, most capable model, the plan's Review Focus verbatim), with extra attention to `0016`: the access function and both storage policies (who can read or add what, including admins, staff and a member whose name or id resembles a path), the two replaced functions (`chat_messages` return order, `send_support_message` with and without the new argument, the old two-argument call), the new check constraints (a photo path from another conversation), `can_use_chat_file` on odd names (`..`, trailing slash, uppercase, 121-character names), whether any function is callable by a user it should refuse, and the app paths that leave a file in the bucket after a failed message.

- [ ] **Step 4: Commit and hand over**

```bash
git add docs/superpowers/specs/2026-10-15-role-redesign-design.md
git commit -m "docs: phase 4b recorded in the redesign spec

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Stop here. Do not push or merge. Tell the owner: branch `role-redesign-phase4b` (on top of the unmerged `role-redesign-phase4a`) is ready; **`npx supabase db push` must run for `0012` to `0016`, in that order**, before photos work against the real project; plan 4c (Realtime, typing, online status) is next.
