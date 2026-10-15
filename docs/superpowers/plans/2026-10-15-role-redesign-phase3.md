# Role Redesign, Phase 3: Amenity Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. UI task (4): invoke the `frontend-design:frontend-design` skill first. Finish with `ponytail:ponytail-review` (CLAUDE.md).

**Goal:** Members and coaches see every amenity as a card with a live status (Available, In use, Unavailable), the reason and an estimated return time when it is unavailable. Members can save an amenity to be alerted when its status changes. Front desk and admin mark an amenity's status from an "Amenity Status" page and see exactly which members were alerted. The "Buy" flow is gone from view.

**Architecture:** One additive migration (`0014`): status columns on `amenities` that only a new function can write, a small `amenity_follows` table, the function `set_amenity_status` (changes the status, writes the audit log, alerts followers, returns who was alerted) and `set_amenity_followed`, plus a new permission. The app gets a board for members and coaches, a management page for staff, a shared card control, and the new chip statuses. The old purchase tables and functions stay in the database untouched.

**Tech Stack:** Supabase Postgres (embedded-Postgres local tests), .NET 10 MAUI (Windows + Android), CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 3 of section 4; database items 3.1 to 3.3).

## Database changes in this phase (the owner asked to be told)

| Change | Kind |
| --- | --- |
| New enum `amenity_status` (`available`, `in_use`, `unavailable`) | additive |
| `amenities`: new columns `status` (default `available`), `status_reason`, `back_at`, `status_changed_by`, `status_changed_at`, with a check that a reason or return time exists only while `unavailable` and the reason is at most 200 characters | additive |
| `amenities` update and insert rights narrowed to the existing editable columns (name, description, price, duration, is_active, image_path), so the status columns can only be written by the function | tightening, no app code writes them directly |
| **New table** `amenity_follows` (member, amenity), readable only by its owner, written only through a function | additive |
| New functions `set_amenity_status(...)` and `set_amenity_followed(...)` | additive |
| New permission `amenities:set_status` for admin and employee | reference data |
| New value `amenity` for the `notification_type` enum | additive |

Nothing is dropped or rewritten. The old purchase flow (`user_amenities`, `amenity_usages`, `avail_amenity`, `verify_amenity_usage`) stays, unused by any screen. **`0012` and `0013` are not on the real Supabase project yet; push them first, then `0014`.**

## Global Constraints

- Migrations are additive. `0014` may be edited in place until the owner pushes it.
- Status is written only by `set_amenity_status` (permission `amenities:set_status`, an audit row, and the alerts). No screen and no model update writes the status columns.
- Colors and type come from the existing tokens; WCAG AA must hold (`ContrastTests` stays green). Chip colors keep their meaning: green is good, yellow is waiting, red is bad, and a status always shows its word too.
- Trim-safe bindings only: no `RelativeSource AncestorType` command bindings; list buttons use `Clicked` handlers through `BasePage.Row<TVm>`. Controls update their children from `BindableProperty` change callbacks, like `StatusChip` and `Avatar`.
- Every new Core behavior has an xUnit test against `DemoGateway`; every new database rule has a test in `supabase/tests/local/rules.mjs`; every new request shape is checked in `SupabaseGatewayTests`.
- No Realtime yet (phase 4). The pages refresh when they open and every 30 seconds while open.
- Never push or merge without the owner saying so. Never commit `docs/screenshotsforexample/` or `supabase.json.bak`. `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo-mode checks and put it back.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Decisions made while planning (change any and this plan changes)

| Question | Decision |
| --- | --- |
| Who is alerted | Followers (saved) only, when the amenity becomes **available** or **unavailable**. A change to **in use** alerts nobody (it flips all day). Inactive (deactivated) members are skipped. Nothing is sent if nothing changed. |
| Who sets status | Anyone with `amenities:set_status`: admin and front desk. Coaches and members read only. |
| Card visual | A round monogram (the existing `Avatar` control with the amenity's initials) now; amenity photos arrive with the image upload in phase 6, because no screen can set `image_path` yet. |
| Return time | Optional, local time on screen, stored as a timestamp, must be in the future. |
| Staff confirmation | After saving, a green notice names the members alerted (first five, then "and N more"), or says nobody had saved it, or says nothing changed. |
| Old screens | `VerifyAmenityUsagesPage`, its view model and the old service methods stay in the code (tests use the service), but nothing links to them. The member "Buy" / "Log a visit" buttons are gone. |
| Coach access | A new "Amenities" menu item shows the same board with no save button. |
| Live | Refresh on open and every 30 seconds while the page is open. |

## Review Focus

Failure modes the happy path will not show. Each has a test in the task named.

1. A 201-character reason, or a return time in the past: refused by the database and by the screen, with a plain message. [Task 1, Task 3]
2. A follower who was deactivated, or who has since un-saved the amenity, is not alerted and not listed. [Task 1, Task 2]
3. Saving the same status again alerts nobody and says "Nothing changed". [Task 1, Task 3]
4. Offline while saving: an error line, the editor stays open with what was typed, nothing is claimed as sent. [Task 3]
5. A member, a coach, or an admin editing the catalog cannot write the status columns directly. [Task 1, Task 2]

---

### Task 1: Migration `0014` and its database tests

**Files:**
- Create: `supabase/migrations/0014_amenity_status.sql`
- Modify: `supabase/tests/local/rules.mjs` (append above the final summary line)

**Interfaces:**
- Produces: `amenities.status`, `status_reason`, `back_at`, `status_changed_by`, `status_changed_at`; table `amenity_follows (member_id, amenity_id, created_at)`; `public.set_amenity_status(p_amenity_id bigint, p_status public.amenity_status, p_reason text default null, p_back_at timestamptz default null) returns table (member_id uuid, full_name text)` (the members alerted); `public.set_amenity_followed(p_amenity_id bigint, p_follow boolean) returns void`; permission `amenities:set_status`; notification type `amenity`.

- [ ] **Step 0: Create the branch**

```bash
git checkout role-redesign-phase2 && git checkout -b role-redesign-phase3
```

- [ ] **Step 1: Write the failing tests**

Append to `supabase/tests/local/rules.mjs` above `console.log(\`\n${pass} passed, ${failed} failed\`);`. The earlier sections define `admin`, `desk` (employee), `coach`, `alex`, `bea` (members), the helpers `run`, `code`, `test`, and an amenity named `Boxing`.

```js
console.log('\namenity status (0014)');
const boxing = (await db.query(`select amenity_id from amenities where name = 'Boxing'`)).rows[0].amenity_id;
const statusOf = async () => (await db.query(`select status, status_reason, back_at from amenities where amenity_id = $1`, [boxing])).rows[0];
const setStatus = (uid, status, reason = null, back = null) => run(uid, `select * from set_amenity_status($1, $2, $3, $4)`, [boxing, status, reason, back]);
const alertsFor = async uid => (await db.query(`select title, body, type from user_notifications where user_id = $1 and type = 'amenity' order by notification_id`, [uid])).rows;
const hours = n => new Date(Date.now() + n * 3600e3).toISOString();

await test('a new amenity is available with no reason or return time', async () => {
  const s = await statusOf();
  assert.equal(s.status, 'available'); assert.equal(s.status_reason, null); assert.equal(s.back_at, null);
});
await test('a member can save and un-save an amenity, once; a coach cannot', async () => {
  await run(alex, `select set_amenity_followed($1, true)`, [boxing]);
  await run(alex, `select set_amenity_followed($1, true)`, [boxing]);   // twice is fine
  assert.equal((await run(alex, `select * from amenity_follows`)).rowCount, 1);
  assert.equal((await run(bea, `select * from amenity_follows`)).rowCount, 0);   // nobody else's list is visible
  assert.match(await code(run(coach, `select set_amenity_followed($1, true)`, [boxing])), /42501/);
  assert.match(await code(run(alex, `select set_amenity_followed($1, true)`, [999999])), /22023/);
});
await test('front desk marks it unavailable: status, reason and return time are saved, the audit log has it, and only the follower is alerted and listed', async () => {
  const back = hours(3);
  const told = (await setStatus(desk, 'unavailable', '  Cleaning  ', back)).rows;
  assert.deepEqual(told.map(r => r.full_name), ['Alex']);
  const s = await statusOf();
  assert.equal(s.status, 'unavailable'); assert.equal(s.status_reason, 'Cleaning'); assert.ok(s.back_at);
  const a = await alertsFor(alex);
  assert.equal(a.length, 1); assert.match(a[0].title, /Boxing is unavailable/); assert.match(a[0].body, /Cleaning/);
  assert.equal((await alertsFor(bea)).length, 0);
  assert.equal((await db.query(`select 1 from audit_log where action = 'amenity.status' and entity_id = $1`, [String(boxing)])).rowCount, 1);
});
await test('saving the same thing again changes nothing and alerts nobody', async () => {
  const before = (await alertsFor(alex)).length;
  const s = await statusOf();
  assert.equal((await setStatus(desk, 'unavailable', 'Cleaning', s.back_at.toISOString())).rowCount, 0);
  assert.equal((await alertsFor(alex)).length, before);
});
await test('"in use" alerts nobody; "available" alerts the follower and clears the reason and return time', async () => {
  const before = (await alertsFor(alex)).length;
  assert.equal((await setStatus(desk, 'in_use', 'ignored')).rowCount, 0);
  assert.equal((await statusOf()).status_reason, null);                       // a reason only belongs to "unavailable"
  assert.equal((await alertsFor(alex)).length, before);
  assert.equal((await setStatus(desk, 'available')).rowCount, 1);
  const a = await alertsFor(alex);
  assert.equal(a.length, before + 1); assert.match(a.at(-1).title, /Boxing is available/);
});
await test('a 201-character reason and a return time in the past are refused', async () => {
  assert.match(await code(setStatus(desk, 'unavailable', 'x'.repeat(201))), /22023/);
  assert.match(await code(setStatus(desk, 'unavailable', 'Cleaning', hours(-1))), /22023/);
  assert.equal((await statusOf()).status, 'available');
});
await test('a deactivated follower and one who un-saved are not alerted and not listed', async () => {
  await run(bea, `select set_amenity_followed($1, true)`, [boxing]);
  await db.query(`update profiles set is_active = false where id = $1`, [bea]);
  try {
    const told = (await setStatus(desk, 'unavailable', 'Repairs')).rows;
    assert.deepEqual(told.map(r => r.full_name), ['Alex']);
  } finally { await db.query(`update profiles set is_active = true where id = $1`, [bea]); }
  await run(alex, `select set_amenity_followed($1, false)`, [boxing]);
  const told2 = (await setStatus(desk, 'available')).rows;
  assert.deepEqual(told2.map(r => r.full_name), ['Bea']);   // Alex un-saved, Bea is active again
});
await test('members and coaches cannot set a status; admin can', async () => {
  assert.match(await code(setStatus(alex, 'unavailable')), /42501/);
  assert.match(await code(setStatus(coach, 'unavailable')), /42501/);
  assert.equal(await code(setStatus(admin, 'in_use')), 'ok');
  await setStatus(admin, 'available');
});
await test('the status columns cannot be written directly, but the catalog can still be edited', async () => {
  assert.match(await code(run(admin, `update amenities set status = 'unavailable' where amenity_id = $1`, [boxing])), /42501/);
  assert.match(await code(run(admin, `insert into amenities (name, status) values ('Steam', 'unavailable')`)), /42501/);
  assert.equal(await code(run(admin, `update amenities set description = 'Ring' where amenity_id = $1`, [boxing])), 'ok');
  assert.equal(await code(run(admin, `insert into amenities (name, price) values ('Steam', 100)`)), 'ok');
});
await test('the amenity alert type and permission exist', async () => {
  assert.equal((await db.query(`select 'amenity'::notification_type as t`)).rows[0].t, 'amenity');
  const p = await db.query(`select r.name from role_permissions rp join roles r using (role_id) join permissions p using (permission_id) where p.resource = 'amenities' and p.action = 'set_status' order by r.name`);
  assert.deepEqual(p.rows.map(r => r.name), ['admin', 'employee']);
});
```

- [ ] **Step 2: Run to verify they fail**

Run: `npm --prefix supabase/tests/local test`
Expected: the new tests FAIL (`column "status" does not exist`, `function set_amenity_followed ... does not exist`); every earlier test still passes (66).

- [ ] **Step 3: Write the migration**

Create `supabase/migrations/0014_amenity_status.sql`:

```sql
-- 0014: live amenity status, saved amenities and their alerts (role redesign, phase 3)
--
-- An amenity is now available, in use or unavailable. Only set_amenity_status can change that: it checks the permission,
-- writes the audit log and alerts the members who saved the amenity. The old purchase tables and functions are not touched.

create type public.amenity_status as enum ('available', 'in_use', 'unavailable');

-- the alert type; it is not used anywhere in this file, so adding it inside this transaction is fine
alter type public.notification_type add value if not exists 'amenity';

alter table public.amenities
	add column status            public.amenity_status not null default 'available',
	add column status_reason     text,
	add column back_at           timestamptz,
	add column status_changed_by uuid references public.profiles (id),
	add column status_changed_at timestamptz,
	add constraint amenities_status_detail check (
		(status = 'unavailable' or (status_reason is null and back_at is null))
		and (status_reason is null or char_length(status_reason) <= 200)
	);

comment on column public.amenities.status is 'Live status. Written only by set_amenity_status';
comment on column public.amenities.status_reason is 'Why it is unavailable (maintenance, cleaning, closed ...); only while unavailable';
comment on column public.amenities.back_at is 'When it is expected back; only while unavailable';

-- the status columns are no longer writable from the app: catalog edits keep the columns they always had
revoke insert, update on public.amenities from authenticated;
grant insert (name, description, price, duration_days, is_active, image_path) on public.amenities to authenticated;
grant update (name, description, price, duration_days, is_active, image_path) on public.amenities to authenticated;

-- members who saved an amenity to hear when it changes
create table public.amenity_follows (
	member_id  uuid        not null references public.members (user_id) on delete cascade,
	amenity_id bigint      not null references public.amenities (amenity_id) on delete cascade,
	created_at timestamptz not null default now(),
	primary key (member_id, amenity_id)
);

create index amenity_follows_amenity_id_idx on public.amenity_follows (amenity_id);

alter table public.amenity_follows enable row level security;
revoke all on public.amenity_follows from anon, authenticated;
grant select on public.amenity_follows to authenticated;
create policy amenity_follows_select on public.amenity_follows for select to authenticated
	using (public.is_active_user() and member_id = auth.uid());

-- save or un-save an amenity (members only; saving twice is fine)
create function public.set_amenity_followed(p_amenity_id bigint, p_follow boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	if not exists (select 1 from public.members where user_id = auth.uid()) then perform public.fail('forbidden', '42501'); end if;

	if p_follow then
		if not exists (select 1 from public.amenities where amenity_id = p_amenity_id and is_active) then perform public.fail('not_found', '22023'); end if;
		insert into public.amenity_follows (member_id, amenity_id) values (auth.uid(), p_amenity_id) on conflict do nothing;
	else
		delete from public.amenity_follows where member_id = auth.uid() and amenity_id = p_amenity_id;
	end if;
end;
$$;

-- Change an amenity's status. Returns the members who were alerted, so the screen can show who was told.
create function public.set_amenity_status(p_amenity_id bigint, p_status public.amenity_status, p_reason text default null, p_back_at timestamptz default null)
returns table (member_id uuid, full_name text)
language plpgsql security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
	v public.amenities;
	v_reason text := nullif(btrim(p_reason), '');
	v_title text;
	v_body text;
begin
	perform public.require_permission('amenities:set_status');

	select * into v from public.amenities where amenity_id = p_amenity_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;

	if p_status <> 'unavailable' then v_reason := null; p_back_at := null; end if;   -- these only belong to "unavailable"
	if char_length(coalesce(v_reason, '')) > 200 then perform public.fail('bad_input', '22023'); end if;
	if p_back_at is not null and p_back_at <= now() then perform public.fail('bad_input', '22023'); end if;

	-- nothing changed: change nothing, tell nobody
	if v.status = p_status and v.status_reason is not distinct from v_reason and v.back_at is not distinct from p_back_at then return; end if;

	update public.amenities
	set status = p_status, status_reason = v_reason, back_at = p_back_at, status_changed_by = auth.uid(), status_changed_at = now()
	where amenity_id = p_amenity_id;

	perform public.audit('amenity.status', 'amenities', p_amenity_id::text,
		jsonb_build_object('from', v.status, 'to', p_status, 'reason', v_reason, 'back_at', p_back_at));

	if p_status = 'in_use' then return; end if;   -- "in use" changes all day: not worth an alert

	if p_status = 'available' then
		v_title := v.name || ' is available';
		v_body := null;
	else
		v_title := v.name || ' is unavailable';
		v_body := concat_ws('. ', v_reason,
			case when p_back_at is not null then 'Back about ' || to_char(p_back_at at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM') end);
	end if;

	return query
		with told as (
			insert into public.user_notifications (user_id, type, title, body, reference)
			select f.member_id, 'amenity', v_title, v_body, jsonb_build_object('amenity_id', p_amenity_id)
			from public.amenity_follows f
			join public.profiles p on p.id = f.member_id and p.is_active
			where f.amenity_id = p_amenity_id
			returning user_id
		)
		select t.user_id, pr.full_name from told t join public.profiles pr on pr.id = t.user_id order by pr.full_name;
end;
$$;

-- who may set a status: admin and front desk
insert into public.permissions (resource, action, description) values
	('amenities', 'set_status', 'Mark an amenity available, in use or unavailable');

insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p on p.resource = 'amenities' and p.action = 'set_status'
where r.name in ('admin', 'employee');

revoke all on function public.set_amenity_followed(bigint, boolean) from public, anon, authenticated;
revoke all on function public.set_amenity_status(bigint, public.amenity_status, text, timestamptz) from public, anon, authenticated;
grant execute on function public.set_amenity_followed(bigint, boolean) to authenticated;
grant execute on function public.set_amenity_status(bigint, public.amenity_status, text, timestamptz) to authenticated;
```

- [ ] **Step 4: Run to verify they pass**

Run: `npm --prefix supabase/tests/local test`
Expected: `ok   0014_amenity_status.sql` during setup, then every test passes (66 earlier + 9 new = 75). If a test fails, read the failure before touching the migration: the likely suspects are the variable-name clash inside `set_amenity_status` (the `#variable_conflict use_column` line handles it), the enum value `amenity` used before commit (it must not appear in any statement outside a function body), and the one-row `Alex`/`Bea` names (`full_name` comes from the sign-up trigger in the earlier test setup).

- [ ] **Step 5: Commit**

```bash
git add supabase/migrations/0014_amenity_status.sql supabase/tests/local/rules.mjs
git commit -m "feat(db): live amenity status, saved amenities and their alerts (0014)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Models, service and demo gateway

**Files:**
- Modify: `src/GymMembership.Core/Models/Models.cs`, `src/GymMembership.Core/Services/Services.cs` (`IAmenityService`, `AmenityService`, new records), `src/GymMembership.Core/Demo/DemoGateway.cs`
- Create: `tests/GymMembership.Tests/AmenityStatusTests.cs`
- Modify: `tests/GymMembership.Tests/SupabaseGatewayTests.cs`

**Interfaces:**
- Produces:
  - `Amenity.Availability : string` (column `status`), `StatusReason : string?`, `BackAt : DateTime?`, all read-only to writes (`ignoreOnInsert`, `ignoreOnUpdate`).
  - `AmenityFollow : BaseModel` (read-only; `AmenityId`, `MemberId`).
  - `record NotifiedMember(string MemberId, string Name)` (JSON `member_id`, `full_name`).
  - `record AmenityCard(Amenity Amenity, bool Followed, bool CanFollow)` with `Availability`, `Reason`, `BackAt`, `HasReason`, `HasBackAt`, `IsUnavailable`, `FollowText` (`"Following"`, `"Notify me"`, or empty when `CanFollow` is false, which hides the button).
  - `record AmenityBoard(IReadOnlyList<AmenityCard> Cards, bool CanFollow)`.
  - `IAmenityService`: `Task<AppResult<AmenityBoard>> BoardAsync()`, `Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow)`, `Task<AppResult<IReadOnlyList<NotifiedMember>>> SetStatusAsync(int amenityId, string status, string? reason, DateTime? backAtUtc)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/AmenityStatusTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class AmenityStatusTests
{
    static async Task<(DemoGateway Gw, AmenityService Svc)> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new AmenityService(gw));
    }

    [Fact]
    public async Task The_board_shows_active_amenities_with_status_and_what_the_member_saved()
    {
        var (_, svc) = await As("member@demo.io");
        var board = (await svc.BoardAsync()).Value!;

        Assert.True(board.CanFollow);
        Assert.DoesNotContain(board.Cards, c => c.Amenity.Name == "Boxing ring");   // switched off in the catalog: not shown
        var sauna = board.Cards.First(c => c.Amenity.Name == "Sauna");
        Assert.Equal("unavailable", sauna.Availability);   // the demo's cleaning break
        Assert.False(string.IsNullOrWhiteSpace(sauna.Reason));
        Assert.NotNull(sauna.BackAt);
        Assert.True(sauna.Followed);
        Assert.Equal("Following", sauna.FollowText);
        Assert.Equal("Notify me", board.Cards.First(c => c.Amenity.Name == "Group classes").FollowText);
        Assert.Equal(board.Cards.Select(c => c.Amenity.Name).Order(), board.Cards.Select(c => c.Amenity.Name));
    }

    [Fact]
    public async Task A_coach_sees_the_board_but_cannot_save_amenities()
    {
        var (_, svc) = await As("coach@demo.io");
        Assert.False((await svc.BoardAsync()).Value!.CanFollow);
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.SetFollowedAsync(1, true)).Error);
    }

    [Fact]
    public async Task Saving_and_un_saving_an_amenity_is_idempotent_and_shows_on_the_board()
    {
        var (_, svc) = await As("member@demo.io");
        var classes = (await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Group classes");

        Assert.True((await svc.SetFollowedAsync(classes.Amenity.Id, true)).Ok);
        Assert.True((await svc.SetFollowedAsync(classes.Amenity.Id, true)).Ok);
        Assert.True((await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == classes.Amenity.Id).Followed);

        Assert.True((await svc.SetFollowedAsync(classes.Amenity.Id, false)).Ok);
        Assert.False((await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == classes.Amenity.Id).Followed);
    }

    [Fact]
    public async Task Front_desk_changes_a_status_and_learns_who_was_alerted_and_the_board_shows_it()
    {
        var (_, desk) = await As("employee@demo.io");
        var pool = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");

        var told = (await desk.SetStatusAsync(pool.Amenity.Id, "unavailable", "  Maintenance ", DateTime.UtcNow.AddHours(2))).Value!;

        Assert.Equal(["Alex Tan"], told.Select(t => t.Name));   // the demo member who saved the pool
        var after = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == pool.Amenity.Id);
        Assert.Equal("unavailable", after.Availability);
        Assert.Equal("Maintenance", after.Reason);
        Assert.True(after.HasBackAt);
    }

    [Fact]
    public async Task The_alert_lands_in_the_followers_alerts_and_not_in_anyone_elses()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new AmenityService(gw);
        var pool = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");
        await desk.SetStatusAsync(pool.Amenity.Id, "unavailable", "Maintenance", DateTime.UtcNow.AddHours(2));

        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex saved the pool
        var alex = (await new NotificationService(gw).ListAsync()).Value!.Where(n => n.Type == "amenity").ToList();
        Assert.Contains(alex, n => n.Title == "Pool is unavailable" && n.Body!.Contains("Maintenance"));

        await gw.SignOutAsync(); await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);   // Bea saved nothing
        Assert.DoesNotContain((await new NotificationService(gw).ListAsync()).Value!, n => n.Type == "amenity");
    }

    [Fact]
    public async Task The_same_status_again_alerts_nobody_and_in_use_alerts_nobody()
    {
        var (_, desk) = await As("employee@demo.io");
        var pool = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");

        Assert.Empty((await desk.SetStatusAsync(pool.Amenity.Id, "available", null, null)).Value!);   // already available
        Assert.Empty((await desk.SetStatusAsync(pool.Amenity.Id, "in_use", "ignored", null)).Value!);
        Assert.Null((await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == pool.Amenity.Id).Reason);   // a reason belongs to "unavailable" only
    }

    [Fact]
    public async Task Members_cannot_set_a_status_and_bad_input_is_refused_before_anything_changes()
    {
        var (_, member) = await As("member@demo.io");
        Assert.Equal(AppErrorKind.Unauthorized, (await member.SetStatusAsync(1, "unavailable", null, null)).Error);

        var (_, desk) = await As("employee@demo.io");
        Assert.Equal(AppErrorKind.Validation, (await desk.SetStatusAsync(1, "broken", null, null)).Error);
        Assert.Equal(AppErrorKind.Validation, (await desk.SetStatusAsync(1, "unavailable", new string('x', 201), null)).Error);
        Assert.Equal(AppErrorKind.Validation, (await desk.SetStatusAsync(1, "unavailable", "Cleaning", DateTime.UtcNow.AddMinutes(-5))).Error);
    }

    [Fact]
    public async Task Someone_who_un_saved_is_not_told_and_not_listed()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var member = new AmenityService(gw);
        var pool = (await member.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");
        await member.SetFollowedAsync(pool.Amenity.Id, false);

        await gw.SignOutAsync(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var told = (await new AmenityService(gw).SetStatusAsync(pool.Amenity.Id, "unavailable", "Repairs", null)).Value!;
        Assert.Empty(told);
    }

    [Fact]
    public async Task A_deactivated_member_who_saved_an_amenity_is_not_told()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await gw.RpcAsync("set_user_active", new() { ["p_user"] = "u-alex", ["p_active"] = false });

        await gw.SignOutAsync(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var svc = new AmenityService(gw);
        var pool = (await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");
        Assert.Empty((await svc.SetStatusAsync(pool.Amenity.Id, "unavailable", "Repairs", null)).Value!);
    }
}
```

Append to `tests/GymMembership.Tests/SupabaseGatewayTests.cs` inside the class:

```csharp
    [Fact]
    public async Task Editing_an_amenity_in_the_catalog_never_sends_the_status_columns()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, "[]");
        await gw.UpdateAsync(new Amenity { Id = 4, Name = "Pool", Price = 300, IsActive = true, Availability = "unavailable", StatusReason = "x", BackAt = DateTime.UtcNow });
        var columns = Columns(Last.Body);
        Assert.DoesNotContain("status", columns);
        Assert.DoesNotContain("status_reason", columns);
        Assert.DoesNotContain("back_at", columns);
        Assert.Contains("amenity_id=eq.4", Last.Query);
    }

    [Fact]
    public async Task Setting_a_status_calls_the_function_and_reads_back_who_was_alerted()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """[{"member_id":"u-9","full_name":"Alex Tan"}]""");
        var told = (await new AmenityService(gw).SetStatusAsync(4, "unavailable", "Cleaning", new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc))).Value!;

        Assert.Contains("rpc/set_amenity_status", Last.Path);
        var body = Last.Body;
        Assert.Contains("\"p_amenity_id\":4", body);
        Assert.Contains("\"p_status\":\"unavailable\"", body);
        Assert.Contains("\"p_reason\":\"Cleaning\"", body);
        Assert.Contains("2026-10-20T02:00:00", body);
        Assert.Equal("u-9", told[0].MemberId);
        Assert.Equal("Alex Tan", told[0].Name);
    }
```

(If `Last.Path` is not what the test class calls the request URL, use whatever the neighbouring tests assert for an RPC call; read two of the existing RPC tests first and copy their style. Also add `Assert.All(coachBoard.Cards, c => Assert.Equal("", c.FollowText));` to `A_coach_sees_the_board_but_cannot_save_amenities` (assign its `BoardAsync` result to `coachBoard` first): a coach's cards have no button text.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter "AmenityStatusTests|status_columns|who_was_alerted"`
Expected: build FAILS (`BoardAsync`, `Availability`, `NotifiedMember` ... do not exist).

- [ ] **Step 3: Models**

In `src/GymMembership.Core/Models/Models.cs` replace the `Amenity` class with:

```csharp
[Table("amenities")] public class Amenity : BaseModel
{ [PrimaryKey("amenity_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int? DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true;
  // live status: written only by set_amenity_status, so a catalog edit never sends these
  [Column("status", ignoreOnInsert: true, ignoreOnUpdate: true)] public string Availability { get; set; } = "available";
  [Column("status_reason", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? StatusReason { get; set; }
  [Column("back_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? BackAt { get; set; } }

// read only: a member's saved amenities change through set_amenity_followed
[Table("amenity_follows")] public class AmenityFollow : BaseModel
{ [PrimaryKey("amenity_id")] public int AmenityId { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; }

public record NotifiedMember([property: Newtonsoft.Json.JsonProperty("member_id")] string MemberId, [property: Newtonsoft.Json.JsonProperty("full_name")] string Name);
```

- [ ] **Step 4: Service**

In `src/GymMembership.Core/Services/Services.cs`, add before `public interface IAmenityService`:

```csharp
public sealed record AmenityCard(Amenity Amenity, bool Followed, bool CanFollow)
{
    public string Availability => Amenity.Availability;
    public bool IsUnavailable => Amenity.Availability == "unavailable";
    public string? Reason => Amenity.StatusReason;
    public DateTime? BackAt => Amenity.BackAt;
    public bool HasReason => !string.IsNullOrWhiteSpace(Amenity.StatusReason);
    public bool HasBackAt => IsUnavailable && Amenity.BackAt is not null;
    // empty hides the button: coaches and staff can look but not save
    public string FollowText => !CanFollow ? "" : Followed ? "Following" : "Notify me";
}

public sealed record AmenityBoard(IReadOnlyList<AmenityCard> Cards, bool CanFollow);
```

Add to `IAmenityService`:

```csharp
    /// <summary>Every amenity that is switched on, with its live status and whether this member saved it. CanFollow is false for coaches and staff.</summary>
    Task<AppResult<AmenityBoard>> BoardAsync();
    Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow);
    /// <summary>Front desk and admin only. Returns the members who were alerted (empty when nothing changed or nobody saved it).</summary>
    Task<AppResult<IReadOnlyList<NotifiedMember>>> SetStatusAsync(int amenityId, string status, string? reason, DateTime? backAtUtc);
```

Add to `AmenityService`:

```csharp
    static readonly string[] Statuses = ["available", "in_use", "unavailable"];

    public Task<AppResult<AmenityBoard>> BoardAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var amenities = (await gw.ListAsync<Amenity>(a => a.IsActive == true)).OrderBy(a => a.Name).ToList();
        var saved = (await gw.ListAsync<AmenityFollow>(f => f.MemberId == uid)).Select(f => f.AmenityId).ToHashSet();
        var isMember = (await gw.ListAsync<Member>(m => m.UserId == uid)).Count > 0;
        return new AmenityBoard(amenities.Select(a => new AmenityCard(a, saved.Contains(a.Id), isMember)).ToList(), isMember);
    });

    public Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("set_amenity_followed", new() { ["p_amenity_id"] = amenityId, ["p_follow"] = follow }); return true; });

    public async Task<AppResult<IReadOnlyList<NotifiedMember>>> SetStatusAsync(int amenityId, string status, string? reason, DateTime? backAtUtc)
    {
        if (!Statuses.Contains(status) || reason?.Trim().Length > 200 || backAtUtc <= DateTime.UtcNow)
            return AppResult<IReadOnlyList<NotifiedMember>>.Fail(AppErrorKind.Validation, "bad_input");
        var args = new Dictionary<string, object> { ["p_amenity_id"] = amenityId, ["p_status"] = status };
        if (!string.IsNullOrWhiteSpace(reason)) args["p_reason"] = reason.Trim();
        if (backAtUtc is { } back) args["p_back_at"] = back.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        return await Safe.RunAsync(async () => (IReadOnlyList<NotifiedMember>)(await gw.RpcAsync<List<NotifiedMember>>("set_amenity_status", args) ?? []));
    }
```

- [ ] **Step 5: Demo gateway**

In `src/GymMembership.Core/Demo/DemoGateway.cs`:
1. Add `"amenities:set_status"` to the `["admin"]` and `["employee"]` permission lists (the two lines that contain `"amenity_usages:verify"`).
2. In the read filter (`Allowed`), add `AmenityFollow f => f.MemberId == uid,` above `_ => true`.
3. In `UpdateAsync`'s `case Amenity a:` copy-back, make sure the copy leaves `Availability`, `StatusReason` and `BackAt` alone (read how the copy is done; if it copies every property, exclude these three, mirroring the real columns that catalog edits never send).
4. In the seed: after the four `Add(new Amenity ...)` lines, mark the sauna `Availability = "unavailable", StatusReason = "Cleaning", BackAt = DateTime.UtcNow.AddMinutes(90)`, and add follows for Alex: `Add(new AmenityFollow { MemberId = "u-alex", AmenityId = pool.Id })` and the same for the sauna (the pool is already held in a variable named `pool`; capture the sauna from its `Add(...)` the same way).
5. In the RPC switch, above `case "mark_password_changed":` add:

```csharp
            case "set_amenity_followed":
            {
                if (!All<Member>().Any(m => m.UserId == uid)) throw Err(Rls);
                var id = I("p_amenity_id");
                var saved = All<AmenityFollow>().FirstOrDefault(f => f.MemberId == uid && f.AmenityId == id);
                if (B("p_follow"))
                {
                    if (All<Amenity>().All(a => a.Id != id || !a.IsActive)) throw Err("not_found");
                    if (saved is null) Add(new AmenityFollow { MemberId = uid, AmenityId = id });
                }
                else if (saved is not null) Table(typeof(AmenityFollow)).Remove(saved);
                return null;
            }
            case "set_amenity_status":
            {
                Require("amenities:set_status");
                var a = All<Amenity>().FirstOrDefault(x => x.Id == I("p_amenity_id")) ?? throw Err("not_found");
                var status = S("p_status");
                var unavailable = status == "unavailable";
                var reason = unavailable ? Opt("p_reason") : null;
                DateTime? backAt = unavailable && Opt("p_back_at") is { } raw ? DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() : null;
                if (status is not ("available" or "in_use" or "unavailable") || reason?.Length > 200 || backAt <= DateTime.UtcNow) throw Err("bad_input");

                var told = new List<NotifiedMember>();
                if (a.Availability == status && a.StatusReason == reason && a.BackAt == backAt) return told;   // nothing changed: nobody is told
                var before = a.Availability;
                a.Availability = status; a.StatusReason = reason; a.BackAt = backAt;
                Audit("amenity.status", "amenities", a.Id.ToString());
                if (status == "in_use") return told;

                var title = status == "available" ? $"{a.Name} is available" : $"{a.Name} is unavailable";
                var body = status == "available" ? null : string.Join(". ", new[] { reason, backAt is { } b ? $"Back about {b.AddHours(8):ddd dd MMM, h:mm tt}" : null }.Where(x => x is not null));
                foreach (var f in All<AmenityFollow>().Where(f => f.AmenityId == a.Id).ToList())
                {
                    var person = All<Profile>().FirstOrDefault(p => p.Id == f.MemberId);
                    if (person is not { IsActive: true }) continue;
                    Notify(f.MemberId, "amenity", title, body);
                    told.Add(new NotifiedMember(f.MemberId, person.FullName));
                }
                return told.OrderBy(t => t.Name).ToList();
            }
```

(`I`, `S`, `B`, `Opt`, `Require`, `Audit`, `Notify`, `Err`, `Rls` and `Table(...)` are the existing helpers in the same file.)

- [ ] **Step 6: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS. If `Setting_a_status_calls_the_function...` fails on the JSON mapping of `NotifiedMember` (the `[property: JsonProperty]` attribute on a positional record), switch the record to an ordinary class with `[JsonProperty]` on two `{ get; set; }` properties and update the three constructors in the demo and tests; the test is the check.

- [ ] **Step 7: Commit**

```bash
git add src/GymMembership.Core tests/GymMembership.Tests
git commit -m "feat: amenity board, saved amenities and status changes in the service and the demo

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: View models

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (replace `AmenitiesViewModel`), `src/GymMembership.Core/ViewModels/StaffViewModels.cs` (add `AmenityStatusViewModel`)
- Create: `tests/GymMembership.Tests/AmenityViewModelTests.cs`

**Interfaces:**
- Consumes: `IAmenityService` (Task 2).
- Produces:
  - `AmenityBoardViewModel(IAmenityService)`: `Cards`, `CanFollow`, `LoadCommand`, `ToggleFollowCommand(AmenityCard)`. It replaces `AmenitiesViewModel`, which is deleted (nothing else uses it).
  - `StatusChoice(string Value, string Label)` and `AmenityStatusViewModel(IAmenityService)`: `Cards`, `Choices`, `Editing : AmenityCard?`, `IsEditing`, `Choice : StatusChoice`, `ShowDetail`, `Reason`, `HasBackAt`, `BackDate : DateTime`, `BackTime : TimeSpan`, commands `SelectCommand(AmenityCard)`, `QuickReasonCommand(string)`, `SaveCommand`, `CancelCommand`; after a save `Notice` names who was alerted.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/AmenityViewModelTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AmenityViewModelTests
{
    static async Task<(DemoGateway Gw, AmenityStatusViewModel Vm)> Desk()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new AmenityStatusViewModel(new AmenityService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task The_board_loads_and_a_member_can_save_an_amenity_from_it()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new AmenityBoardViewModel(new AmenityService(gw));
        await vm.OnAppearingAsync();
        Assert.True(vm.CanFollow);
        var classes = vm.Cards.First(c => c.Amenity.Name == "Group classes");
        Assert.False(classes.Followed);

        await vm.ToggleFollowCommand.ExecuteAsync(classes);

        Assert.True(vm.Cards.First(c => c.Amenity.Id == classes.Amenity.Id).Followed);
    }

    [Fact]
    public async Task A_coach_gets_the_board_without_the_save_button()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new AmenityBoardViewModel(new AmenityService(gw));
        await vm.OnAppearingAsync();
        Assert.False(vm.CanFollow);
        Assert.NotEmpty(vm.Cards);
    }

    [Fact]
    public async Task Choosing_an_amenity_fills_the_editor_with_its_current_status()
    {
        var (_, vm) = await Desk();
        var sauna = vm.Cards.First(c => c.Amenity.Name == "Sauna");

        vm.SelectCommand.Execute(sauna);

        Assert.True(vm.IsEditing);
        Assert.Equal("unavailable", vm.Choice.Value);
        Assert.Equal("Cleaning", vm.Reason);
        Assert.True(vm.HasBackAt);
        Assert.True(vm.ShowDetail);
    }

    [Fact]
    public async Task Saving_unavailable_names_the_members_who_were_alerted_and_closes_the_editor()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        vm.QuickReasonCommand.Execute("Maintenance");
        Assert.Equal("Maintenance", vm.Reason);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("Pool is now unavailable. Alerted 1 member: Alex Tan.", vm.Notice);
        Assert.Equal("unavailable", vm.Cards.First(c => c.Amenity.Name == "Pool").Availability);
    }

    [Fact]
    public async Task The_notice_says_so_when_nobody_saved_it_when_it_is_only_in_use_and_when_nothing_changed()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Group classes"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Group classes is now unavailable. Nobody had saved it, so no alerts were sent.", vm.Notice);

        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Group classes"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Nothing changed for Group classes.", vm.Notice);

        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "in_use");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Pool is now in use. Members are not alerted for this.", vm.Notice);
    }

    [Fact]
    public async Task A_return_time_in_the_past_is_refused_on_the_screen_and_the_editor_stays_open()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        vm.HasBackAt = true;
        vm.BackDate = DateTime.Today.AddDays(-1);
        vm.BackTime = TimeSpan.FromHours(9);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Pick a return time that is still ahead.", vm.ErrorMessage);
        Assert.True(vm.IsEditing);
        Assert.Equal("available", vm.Cards.First(c => c.Amenity.Name == "Pool").Availability);
    }

    [Fact]
    public async Task Offline_the_editor_stays_open_with_what_was_typed_and_nothing_is_claimed()
    {
        var (gw, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        vm.Reason = "Repairs";
        gw.FailWith = new HttpRequestException("offline");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.True(vm.IsEditing);
        Assert.Equal("Repairs", vm.Reason);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task Cancel_closes_the_editor_without_saving()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("available", vm.Cards.First(c => c.Amenity.Name == "Pool").Availability);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymMembership.Tests --filter AmenityViewModelTests`
Expected: build FAILS (`AmenityBoardViewModel`, `AmenityStatusViewModel` do not exist).

- [ ] **Step 3: The board view model**

In `src/GymMembership.Core/ViewModels/MemberViewModels.cs` **replace** the whole `AmenitiesViewModel` class (the one with `AvailCommand` and `LogUsageCommand`) with:

```csharp
/// <summary>Every amenity with its live status, for members and coaches. Members can save an amenity to be alerted when it changes.</summary>
public partial class AmenityBoardViewModel(IAmenityService svc) : LoadableViewModel
{
    public ObservableCollection<AmenityCard> Cards { get; } = new();
    [ObservableProperty] private bool canFollow;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.BoardAsync, board => { Fill(Cards, board.Cards); CanFollow = board.CanFollow; });

    [RelayCommand]
    private async Task ToggleFollowAsync(AmenityCard card)
    { if (await RunAsync(() => svc.SetFollowedAsync(card.Amenity.Id, !card.Followed))) await LoadAsync(); }
}
```

- [ ] **Step 4: The staff view model**

Append to `src/GymMembership.Core/ViewModels/StaffViewModels.cs`:

```csharp
public sealed record StatusChoice(string Value, string Label);

/// <summary>Front desk and admin: mark an amenity available, in use or unavailable, and see who was alerted.</summary>
public partial class AmenityStatusViewModel(IAmenityService svc) : LoadableViewModel
{
    public ObservableCollection<AmenityCard> Cards { get; } = new();
    public StatusChoice[] Choices { get; } = [new("available", "Available"), new("in_use", "In use"), new("unavailable", "Unavailable")];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEditing))] private AmenityCard? editing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDetail))] private StatusChoice choice = new("available", "Available");
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private bool hasBackAt;
    [ObservableProperty] private DateTime backDate = DateTime.Today;
    [ObservableProperty] private TimeSpan backTime = TimeSpan.FromHours(12);

    public bool IsEditing => Editing is not null;
    public bool ShowDetail => Choice.Value == "unavailable";   // a reason and a return time only belong to "unavailable"

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.BoardAsync, board => Fill(Cards, board.Cards));

    [RelayCommand]
    private void Select(AmenityCard card)
    {
        Editing = card;
        ErrorMessage = null; Notice = null;
        Choice = Choices.First(c => c.Value == card.Availability);
        Reason = card.Reason ?? "";
        HasBackAt = card.BackAt is not null;
        var back = (card.BackAt ?? DateTime.UtcNow.AddHours(2)).ToLocalTime();
        BackDate = back.Date; BackTime = back.TimeOfDay;
    }

    [RelayCommand] private void QuickReason(string text) => Reason = text;

    [RelayCommand]
    private void Cancel() { Editing = null; ErrorMessage = null; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editing is not { } card) return;
        DateTime? back = null;
        if (ShowDetail && HasBackAt)
        {
            back = DateTime.SpecifyKind(BackDate.Date + BackTime, DateTimeKind.Local).ToUniversalTime();
            if (back <= DateTime.UtcNow) { ErrorMessage = "Pick a return time that is still ahead."; return; }
        }
        var reason = ShowDetail ? Reason.Trim() : "";
        var unchanged = card.Availability == Choice.Value && (card.Reason ?? "") == reason && (!ShowDetail || card.BackAt?.ToUniversalTime().ToString("s") == back?.ToString("s"));

        Notice = null;
        IReadOnlyList<NotifiedMember>? told = null;
        if (!await RunAsync(() => svc.SetStatusAsync(card.Amenity.Id, Choice.Value, reason, back), r => told = r)) return;   // the editor stays open with what was typed

        Notice = unchanged ? $"Nothing changed for {card.Amenity.Name}." : Summary(card.Amenity.Name, Choice, told!);
        Editing = null;
        await LoadAsync();
    }

    static string Summary(string name, StatusChoice choice, IReadOnlyList<NotifiedMember> told)
    {
        if (choice.Value == "in_use") return $"{name} is now in use. Members are not alerted for this.";
        var now = $"{name} is now {choice.Label.ToLowerInvariant()}.";
        if (told.Count == 0) return $"{now} Nobody had saved it, so no alerts were sent.";
        var names = string.Join(", ", told.Take(5).Select(t => t.Name)) + (told.Count > 5 ? $" and {told.Count - 5} more" : "");
        return $"{now} Alerted {told.Count} {(told.Count == 1 ? "member" : "members")}: {names}.";
    }
}
```

- [ ] **Step 5: Run to verify they pass, and the whole suite**

Run: `dotnet test tests/GymMembership.Tests`
Expected: all PASS. (The XAML for the old Amenities page still references the deleted `AmenitiesViewModel`; the app project does not build until Task 4, but the test project does not reference it.) If the "unchanged" comparison trips on time precision, compare only to the second as written; do not loosen it further.

- [ ] **Step 6: Commit**

```bash
git add src/GymMembership.Core tests/GymMembership.Tests/AmenityViewModelTests.cs
git commit -m "feat: amenity board and amenity status view models

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The screens

**Invoke the `frontend-design:frontend-design` skill first.** Keep the existing look: flat panels, hairlines, Barlow, no shadows, ink as primary, chip colors only for status.

**Files:**
- Modify: `src/GymMembership.App/Controls/Controls.cs` (`StatusChip` sets), `src/GymMembership.App/Views/BasePage.cs` (auto refresh), `src/GymMembership.App/Views/AmenitiesPage.xaml` and `.xaml.cs` (rewritten), `src/GymMembership.App/StaffShell.xaml` and `.xaml.cs`, `src/GymMembership.App/CoachShell.xaml`
- Create: `src/GymMembership.App/Controls/AmenityCardView.xaml` and `.xaml.cs`, `src/GymMembership.App/Views/AmenityStatusPage.xaml` and `.xaml.cs`

**Interfaces:**
- Consumes: `AmenityBoardViewModel`, `AmenityStatusViewModel`, `AmenityCard` (Tasks 2 and 3), `Avatar`, `StatusChip`, `BasePage.Row<TVm>`.
- Produces: `AmenityCardView` with bindable `ActionText` (empty hides the button) and event `ActionClicked`; `BasePage.AutoRefresh` (a `TimeSpan?` pages override).

- [ ] **Step 1: The chip learns the three statuses**

In `src/GymMembership.App/Controls/Controls.cs`, `StatusChip`:

```csharp
    static readonly HashSet<string> Good = ["verified", "active", "completed", "approved", "present", "available"];
    static readonly HashSet<string> Bad = ["rejected", "expired", "cancelled", "absent", "no_show", "inactive", "unavailable"];
    static readonly HashSet<string> Wait = ["pending", "late", "in_use"];
```

(The label already turns `in_use` into "In use".)

- [ ] **Step 2: A page can refresh itself while it is open**

In `src/GymMembership.App/Views/BasePage.cs` add:

```csharp
    /// <summary>A page that shows live data returns how often to reload while it is open; null means only when it opens.</summary>
    protected virtual TimeSpan? AutoRefresh => null;

    IDispatcherTimer? _refresh;

    protected override void OnDisappearing()
    {
        _refresh?.Stop();
        base.OnDisappearing();
    }
```

and in `OnAppearing`, after the existing `await vm.OnAppearingAsync()` line:

```csharp
        if (AutoRefresh is { } every)
        {
            _refresh ??= Dispatcher.CreateTimer();
            _refresh.Interval = every;
            _refresh.Tick -= OnRefreshTick;
            _refresh.Tick += OnRefreshTick;
            _refresh.Start();
        }
```

with, as a member:

```csharp
    async void OnRefreshTick(object? sender, EventArgs e)
    { if (BindingContext is LoadableViewModel live) await live.OnAppearingAsync(); }
```

- [ ] **Step 3: The shared card**

`src/GymMembership.App/Controls/AmenityCardView.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:c="clr-namespace:GymMembership.App.Controls"
             xmlns:svc="clr-namespace:GymMembership.Core.Services;assembly=GymMembership.Core"
             x:Class="GymMembership.App.Controls.AmenityCardView" x:DataType="svc:AmenityCard">

    <Border Style="{StaticResource Panel}">
        <Grid ColumnDefinitions="Auto,*" ColumnSpacing="14" RowDefinitions="Auto,Auto">
            <c:Avatar x:Name="Mark" Letters="?" Diameter="48" VerticalOptions="Start" />
            <VerticalStackLayout Grid.Column="1" Spacing="4">
                <Label Text="{Binding Amenity.Name}" Style="{StaticResource Heading}" />
                <c:StatusChip Status="{Binding Availability}" />
                <Label Text="{Binding Reason}" FontFamily="BarlowMedium" IsVisible="{Binding HasReason}" />
                <Label Text="{Binding BackAt, Converter={StaticResource LocalDate}, ConverterParameter='ddd d MMM, h:mm tt', StringFormat='Back about {0}'}"
                       Style="{StaticResource Caption}" IsVisible="{Binding HasBackAt}" />
                <Label Text="{Binding Amenity.Description}" Style="{StaticResource Muted}" />
            </VerticalStackLayout>
            <Button x:Name="Action" Grid.Row="1" Grid.ColumnSpan="2" Margin="0,10,0,0" Style="{StaticResource SmallOutlineButton}" IsVisible="False" />
        </Grid>
    </Border>
</ContentView>
```

`src/GymMembership.App/Controls/AmenityCardView.xaml.cs`:

```csharp
using GymMembership.Core.Services;

namespace GymMembership.App.Controls;

/// <summary>One amenity: monogram, name, live status chip, why it is unavailable and when it is back. An optional action button sits underneath.</summary>
public partial class AmenityCardView : ContentView
{
	public static readonly BindableProperty ActionTextProperty = BindableProperty.Create(nameof(ActionText), typeof(string), typeof(AmenityCardView), "",
		propertyChanged: (b, _, n) => { var v = (AmenityCardView)b; v.Action.Text = n as string; v.Action.IsVisible = !string.IsNullOrEmpty(n as string); });

	public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

	public event EventHandler? ActionClicked;

	public AmenityCardView()
	{
		InitializeComponent();
		Action.Clicked += (s, e) => ActionClicked?.Invoke(this, e);   // the sender is the card, whose BindingContext is the AmenityCard
	}

	protected override void OnBindingContextChanged()
	{
		base.OnBindingContextChanged();
		if (BindingContext is AmenityCard card) Mark.Letters = Initials.From(card.Amenity.Name);
	}
}
```

- [ ] **Step 4: The member and coach page**

Replace `src/GymMembership.App/Views/AmenitiesPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<v:BasePage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
            xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
            xmlns:v="clr-namespace:GymMembership.App.Views"
            xmlns:c="clr-namespace:GymMembership.App.Controls"
            xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
            xmlns:svc="clr-namespace:GymMembership.Core.Services;assembly=GymMembership.Core"
            x:Class="GymMembership.App.Views.AmenitiesPage" x:DataType="vm:AmenityBoardViewModel" Title="Amenities">

    <Grid RowDefinitions="Auto,*" Padding="{OnIdiom Phone='16,12,16,0', Default='20,16,20,0'}" MaximumWidthRequest="920">
        <VerticalStackLayout Spacing="10" Padding="0,0,0,10">
            <Label Text="What is open right now. Tap Notify me on an amenity to get an alert when it becomes available or unavailable." Style="{StaticResource Muted}" />
            <c:Banner Message="{Binding ErrorMessage}" />
        </VerticalStackLayout>

        <CollectionView Grid.Row="1" ItemsSource="{Binding Cards}">
            <CollectionView.ItemsLayout><LinearItemsLayout Orientation="Vertical" ItemSpacing="10" /></CollectionView.ItemsLayout>
            <CollectionView.EmptyView><Label Text="No amenities are listed yet." Style="{StaticResource Muted}" /></CollectionView.EmptyView>
            <CollectionView.ItemTemplate>
                <DataTemplate x:DataType="svc:AmenityCard">
                    <!-- the Notify me button only exists for members: coaches look but cannot save -->
                    <c:AmenityCardView ActionText="{Binding FollowText}" ActionClicked="OnFollow" />
                </DataTemplate>
            </CollectionView.ItemTemplate>
        </CollectionView>
    </Grid>
</v:BasePage>
```

The "Notify me" button needs no extra logic for coaches: `AmenityCard.FollowText` is empty when the viewer cannot save amenities, and `AmenityCardView` hides its button when `ActionText` is empty.

- [ ] **Step 5: The staff page**

`src/GymMembership.App/Views/AmenityStatusPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<v:BasePage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
            xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
            xmlns:v="clr-namespace:GymMembership.App.Views"
            xmlns:c="clr-namespace:GymMembership.App.Controls"
            xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
            xmlns:svc="clr-namespace:GymMembership.Core.Services;assembly=GymMembership.Core"
            x:Class="GymMembership.App.Views.AmenityStatusPage" x:DataType="vm:AmenityStatusViewModel" Title="Amenity Status">

    <ScrollView MaximumWidthRequest="920">
        <VerticalStackLayout Padding="{OnIdiom Phone='16,12,16,24', Default='20,16,20,24'}" Spacing="12">
            <Label Text="Mark an amenity available, in use or unavailable. Members who saved an unavailable or available amenity are alerted, and you see who." Style="{StaticResource Muted}" />
            <c:Banner Message="{Binding ErrorMessage}" />
            <c:Banner Kind="notice" Message="{Binding Notice}" />

            <!-- the editor for the amenity you chose -->
            <Border Style="{StaticResource Panel}" IsVisible="{Binding IsEditing}">
                <VerticalStackLayout Spacing="10">
                    <Label Text="{Binding Editing.Amenity.Name, StringFormat='Change {0}'}" Style="{StaticResource Heading}" />
                    <Border Style="{StaticResource Field}">
                        <Picker ItemsSource="{Binding Choices}" ItemDisplayBinding="{Binding Label}" SelectedItem="{Binding Choice}" SemanticProperties.Description="Status" />
                    </Border>

                    <VerticalStackLayout Spacing="10" IsVisible="{Binding ShowDetail}">
                        <Label Text="Reason (shown to members)" Style="{StaticResource Caption}" />
                        <Border Style="{StaticResource Field}">
                            <Entry Text="{Binding Reason}" MaxLength="200" Placeholder="For example maintenance, cleaning, closed" SemanticProperties.Description="Reason" />
                        </Border>
                        <FlexLayout Wrap="Wrap">
                            <Button Text="Maintenance" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnMaintenance" />
                            <Button Text="Cleaning" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnCleaning" />
                            <Button Text="Closed" Style="{StaticResource SmallOutlineButton}" Margin="0,0,8,8" Clicked="OnClosed" />
                        </FlexLayout>
                        <HorizontalStackLayout Spacing="10">
                            <Switch IsToggled="{Binding HasBackAt}" SemanticProperties.Description="I know when it will be back" />
                            <Label Text="I know when it will be back" VerticalOptions="Center" />
                        </HorizontalStackLayout>
                        <HorizontalStackLayout Spacing="10" IsVisible="{Binding HasBackAt}">
                            <DatePicker Date="{Binding BackDate}" SemanticProperties.Description="Return date" />
                            <TimePicker Time="{Binding BackTime}" SemanticProperties.Description="Return time" />
                        </HorizontalStackLayout>
                    </VerticalStackLayout>

                    <HorizontalStackLayout Spacing="10">
                        <Button Text="Save status" Command="{Binding SaveCommand}" />
                        <Button Text="Cancel" Style="{StaticResource OutlineButton}" Command="{Binding CancelCommand}" />
                    </HorizontalStackLayout>
                </VerticalStackLayout>
            </Border>

            <CollectionView ItemsSource="{Binding Cards}" HeightRequest="{OnIdiom Phone=900, Default=700}">
                <CollectionView.ItemsLayout><LinearItemsLayout Orientation="Vertical" ItemSpacing="10" /></CollectionView.ItemsLayout>
                <CollectionView.EmptyView><Label Text="No amenities are listed yet." Style="{StaticResource Muted}" /></CollectionView.EmptyView>
                <CollectionView.ItemTemplate>
                    <DataTemplate x:DataType="svc:AmenityCard">
                        <c:AmenityCardView ActionText="Change status" ActionClicked="OnChange" />
                    </DataTemplate>
                </CollectionView.ItemTemplate>
            </CollectionView>
        </VerticalStackLayout>
    </ScrollView>
</v:BasePage>
```

`src/GymMembership.App/Views/AmenityStatusPage.xaml.cs`:

```csharp
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AmenityStatusPage : BasePage
{
	public AmenityStatusPage(AmenityStatusViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// the reload only refills the list of cards; it never touches the editor fields, so typing is safe
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

	void OnChange(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.SelectCommand);
	void OnMaintenance(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Maintenance");
	void OnCleaning(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Cleaning");
	void OnClosed(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Closed");
}
```

- [ ] **Step 6: The menus**

`StaffShell.xaml`: replace the line `<FlyoutItem Title="Amenity visits" ClassId="desk"><ShellContent ContentTemplate="{DataTemplate views:VerifyAmenityUsagesPage}" /></FlyoutItem>` with `<FlyoutItem Title="Amenity Status" ClassId="desk"><ShellContent ContentTemplate="{DataTemplate views:AmenityStatusPage}" /></FlyoutItem>`, and in `StaffShell.xaml.cs` change the `ShortNames` key `["Amenity visits"] = "Amenities"` to `["Amenity Status"] = "Amenities"`.

`CoachShell.xaml`: add `<FlyoutItem Title="Amenities"><ShellContent ContentTemplate="{DataTemplate views:AmenitiesPage}" /></FlyoutItem>` before the Settings item. `MemberShell.xaml` already lists Amenities (members only) and needs no change.

- [ ] **Step 7: Build and run the tests**

```bash
dotnet test tests/GymMembership.Tests
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
dotnet build src/GymMembership.App -f net10.0-android
```
Expected: all pass, both builds succeed (`XamlBindingTests` now reads `AmenityCardView.xaml`, `AmenitiesPage.xaml` and `AmenityStatusPage.xaml`).

- [ ] **Step 8: Look at it (demo mode; Windows and the Android emulator)**

Move `src/GymMembership.App/Resources/Raw/supabase.json` to `supabase.json.bak` for the check and put it back afterwards. Windows: run `bin\Debug\net10.0-windows10.0.19041.0\win-x64\GymMembership.App.exe`; Android: build with `-p:EmbedAssembliesIntoApk=true`, uninstall the old app, emulator `pixel_7_-_api_36_0`.

Check, in light and dark:
- **Member (Alex):** Amenities shows Pool, Sauna, Group classes as cards with monogram, name, chip. The Sauna reads "Unavailable", "Cleaning" and "Back about ...". Pool and Sauna say "Following", Group classes says "Notify me". Tapping it flips the button to "Following".
- **Coach:** the new Amenities menu item shows the same cards with no button.
- **Front desk:** "Amenity Status" is in the bottom bar on a phone ("Amenities") and the menu on Windows. Choose Pool, set Unavailable, Maintenance, a return time; Save. The green notice reads "Pool is now unavailable. Alerted 1 member: Alex Tan." Then sign in as Alex: the bell shows the new alert, "Pool is unavailable", with "Maintenance. Back about ...".
- Saving again with nothing changed says "Nothing changed for Pool." A return time in the past shows "Pick a return time that is still ahead." and keeps the editor open.
- The old "Buy" and "Log a visit" buttons and the "Amenity visits" page are gone from every menu.
- Leave the page open 30 seconds after changing a status as another user: the card catches up.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "feat: amenity cards with live status for members and coaches, and an Amenity Status page for front desk

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

(`git status` must not list `supabase.json.bak` or `docs/screenshotsforexample/`.)

---

### Task 5: Wrap-up

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-role-redesign-design.md`

- [ ] **Step 1: Record what was built in the spec**

In section 4, after phase 3's sentence, add: `**Built in phase 3:** migration 0014 (status columns writable only by set_amenity_status, amenity_follows, set_amenity_followed, permission amenities:set_status, alert type amenity); followers are alerted when an amenity becomes available or unavailable (not for "in use"); the staff page shows who was alerted; coaches get the same board read-only; the old purchase flow is hidden, its tables and functions untouched; amenity photos wait for the image upload in phase 6; the pages refresh every 30 seconds until Realtime in phase 4.`

- [ ] **Step 2: Full verification**

```bash
dotnet test tests/GymMembership.Tests
npm --prefix supabase/tests/local test
dotnet build src/GymMembership.App -f net10.0-android
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
```
Expected: every test passes (report the counts), 75 database checks pass, both builds succeed.

- [ ] **Step 3: Review**

Run `ponytail:ponytail-review` on `git diff role-redesign-phase2...HEAD`, apply what is worth applying, re-run Step 2. Then one fresh whole-branch review of the same range (review package, most capable model, the plan's Review Focus verbatim), with extra attention to the migration: the grants (`revoke insert, update` then column grants), the security-definer functions, the `#variable_conflict` directive, and the enum value added in the same file.

- [ ] **Step 4: Commit and hand over**

```bash
git add docs/superpowers/specs/2026-10-15-role-redesign-design.md
git commit -m "docs: phase 3 recorded in the redesign spec

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Stop here. Do not push or merge. Tell the owner: branch `role-redesign-phase3` (on top of the unmerged `role-redesign-phase2`) is ready; **before the amenity status screens can work against the real project, `npx supabase db push` must run for `0012`, `0013` and `0014`, in that order** (after the pre-check query at the top of `0012`).
