# Gym Membership System — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Phase 1 of the gym membership system: a Supabase (Postgres + RLS + RPC) backend and a .NET MAUI client for Member, Coach, Admin and Employee roles.

**Architecture:** The MAUI app talks directly to Supabase through `supabase-csharp`. Supabase Auth owns identity; Postgres RLS enforces roles; atomic flows (payment verification, renewals, time-request approval) live in `SECURITY DEFINER` RPC functions. Client code is split into a plain `net10.0` `GymMembership.Core` library (models, services, ViewModels — fully unit-testable) and a thin MAUI `GymMembership.App` (XAML, Shells, DI, platform pickers).

**Tech Stack:** .NET 10 / .NET MAUI 10.0.x, C#, XAML, `CommunityToolkit.Mvvm`, `CommunityToolkit.Maui`, `supabase-csharp`, Supabase CLI (local Postgres via Docker), pgTAP, xUnit.

**Spec:** `docs/specs/technical-spec.md` (architecture, schema, RLS, RPCs) and `docs/specs/PRD.md` (features F1–F11, flows, rules). Read both before starting.

## Global Constraints

- Targets: `net10.0-android`, `net10.0-ios`, `net10.0-windows10.0.19041.0` (supported OS floor `10.0.17763.0`); Android API 21+, iOS 13+.
- Client holds **only** the Supabase URL + anon key. The service-role key never enters the repo or the app.
- Every `public` table has RLS enabled; no client write path to money/entitlement tables except RPCs.
- Timestamps are `timestamptz`; money is `numeric(12,2)` with a `char(3)` currency, default `'USD'`.
- Status columns are Postgres enums; the C# models carry them as `string` constants in `Status`.
- RPC error contract (exception message tokens the client maps): `forbidden` (errcode 42501), `not_found`, `already_processed`, `already_availed`, `renewal_pending`, `not_renewable`, `self_verify`, `overlap`, `wrong_member`, `no_active_membership`.
- Assumptions pending PRD §8 answers (each isolated so changing it is one edit): an **active membership** is required to avail amenities, hire a coach, and check in (`has_active_membership()`); currency default USD; payments are verified manually by staff from a QR + uploaded proof.
- Phase 2 (products, sales, employees, working hours, sales reports, full audit UI) is **out of scope**.

## Review Focus

1. A member reads or edits another member's payment / membership / notification (RLS must return nothing / reject) → Task 3.
2. Staff verify the same payment twice, or verify their own payment → Task 4.
3. Renewing a membership that already expired must extend from *now*, not from the past `ends_at` → Task 4.
4. Back-to-back sessions (end == next start) must be allowed; overlapping ones rejected; a non-counterpart cannot approve a time request → Task 5.
5. App starts offline or with an expired/invalid stored session → must land on Login with a message, not crash → Task 8.

## File Structure

```
gym-membership/
  supabase/
    config.toml                                   (created by `supabase init`)
    migrations/
      20261004000100_schema.sql                   enums, tables, constraints, indexes, triggers
      20261004000200_rbac.sql                     seed roles/permissions, helper fns, signup trigger
      20261004000300_rls.sql                      RLS enable + policies
      20261004000400_rpc_payments.sql             notify/audit helpers, avail/verify/renew
      20261004000500_rpc_sessions.sql             time requests, attendance, check-in, amenities, admin, expiry
      20261004000600_storage_cron.sql             buckets, storage policies, pg_cron
    tests/database/
      01_schema.test.sql  02_rls.test.sql  03_rpc_payments.test.sql  04_rpc_sessions.test.sql
  src/
    GymMembership.Core/                           net10.0 class library
      Models/Models.cs  Models/Status.cs
      Services/ErrorMapper.cs  Services/AppResult.cs  Services/ISupabaseGateway.cs  Services/SupabaseGateway.cs
      Services/AuthService.cs  MembershipService.cs  PaymentStaffService.cs  AmenityService.cs
      Services/CoachService.cs  SessionService.cs  AttendanceService.cs  NotificationService.cs  AdminService.cs
      ViewModels/LoadableViewModel.cs  + one ViewModel per screen
    GymMembership.App/                            MAUI project (Views, Shells, DI, platform services)
  tests/GymMembership.Tests/                      xUnit; FakeGateway; one test file per service/VM
```

Prerequisites on the dev machine: Docker Desktop, Supabase CLI (`scoop install supabase` or `npm i -g supabase`), .NET 10 SDK, `dotnet workload install maui`.

---

# PART A — Backend (Supabase)

### Task 1: Repo init + schema migration

**Files:**
- Create: `supabase/migrations/20261004000100_schema.sql`
- Test: `supabase/tests/database/01_schema.test.sql`

**Interfaces:**
- Produces: all tables, enums, constraints used by Tasks 2–5 and the C# models in Task 6. Table/column names are exactly as in the spec §3 (`profiles`, `payments.proof_path`, `check_ins`, `audit_log`, …).

- [ ] **Step 1: Init repo and Supabase**

```bash
cd C:/Users/bowie/OneDrive/Desktop/projects/gym-membership
git init
printf "bin/\nobj/\n.vs/\nsupabase/.temp/\nsupabase/.branches/\n*.user\n" > .gitignore
supabase init
supabase start
```
Expected: local stack prints `API URL`, `anon key`, `DB URL`. Record URL + anon key for Task 8.

- [ ] **Step 2: Write the failing schema test**

`supabase/tests/database/01_schema.test.sql`:
```sql
begin;
select plan(8);

select has_table('public','profiles','profiles exists');
select has_table('public','check_ins','check_ins exists');
select has_table('public','audit_log','audit_log exists');
select col_is_unique('public','user_amenities',array['user_id','amenity_id'],'one entitlement per amenity');

-- every public table must have RLS enabled (policies come in Task 3; enabling is done there,
-- so this test is expected to fail until Task 3 — it is re-run in Task 3).
select is((select count(*)::int from pg_tables where schemaname='public' and not rowsecurity), 0, 'RLS enabled on all tables');

-- overlap constraint
insert into auth.users(id,email) values ('00000000-0000-0000-0000-0000000000c1','c1@x.io'),('00000000-0000-0000-0000-0000000000a1','m1@x.io');
insert into coaches(user_id) values ('00000000-0000-0000-0000-0000000000c1');
insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end)
  select (select coach_id from coaches limit 1),(select member_id from members limit 1),'2026-11-01 10:00+00','2026-11-01 11:00+00';
select throws_ok($$insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end)
  select (select coach_id from coaches limit 1),(select member_id from members limit 1),'2026-11-01 10:30+00','2026-11-01 11:30+00'$$,
  '23P01',null,'overlapping session rejected');
select lives_ok($$insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end)
  select (select coach_id from coaches limit 1),(select member_id from members limit 1),'2026-11-01 11:00+00','2026-11-01 12:00+00'$$,
  'back-to-back session allowed');

select * from finish();
rollback;
```
Note: this file references the `handle_new_user` trigger (Task 2) to create `members` rows. Until Task 2 the `members` insert subquery is empty and the overlap tests fail; that is expected — the full file must pass after Task 3.

- [ ] **Step 3: Run it — expect failure**

Run: `supabase test db`
Expected: FAIL (`profiles` does not exist).

- [ ] **Step 4: Write the schema migration**

`supabase/migrations/20261004000100_schema.sql`:
```sql
create extension if not exists btree_gist;

create type payment_status       as enum ('pending','verified','rejected','expired');
create type membership_status    as enum ('pending','active','expired','cancelled');
create type renewal_status       as enum ('pending','completed','cancelled');
create type time_request_status  as enum ('pending','approved','rejected','cancelled');
create type attendance_status    as enum ('present','late','absent','excused');
create type coach_hire_status    as enum ('active','ended','cancelled');
create type session_status       as enum ('scheduled','completed','cancelled','no_show');
create type amenity_usage_status as enum ('pending','verified','rejected');
create type requested_by_role    as enum ('member','coach');

create function public.set_updated_at() returns trigger language plpgsql as $$
begin new.updated_at = now(); return new; end $$;

create table profiles (
  id          uuid primary key references auth.users(id) on delete cascade,
  username    text not null unique,
  is_active   boolean not null default true,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now()
);
create trigger profiles_updated before update on profiles for each row execute function set_updated_at();

create table roles (
  role_id int generated always as identity primary key,
  name text not null unique, description text, created_at timestamptz not null default now());
create table permissions (
  permission_id int generated always as identity primary key,
  name text not null unique, resource text not null, action text not null,
  description text, created_at timestamptz not null default now());
create table user_roles (
  user_role_id int generated always as identity primary key,
  user_id uuid not null references profiles(id) on delete cascade,
  role_id int not null references roles(role_id) on delete cascade,
  assigned_at timestamptz not null default now(),
  unique (user_id, role_id));
create table role_permissions (
  role_permission_id int generated always as identity primary key,
  role_id int not null references roles(role_id) on delete cascade,
  permission_id int not null references permissions(permission_id) on delete cascade,
  granted_at timestamptz not null default now(),
  unique (role_id, permission_id));

create table user_settings (
  user_setting_id int generated always as identity primary key,
  user_id uuid not null unique references profiles(id) on delete cascade,
  locale text not null default 'en', timezone text not null default 'UTC',
  theme text not null default 'system' check (theme in ('system','light','dark')),
  language text not null default 'en',
  updated_at timestamptz not null default now());
create trigger user_settings_updated before update on user_settings for each row execute function set_updated_at();

create table membership_packages (
  membership_package_id int generated always as identity primary key,
  name text not null unique, description text,
  price numeric(12,2) not null check (price >= 0),
  duration_days int not null check (duration_days > 0),
  is_active boolean not null default true,
  currency char(3) not null default 'USD',
  created_at timestamptz not null default now());
create table amenities (
  amenity_id int generated always as identity primary key,
  name text not null unique, description text,
  is_active boolean not null default true, created_at timestamptz not null default now());

create table user_membership_packages (
  user_membership_package_id int generated always as identity primary key,
  user_id uuid not null references profiles(id) on delete cascade,
  membership_package_id int not null references membership_packages(membership_package_id),
  starts_at timestamptz, ends_at timestamptz,
  status membership_status not null default 'pending',
  created_at timestamptz not null default now());
create table user_amenities (
  user_amenity_id int generated always as identity primary key,
  user_id uuid not null references profiles(id) on delete cascade,
  amenity_id int not null references amenities(amenity_id),
  availed_at timestamptz not null default now(),
  unique (user_id, amenity_id));

create table payments (
  payment_id int generated always as identity primary key,
  qr text not null unique,
  status payment_status not null default 'pending',
  verified_by uuid references profiles(id),
  verified_at timestamptz,
  amount numeric(12,2) not null check (amount >= 0),
  currency char(3) not null default 'USD',
  created_at timestamptz not null default now(),
  user_id uuid not null references profiles(id),
  user_membership_package_id int references user_membership_packages(user_membership_package_id),
  user_amenity_id int references user_amenities(user_amenity_id),
  proof_path text,
  check (num_nonnulls(user_membership_package_id, user_amenity_id) = 1));

create table coaches (
  coach_id int generated always as identity primary key,
  user_id uuid not null unique references profiles(id) on delete cascade,
  bio text, specialty text, hourly_rate numeric(12,2),
  is_available boolean not null default true, created_at timestamptz not null default now());
create table members (
  member_id int generated always as identity primary key,
  user_id uuid not null unique references profiles(id) on delete cascade,
  goals text, created_at timestamptz not null default now());

create table coach_hires (
  coach_hire_id int generated always as identity primary key,
  member_id int not null references members(member_id),
  coach_id int not null references coaches(coach_id),
  hired_at timestamptz not null default now(), ended_at timestamptz,
  status coach_hire_status not null default 'active');
create unique index coach_hires_one_active on coach_hires(member_id, coach_id) where status = 'active';

create table training_sessions (
  training_session_id int generated always as identity primary key,
  coach_id int not null references coaches(coach_id),
  member_id int not null references members(member_id),
  title text,
  scheduled_start timestamptz not null, scheduled_end timestamptz not null,
  status session_status not null default 'scheduled',
  notes text, created_at timestamptz not null default now(),
  check (scheduled_end > scheduled_start),
  exclude using gist (coach_id with =, tstzrange(scheduled_start, scheduled_end) with &&) where (status <> 'cancelled'));

create table time_requests (
  time_request_id int generated always as identity primary key,
  coach_id int not null references coaches(coach_id),
  member_id int references members(member_id),
  requested_by requested_by_role not null,
  requested_start timestamptz not null, requested_end timestamptz not null,
  status time_request_status not null default 'pending',
  message text, responded_at timestamptz, created_at timestamptz not null default now(),
  check (requested_end > requested_start),
  check (requested_by = 'coach' or member_id is not null));

create table session_attendance (
  attendance_id int generated always as identity primary key,
  training_session_id int not null references training_sessions(training_session_id) on delete cascade,
  member_id int not null references members(member_id),
  status attendance_status not null default 'present',
  checked_in_at timestamptz, recorded_by uuid references profiles(id),
  notes text, created_at timestamptz not null default now(),
  unique (training_session_id, member_id));

create table amenity_usages (
  amenity_usage_id int generated always as identity primary key,
  user_amenity_id int not null references user_amenities(user_amenity_id) on delete cascade,
  used_at timestamptz not null default now(),
  proof text, verified_by uuid references profiles(id), verified_at timestamptz,
  status amenity_usage_status not null default 'pending', notes text);

create table membership_renewals (
  renewal_id int generated always as identity primary key,
  user_membership_package_id int not null references user_membership_packages(user_membership_package_id),
  payment_id int unique references payments(payment_id),
  new_starts_at timestamptz not null, new_ends_at timestamptz,
  amount numeric(12,2), currency char(3) not null default 'USD',
  status renewal_status not null default 'pending',
  renewed_at timestamptz not null default now(), notes text);

create table user_notifications (
  notification_id int generated always as identity primary key,
  user_id uuid not null references profiles(id) on delete cascade,
  type text not null check (type in ('time_request','membership','payment','payout','attendance','system')),
  title text not null, body text, reference text,
  is_read boolean not null default false, read_at timestamptz,
  created_at timestamptz not null default now());

create table check_ins (
  check_in_id int generated always as identity primary key,
  member_id int not null references members(member_id),
  checked_in_at timestamptz not null default now(),
  recorded_by uuid references profiles(id));

create table audit_log (
  audit_id int generated always as identity primary key,
  actor_id uuid references profiles(id),
  action text not null, entity text not null, entity_id text,
  details jsonb, created_at timestamptz not null default now());

-- indexes on FK / filter columns
create index on user_roles(user_id);
create index on user_membership_packages(user_id);
create index on user_membership_packages(status);
create index on user_amenities(user_id);
create index on payments(user_id);
create index on payments(status);
create index on coach_hires(coach_id);
create index on training_sessions(member_id);
create index on time_requests(coach_id);
create index on time_requests(member_id);
create index on amenity_usages(user_amenity_id);
create index on membership_renewals(user_membership_package_id);
create index on user_notifications(user_id, is_read);
create index on check_ins(member_id);
create index on audit_log(created_at desc);
```

- [ ] **Step 5: Run migrations + test**

Run: `supabase db reset && supabase test db`
Expected: migration applies cleanly. `01_schema.test.sql` still has failures for the RLS test and the overlap tests (no `members` row yet) — everything else passes. These last 3 turn green in Task 3.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(db): schema migration and schema tests"
```

---

### Task 2: RBAC seed, helper functions, signup trigger

**Files:**
- Create: `supabase/migrations/20261004000200_rbac.sql`
- Test: `supabase/tests/database/02_rls.test.sql` (started here, extended in Task 3)

**Interfaces:**
- Produces (SQL): `has_permission(text) → boolean`, `current_member_id() → int`, `current_coach_id() → int`, `has_active_membership(uuid) → boolean`. Roles `admin, employee, coach, member`. Permissions: `users:manage, packages:manage, amenities:manage, amenities:verify, coaches:manage, payments:verify, revenue:read, audit:read, checkins:record, attendance:record`.

- [ ] **Step 1: Write failing test (signup creates profile, member, role)**

`supabase/tests/database/02_rls.test.sql`:
```sql
begin;
select plan(4);

insert into auth.users(id,email,raw_user_meta_data)
values ('00000000-0000-0000-0000-0000000000a1','m1@x.io','{"username":"alice"}');

select is((select username from profiles where id='00000000-0000-0000-0000-0000000000a1'),'alice','profile created from metadata');
select is((select count(*)::int from members where user_id='00000000-0000-0000-0000-0000000000a1'),1,'member row created');
select is((select r.name from user_roles ur join roles r using(role_id) where ur.user_id='00000000-0000-0000-0000-0000000000a1'),'member','default role is member');
select is((select count(*)::int from user_settings where user_id='00000000-0000-0000-0000-0000000000a1'),1,'settings row created');

select * from finish();
rollback;
```

- [ ] **Step 2: Run — expect FAIL** (`supabase test db`; profile not created).

- [ ] **Step 3: Write the migration**

`supabase/migrations/20261004000200_rbac.sql`:
```sql
insert into roles(name, description) values
  ('admin','Full access'),('employee','Front desk staff'),('coach','Coaching staff'),('member','Gym member');

insert into permissions(name, resource, action) values
  ('users:manage','users','manage'),('packages:manage','packages','manage'),
  ('amenities:manage','amenities','manage'),('amenities:verify','amenities','verify'),
  ('coaches:manage','coaches','manage'),('payments:verify','payments','verify'),
  ('revenue:read','revenue','read'),('audit:read','audit','read'),
  ('checkins:record','checkins','record'),('attendance:record','attendance','record');

-- admin: everything
insert into role_permissions(role_id, permission_id)
select (select role_id from roles where name='admin'), permission_id from permissions;
-- employee
insert into role_permissions(role_id, permission_id)
select (select role_id from roles where name='employee'), permission_id from permissions
where name in ('payments:verify','amenities:verify','checkins:record','attendance:record');
-- coach
insert into role_permissions(role_id, permission_id)
select (select role_id from roles where name='coach'), permission_id from permissions
where name in ('attendance:record');

create function public.has_permission(p text) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (
    select 1 from user_roles ur
    join role_permissions rp on rp.role_id = ur.role_id
    join permissions pm on pm.permission_id = rp.permission_id
    where ur.user_id = auth.uid() and pm.name = p);
$$;

create function public.current_member_id() returns int
language sql stable security definer set search_path = public as $$
  select member_id from members where user_id = auth.uid();
$$;

create function public.current_coach_id() returns int
language sql stable security definer set search_path = public as $$
  select coach_id from coaches where user_id = auth.uid();
$$;

create function public.has_active_membership(uid uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from user_membership_packages
    where user_id = uid and status = 'active' and (ends_at is null or ends_at > now()));
$$;

create function public.handle_new_user() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  insert into profiles(id, username) values (new.id,
    coalesce(nullif(new.raw_user_meta_data->>'username',''),
             split_part(new.email,'@',1) || '-' || left(new.id::text, 4)));
  insert into user_settings(user_id) values (new.id);
  insert into members(user_id) values (new.id);
  insert into user_roles(user_id, role_id) select new.id, role_id from roles where name = 'member';
  return new;
end $$;

create trigger on_auth_user_created after insert on auth.users
for each row execute function public.handle_new_user();
```

- [ ] **Step 4: Run — expect PASS** (`supabase db reset && supabase test db`; `02_rls` 4/4; `01_schema` overlap tests now pass too).

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(db): rbac seed, helpers, signup trigger"`

---

### Task 3: Row Level Security policies

**Files:**
- Create: `supabase/migrations/20261004000300_rls.sql`
- Modify: `supabase/tests/database/02_rls.test.sql`

**Interfaces:**
- Consumes: helpers from Task 2.
- Produces: RLS on every table. Clients can write directly only: own `user_settings`, own `profiles.username`, `payments.proof_path` (own pending), `user_notifications` (own), `amenity_usages` insert (own), `coaches` (own row), `coach_hires` insert/update, `time_requests` insert/cancel, and admin catalog tables (`membership_packages`, `amenities`, `roles`…). Everything else is RPC-only.

- [ ] **Step 1: Extend the failing test (Review Focus #1)**

Replace `02_rls.test.sql` with:
```sql
begin;
select plan(11);
grant all on table __tresults__ to authenticated;
grant all on sequence __tresults___numb_seq to authenticated;

insert into auth.users(id,email) values
  ('00000000-0000-0000-0000-0000000000a1','alice@x.io'),
  ('00000000-0000-0000-0000-0000000000a2','bob@x.io'),
  ('00000000-0000-0000-0000-0000000000e1','emp@x.io'),
  ('00000000-0000-0000-0000-0000000000d1','admin@x.io');
insert into user_roles(user_id, role_id)
  select '00000000-0000-0000-0000-0000000000e1', role_id from roles where name='employee'
  union all select '00000000-0000-0000-0000-0000000000d1', role_id from roles where name='admin';
insert into membership_packages(name,price,duration_days) values ('Monthly',30,30);
insert into user_membership_packages(user_id,membership_package_id,status)
  values ('00000000-0000-0000-0000-0000000000a1',1,'pending'),('00000000-0000-0000-0000-0000000000a2',1,'pending');
insert into payments(qr,amount,user_id,user_membership_package_id) values
  ('qr-a',30,'00000000-0000-0000-0000-0000000000a1',1),('qr-b',30,'00000000-0000-0000-0000-0000000000a2',2);
insert into user_notifications(user_id,type,title) values ('00000000-0000-0000-0000-0000000000a2','system','for bob');

select is((select count(*)::int from pg_tables where schemaname='public' and not rowsecurity),0,'RLS on every table');

-- alice
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-0000000000a1","role":"authenticated"}', true);
set local role authenticated;
select is((select count(*)::int from payments),1,'member sees only own payments');
select is((select count(*)::int from user_membership_packages),1,'member sees only own memberships');
select is((select count(*)::int from user_notifications),0,'member cannot read others notifications');
select is((select count(*)::int from audit_log),0,'member cannot read audit log');
select throws_ok($$update payments set amount = 0 where payment_id = 1$$,'42501',null,'member cannot change payment amount (column grant)');
select lives_ok($$update payments set proof_path = 'a1/proof.jpg' where payment_id = 1$$,'member can attach proof to own pending payment');
select is((select count(*)::int from payments where payment_id = 2),0,'member cannot see bob payment');
reset role;

-- employee
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-0000000000e1","role":"authenticated"}', true);
set local role authenticated;
select is((select count(*)::int from payments where status='pending'),2,'employee sees pending payments');
reset role;

-- admin
select set_config('request.jwt.claims', '{"sub":"00000000-0000-0000-0000-0000000000d1","role":"authenticated"}', true);
set local role authenticated;
select lives_ok($$insert into membership_packages(name,price,duration_days) values ('Yearly',300,365)$$,'admin can create package');

select * from finish();
rollback;
```

- [ ] **Step 2: Run — expect FAIL** (`supabase test db`; RLS not enabled).

- [ ] **Step 3: Write the migration**

`supabase/migrations/20261004000300_rls.sql`:
```sql
do $$ declare t text; begin
  for t in select tablename from pg_tables where schemaname = 'public' loop
    execute format('alter table public.%I enable row level security', t);
  end loop;
end $$;

-- profiles: readable by any signed-in user (username only; email lives in auth.users)
create policy profiles_select on profiles for select to authenticated using (true);
create policy profiles_update_own on profiles for update to authenticated
  using (id = auth.uid()) with check (id = auth.uid());
create policy profiles_update_admin on profiles for update to authenticated
  using (has_permission('users:manage'));
create function public.guard_profile_update() returns trigger language plpgsql as $$
begin
  if new.is_active is distinct from old.is_active and not has_permission('users:manage') then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  return new;
end $$;
create trigger profiles_guard before update on profiles for each row execute function guard_profile_update();

-- RBAC tables
create policy roles_select on roles for select to authenticated using (true);
create policy permissions_select on permissions for select to authenticated using (true);
create policy role_permissions_select on role_permissions for select to authenticated using (true);
create policy roles_admin on roles for all to authenticated using (has_permission('users:manage')) with check (has_permission('users:manage'));
create policy permissions_admin on permissions for all to authenticated using (has_permission('users:manage')) with check (has_permission('users:manage'));
create policy role_permissions_admin on role_permissions for all to authenticated using (has_permission('users:manage')) with check (has_permission('users:manage'));
create policy user_roles_select on user_roles for select to authenticated
  using (user_id = auth.uid() or has_permission('users:manage'));

create policy user_settings_own on user_settings for all to authenticated
  using (user_id = auth.uid()) with check (user_id = auth.uid());
create policy user_settings_admin on user_settings for select to authenticated using (has_permission('users:manage'));

-- catalogs
create policy packages_select on membership_packages for select to authenticated
  using (is_active or has_permission('packages:manage'));
create policy packages_admin on membership_packages for all to authenticated
  using (has_permission('packages:manage')) with check (has_permission('packages:manage'));
create policy amenities_select on amenities for select to authenticated
  using (is_active or has_permission('amenities:manage'));
create policy amenities_admin on amenities for all to authenticated
  using (has_permission('amenities:manage')) with check (has_permission('amenities:manage'));

-- entitlements (RPC-only writes)
create policy ump_select on user_membership_packages for select to authenticated
  using (user_id = auth.uid() or has_permission('payments:verify') or has_permission('users:manage'));
create policy ua_select on user_amenities for select to authenticated
  using (user_id = auth.uid() or has_permission('amenities:verify') or has_permission('users:manage'));
create policy renewals_select on membership_renewals for select to authenticated
  using (has_permission('payments:verify') or exists (select 1 from user_membership_packages u
    where u.user_membership_package_id = membership_renewals.user_membership_package_id and u.user_id = auth.uid()));

-- payments: select own/staff; member may only set proof_path on own pending payment
create policy payments_select on payments for select to authenticated
  using (user_id = auth.uid() or has_permission('payments:verify') or has_permission('revenue:read'));
create policy payments_proof on payments for update to authenticated
  using (user_id = auth.uid() and status = 'pending') with check (user_id = auth.uid() and status = 'pending');
revoke update on payments from authenticated;
grant update (proof_path) on payments to authenticated;

-- coaches / members
create policy coaches_select on coaches for select to authenticated using (true);
create policy coaches_update_own on coaches for update to authenticated using (user_id = auth.uid()) with check (user_id = auth.uid());
create policy coaches_admin on coaches for all to authenticated using (has_permission('coaches:manage')) with check (has_permission('coaches:manage'));
create policy members_select on members for select to authenticated
  using (user_id = auth.uid() or has_permission('users:manage') or has_permission('checkins:record')
    or exists (select 1 from coach_hires h where h.member_id = members.member_id and h.coach_id = current_coach_id()));
create policy members_update_own on members for update to authenticated using (user_id = auth.uid()) with check (user_id = auth.uid());

-- hires
create policy hires_select on coach_hires for select to authenticated
  using (member_id = current_member_id() or coach_id = current_coach_id() or has_permission('coaches:manage'));
create policy hires_insert on coach_hires for insert to authenticated
  with check (member_id = current_member_id() and status = 'active' and has_active_membership(auth.uid()));
create policy hires_update on coach_hires for update to authenticated
  using (member_id = current_member_id() or coach_id = current_coach_id() or has_permission('coaches:manage'))
  with check (member_id = current_member_id() or coach_id = current_coach_id() or has_permission('coaches:manage'));

-- sessions
create policy sessions_select on training_sessions for select to authenticated
  using (member_id = current_member_id() or coach_id = current_coach_id() or has_permission('coaches:manage'));
create policy sessions_update on training_sessions for update to authenticated
  using (coach_id = current_coach_id() or has_permission('coaches:manage'))
  with check (coach_id = current_coach_id() or has_permission('coaches:manage'));

-- time requests
create policy tr_select on time_requests for select to authenticated
  using (member_id = current_member_id() or coach_id = current_coach_id() or has_permission('coaches:manage')
    or (member_id is null and exists (select 1 from coach_hires h where h.coach_id = time_requests.coach_id
        and h.member_id = current_member_id() and h.status = 'active')));
create policy tr_insert_member on time_requests for insert to authenticated
  with check (requested_by = 'member' and member_id = current_member_id() and status = 'pending'
    and exists (select 1 from coach_hires h where h.coach_id = time_requests.coach_id
      and h.member_id = current_member_id() and h.status = 'active'));
create policy tr_insert_coach on time_requests for insert to authenticated
  with check (requested_by = 'coach' and coach_id = current_coach_id() and status = 'pending');
create policy tr_cancel on time_requests for update to authenticated
  using ((requested_by = 'member' and member_id = current_member_id()) or (requested_by = 'coach' and coach_id = current_coach_id()))
  with check (status = 'cancelled');

-- attendance, check-ins
create policy att_select on session_attendance for select to authenticated
  using (member_id = current_member_id() or has_permission('attendance:record')
    or exists (select 1 from training_sessions s where s.training_session_id = session_attendance.training_session_id
        and s.coach_id = current_coach_id()));
create policy checkins_select on check_ins for select to authenticated
  using (member_id = current_member_id() or has_permission('checkins:record') or has_permission('users:manage'));

-- amenity usages
create policy au_select on amenity_usages for select to authenticated
  using (has_permission('amenities:verify') or exists (select 1 from user_amenities ua
    where ua.user_amenity_id = amenity_usages.user_amenity_id and ua.user_id = auth.uid()));
create policy au_insert on amenity_usages for insert to authenticated
  with check (status = 'pending' and verified_by is null and exists (select 1 from user_amenities ua
    where ua.user_amenity_id = amenity_usages.user_amenity_id and ua.user_id = auth.uid()));

-- notifications, audit
create policy notif_select on user_notifications for select to authenticated using (user_id = auth.uid());
create policy notif_update on user_notifications for update to authenticated using (user_id = auth.uid()) with check (user_id = auth.uid());
create policy audit_select on audit_log for select to authenticated using (has_permission('audit:read'));
```

- [ ] **Step 4: Run — expect PASS** (`supabase db reset && supabase test db`; `01_schema` 8/8, `02_rls` 11/11).

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(db): row level security policies"`

---

### Task 4: RPCs — membership, payments, renewals

**Files:**
- Create: `supabase/migrations/20261004000400_rpc_payments.sql`
- Test: `supabase/tests/database/03_rpc_payments.test.sql`

**Interfaces:**
- Produces (SQL, callable via `Rpc`): `avail_membership(p_package_id int) → int (payment_id)`, `renew_membership(p_ump_id int) → int (payment_id)`, `verify_payment(p_payment_id int, p_approve boolean) → void`. Internal: `notify(uuid,text,text,text,jsonb)`, `write_audit(text,text,anyelement,jsonb)`.
- Parameter names are exactly `p_package_id`, `p_ump_id`, `p_payment_id`, `p_approve` (the client passes them by name).

- [ ] **Step 1: Write the failing test (Review Focus #2, #3)**

`supabase/tests/database/03_rpc_payments.test.sql`:
```sql
begin;
select plan(11);
grant all on table __tresults__ to authenticated;
grant all on sequence __tresults___numb_seq to authenticated;

insert into auth.users(id,email) values
  ('00000000-0000-0000-0000-0000000000a1','alice@x.io'),
  ('00000000-0000-0000-0000-0000000000e1','emp@x.io');
insert into user_roles(user_id, role_id) select '00000000-0000-0000-0000-0000000000e1', role_id from roles where name='employee';
insert into membership_packages(name,price,duration_days) values ('Monthly',30,30);

-- alice avails
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000a1","role":"authenticated"}',true);
set local role authenticated;
select is((select avail_membership(1)),1,'avail returns payment id');
select throws_ok($$select avail_membership(1)$$,'P0001','already_availed','cannot avail same package twice');
select throws_ok($$select verify_payment(1,true)$$,'42501','forbidden','member cannot verify');
reset role;

-- employee verifies
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000e1","role":"authenticated"}',true);
set local role authenticated;
select lives_ok($$select verify_payment(1,true)$$,'employee verifies');
select throws_ok($$select verify_payment(1,true)$$,'P0001','already_processed','double verify rejected');
reset role;
select is((select status::text from user_membership_packages where user_membership_package_id=1),'active','membership active');
select ok((select ends_at - starts_at from user_membership_packages where user_membership_package_id=1) = interval '30 days','ends_at = starts_at + 30d');

-- staff cannot verify own payment
insert into user_membership_packages(user_id,membership_package_id) values ('00000000-0000-0000-0000-0000000000e1',1);
insert into payments(qr,amount,user_id,user_membership_package_id) values ('qr-e',30,'00000000-0000-0000-0000-0000000000e1',2);
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000e1","role":"authenticated"}',true);
set local role authenticated;
select throws_ok($$select verify_payment(2,true)$$,'P0001','self_verify','cannot verify own payment');
reset role;

-- renewal after expiry extends from now, not from the past end date
update user_membership_packages set ends_at = now() - interval '10 days', status='expired' where user_membership_package_id=1;
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000a1","role":"authenticated"}',true);
set local role authenticated;
select lives_ok($$select renew_membership(1)$$,'renew expired membership');
select throws_ok($$select renew_membership(1)$$,'P0001','renewal_pending','second pending renewal rejected');
reset role;
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000e1","role":"authenticated"}',true);
set local role authenticated;
select verify_payment((select payment_id from payments where user_id='00000000-0000-0000-0000-0000000000a1' order by payment_id desc limit 1), true);
reset role;
select ok((select ends_at from user_membership_packages where user_membership_package_id=1) > now() + interval '29 days','renewal extends from now');

select * from finish();
rollback;
```

- [ ] **Step 2: Run — expect FAIL** (functions do not exist).

- [ ] **Step 3: Write the migration**

`supabase/migrations/20261004000400_rpc_payments.sql`:
```sql
create function public.notify(p_user uuid, p_type text, p_title text, p_body text default null, p_ref jsonb default null)
returns void language sql security definer set search_path = public as $$
  insert into user_notifications(user_id, type, title, body, reference)
  values (p_user, p_type, p_title, p_body, p_ref::text);
$$;
create function public.write_audit(p_action text, p_entity text, p_entity_id text, p_details jsonb default null)
returns void language sql security definer set search_path = public as $$
  insert into audit_log(actor_id, action, entity, entity_id, details) values (auth.uid(), p_action, p_entity, p_entity_id, p_details);
$$;
revoke execute on function public.notify, public.write_audit from public, anon, authenticated;

create function public.avail_membership(p_package_id int) returns int
language plpgsql security definer set search_path = public as $$
declare pkg membership_packages; ump int; pay int;
begin
  if auth.uid() is null then raise exception 'forbidden' using errcode = '42501'; end if;
  select * into pkg from membership_packages where membership_package_id = p_package_id and is_active;
  if not found then raise exception 'not_found'; end if;
  if exists (select 1 from user_membership_packages where user_id = auth.uid()
             and membership_package_id = p_package_id and status in ('pending','active')) then
    raise exception 'already_availed';
  end if;
  insert into user_membership_packages(user_id, membership_package_id)
    values (auth.uid(), p_package_id) returning user_membership_package_id into ump;
  insert into payments(qr, amount, currency, user_id, user_membership_package_id)
    values (gen_random_uuid()::text, pkg.price, pkg.currency, auth.uid(), ump) returning payment_id into pay;
  return pay;
end $$;

create function public.renew_membership(p_ump_id int) returns int
language plpgsql security definer set search_path = public as $$
declare ump user_membership_packages; pkg membership_packages; pay int;
begin
  select * into ump from user_membership_packages
    where user_membership_package_id = p_ump_id and user_id = auth.uid();
  if not found then raise exception 'not_found'; end if;
  if ump.status in ('pending','cancelled') then raise exception 'not_renewable'; end if;
  if exists (select 1 from membership_renewals where user_membership_package_id = p_ump_id and status = 'pending') then
    raise exception 'renewal_pending';
  end if;
  select * into pkg from membership_packages where membership_package_id = ump.membership_package_id;
  insert into payments(qr, amount, currency, user_id, user_membership_package_id)
    values (gen_random_uuid()::text, pkg.price, pkg.currency, auth.uid(), p_ump_id) returning payment_id into pay;
  insert into membership_renewals(user_membership_package_id, payment_id, new_starts_at, new_ends_at, amount, currency)
    values (p_ump_id, pay, now(), now() + make_interval(days => pkg.duration_days), pkg.price, pkg.currency);
  return pay;
end $$;

create function public.verify_payment(p_payment_id int, p_approve boolean) returns void
language plpgsql security definer set search_path = public as $$
declare
  pay payments; ump user_membership_packages; pkg membership_packages; ren membership_renewals;
  is_renewal boolean := false; base_ts timestamptz; new_end timestamptz;
begin
  if not has_permission('payments:verify') then raise exception 'forbidden' using errcode = '42501'; end if;
  select * into pay from payments where payment_id = p_payment_id for update;
  if not found then raise exception 'not_found'; end if;
  if pay.status <> 'pending' then raise exception 'already_processed'; end if;
  if pay.user_id = auth.uid() then raise exception 'self_verify'; end if;

  update payments set status = (case when p_approve then 'verified' else 'rejected' end)::payment_status,
         verified_by = auth.uid(), verified_at = now()
   where payment_id = p_payment_id;

  if pay.user_membership_package_id is not null then
    select * into ren from membership_renewals where payment_id = p_payment_id for update;
    is_renewal := found;
    select * into ump from user_membership_packages
      where user_membership_package_id = pay.user_membership_package_id for update;
    select * into pkg from membership_packages where membership_package_id = ump.membership_package_id;
    if p_approve then
      if is_renewal then
        base_ts := greatest(coalesce(ump.ends_at, now()), now());
        new_end := base_ts + make_interval(days => pkg.duration_days);
        update membership_renewals set status = 'completed', new_starts_at = base_ts, new_ends_at = new_end, renewed_at = now()
          where renewal_id = ren.renewal_id;
        update user_membership_packages set status = 'active', ends_at = new_end
          where user_membership_package_id = ump.user_membership_package_id;
      else
        update user_membership_packages set status = 'active', starts_at = now(),
               ends_at = now() + make_interval(days => pkg.duration_days)
          where user_membership_package_id = ump.user_membership_package_id;
      end if;
    elsif is_renewal then
      update membership_renewals set status = 'cancelled' where renewal_id = ren.renewal_id;
    else
      update user_membership_packages set status = 'cancelled'
        where user_membership_package_id = ump.user_membership_package_id;
    end if;
  end if;

  perform notify(pay.user_id, 'payment', case when p_approve then 'Payment verified' else 'Payment rejected' end,
                 null, jsonb_build_object('payment_id', pay.payment_id));
  perform write_audit(case when p_approve then 'payment.verify' else 'payment.reject' end,
                      'payments', pay.payment_id::text, jsonb_build_object('amount', pay.amount));
end $$;
```

- [ ] **Step 4: Run — expect PASS** (`supabase db reset && supabase test db`; `03_rpc_payments` 11/11).

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(db): membership, payment, renewal rpcs"`

---

### Task 5: RPCs — sessions, attendance, amenities, admin, expiry; storage; cron

**Files:**
- Create: `supabase/migrations/20261004000500_rpc_sessions.sql`, `supabase/migrations/20261004000600_storage_cron.sql`
- Test: `supabase/tests/database/04_rpc_sessions.test.sql`

**Interfaces:**
- Produces (SQL): `respond_time_request(p_id int, p_approve boolean)`, `record_session_attendance(p_session_id int, p_member_id int, p_status attendance_status)`, `record_check_in(p_member_id int)`, `avail_amenity(p_amenity_id int) → int`, `verify_amenity_usage(p_id int, p_approve boolean)`, `assign_role(p_user uuid, p_role text)`, `revoke_role(p_user uuid, p_role text)`, `set_user_active(p_user uuid, p_active boolean)`, `revenue_summary(p_from date, p_to date) → table(day date, cur char(3), total numeric)`, `expire_memberships()`. Trigger `notify_time_request`. Storage buckets `payment-proofs`, `amenity-proofs` (object path `"{auth.uid()}/{file}"`).

- [ ] **Step 1: Write the failing test (Review Focus #4)**

`supabase/tests/database/04_rpc_sessions.test.sql`:
```sql
begin;
select plan(10);
grant all on table __tresults__ to authenticated;
grant all on sequence __tresults___numb_seq to authenticated;

insert into auth.users(id,email) values
  ('00000000-0000-0000-0000-0000000000a1','alice@x.io'),
  ('00000000-0000-0000-0000-0000000000a2','bob@x.io'),
  ('00000000-0000-0000-0000-0000000000c1','coach@x.io'),
  ('00000000-0000-0000-0000-0000000000e1','emp@x.io');
insert into user_roles(user_id, role_id) select '00000000-0000-0000-0000-0000000000e1', role_id from roles where name='employee';
insert into coaches(user_id) values ('00000000-0000-0000-0000-0000000000c1');
insert into membership_packages(name,price,duration_days) values ('Monthly',30,30);
insert into user_membership_packages(user_id,membership_package_id,status,starts_at,ends_at)
  values ('00000000-0000-0000-0000-0000000000a1',1,'active',now(),now()+interval '30 days');
insert into coach_hires(member_id,coach_id)
  select member_id,1 from members where user_id='00000000-0000-0000-0000-0000000000a1';

-- alice requests a slot; coach approves; bob cannot approve
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000a1","role":"authenticated"}',true);
set local role authenticated;
insert into time_requests(coach_id,member_id,requested_by,requested_start,requested_end)
  values (1,(select current_member_id()),'member','2026-12-01 10:00+00','2026-12-01 11:00+00');
select throws_ok($$select respond_time_request(1,true)$$,'42501','forbidden','requester cannot approve own request');
reset role;

select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000c1","role":"authenticated"}',true);
set local role authenticated;
select lives_ok($$select respond_time_request(1,true)$$,'coach approves');
reset role;
select is((select count(*)::int from training_sessions),1,'session created on approval');

-- overlapping second request cannot be approved; back-to-back can
insert into time_requests(coach_id,member_id,requested_by,requested_start,requested_end) values
  (1,(select member_id from members where user_id='00000000-0000-0000-0000-0000000000a1'),'member','2026-12-01 10:30+00','2026-12-01 11:30+00'),
  (1,(select member_id from members where user_id='00000000-0000-0000-0000-0000000000a1'),'member','2026-12-01 11:00+00','2026-12-01 12:00+00');
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000c1","role":"authenticated"}',true);
set local role authenticated;
select throws_ok($$select respond_time_request(2,true)$$,'P0001','overlap','overlap rejected');
select lives_ok($$select respond_time_request(3,true)$$,'back-to-back approved');
reset role;

-- attendance and check-in
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000c1","role":"authenticated"}',true);
set local role authenticated;
select lives_ok($$select record_session_attendance(1,(select member_id from members where user_id='00000000-0000-0000-0000-0000000000a1'),'present')$$,'coach records attendance');
reset role;
select set_config('request.jwt.claims','{"sub":"00000000-0000-0000-0000-0000000000e1","role":"authenticated"}',true);
set local role authenticated;
select lives_ok($$select record_check_in((select member_id from members where user_id='00000000-0000-0000-0000-0000000000a1'))$$,'employee checks in active member');
select throws_ok($$select record_check_in((select member_id from members where user_id='00000000-0000-0000-0000-0000000000a2'))$$,'P0001','no_active_membership','check-in blocked without membership');
reset role;

-- expiry job
update user_membership_packages set ends_at = now() - interval '1 hour' where user_membership_package_id=1;
select expire_memberships();
select is((select status::text from user_membership_packages where user_membership_package_id=1),'expired','expire_memberships flips status');

select * from finish();
rollback;
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Write `20261004000500_rpc_sessions.sql`**

```sql
create function public.notify_time_request() returns trigger
language plpgsql security definer set search_path = public as $$
declare target uuid;
begin
  if new.requested_by = 'member' then
    select user_id into target from coaches where coach_id = new.coach_id;
  elsif new.member_id is not null then
    select user_id into target from members where member_id = new.member_id;
  end if;
  if target is not null then
    perform notify(target, 'time_request', 'New time request', null, jsonb_build_object('time_request_id', new.time_request_id));
  end if;
  return new;
end $$;
create trigger time_requests_notify after insert on time_requests for each row execute function notify_time_request();

create function public.respond_time_request(p_id int, p_approve boolean) returns void
language plpgsql security definer set search_path = public as $$
declare r time_requests; c coaches; me_member int := current_member_id(); requester uuid;
begin
  select * into r from time_requests where time_request_id = p_id for update;
  if not found then raise exception 'not_found'; end if;
  if r.status <> 'pending' then raise exception 'already_processed'; end if;
  select * into c from coaches where coach_id = r.coach_id;

  if r.requested_by = 'member' then
    if c.user_id <> auth.uid() then raise exception 'forbidden' using errcode = '42501'; end if;
    select user_id into requester from members where member_id = r.member_id;
  else
    if r.member_id is null then
      if me_member is null or not exists (select 1 from coach_hires h where h.coach_id = r.coach_id
           and h.member_id = me_member and h.status = 'active') then
        raise exception 'forbidden' using errcode = '42501';
      end if;
      r.member_id := me_member;
    elsif r.member_id is distinct from me_member then
      raise exception 'forbidden' using errcode = '42501';
    end if;
    requester := c.user_id;
  end if;

  if p_approve then
    begin
      insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, title)
        values (r.coach_id, r.member_id, r.requested_start, r.requested_end, 'Training session');
    exception when exclusion_violation then
      raise exception 'overlap';
    end;
    update time_requests set status = 'approved', member_id = r.member_id, responded_at = now() where time_request_id = p_id;
  else
    update time_requests set status = 'rejected', responded_at = now() where time_request_id = p_id;
  end if;
  perform notify(requester, 'time_request', case when p_approve then 'Time request approved' else 'Time request rejected' end,
                 null, jsonb_build_object('time_request_id', p_id));
end $$;

create function public.record_session_attendance(p_session_id int, p_member_id int, p_status attendance_status) returns void
language plpgsql security definer set search_path = public as $$
declare s training_sessions;
begin
  select * into s from training_sessions where training_session_id = p_session_id;
  if not found then raise exception 'not_found'; end if;
  if not (has_permission('attendance:record') or s.coach_id = current_coach_id()) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
  if s.member_id <> p_member_id then raise exception 'wrong_member'; end if;
  insert into session_attendance(training_session_id, member_id, status, checked_in_at, recorded_by)
    values (p_session_id, p_member_id, p_status, case when p_status in ('present','late') then now() end, auth.uid())
  on conflict (training_session_id, member_id) do update
    set status = excluded.status, checked_in_at = excluded.checked_in_at, recorded_by = excluded.recorded_by;
end $$;

create function public.record_check_in(p_member_id int) returns void
language plpgsql security definer set search_path = public as $$
declare uid uuid;
begin
  if not has_permission('checkins:record') then raise exception 'forbidden' using errcode = '42501'; end if;
  select user_id into uid from members where member_id = p_member_id;
  if not found then raise exception 'not_found'; end if;
  if not has_active_membership(uid) then raise exception 'no_active_membership'; end if;
  insert into check_ins(member_id, recorded_by) values (p_member_id, auth.uid());
end $$;

create function public.avail_amenity(p_amenity_id int) returns int
language plpgsql security definer set search_path = public as $$
declare ua int;
begin
  if auth.uid() is null then raise exception 'forbidden' using errcode = '42501'; end if;
  if not has_active_membership(auth.uid()) then raise exception 'no_active_membership'; end if;
  if not exists (select 1 from amenities where amenity_id = p_amenity_id and is_active) then raise exception 'not_found'; end if;
  insert into user_amenities(user_id, amenity_id) values (auth.uid(), p_amenity_id)
  on conflict (user_id, amenity_id) do update set availed_at = user_amenities.availed_at
  returning user_amenity_id into ua;
  return ua;
end $$;

create function public.verify_amenity_usage(p_id int, p_approve boolean) returns void
language plpgsql security definer set search_path = public as $$
declare u amenity_usages; owner uuid;
begin
  if not has_permission('amenities:verify') then raise exception 'forbidden' using errcode = '42501'; end if;
  select * into u from amenity_usages where amenity_usage_id = p_id for update;
  if not found then raise exception 'not_found'; end if;
  if u.status <> 'pending' then raise exception 'already_processed'; end if;
  update amenity_usages set status = (case when p_approve then 'verified' else 'rejected' end)::amenity_usage_status,
         verified_by = auth.uid(), verified_at = now() where amenity_usage_id = p_id;
  select user_id into owner from user_amenities where user_amenity_id = u.user_amenity_id;
  perform notify(owner, 'attendance', case when p_approve then 'Amenity usage verified' else 'Amenity usage rejected' end);
  perform write_audit(case when p_approve then 'amenity_usage.verify' else 'amenity_usage.reject' end, 'amenity_usages', p_id::text);
end $$;

create function public.assign_role(p_user uuid, p_role text) returns void
language plpgsql security definer set search_path = public as $$
begin
  if not has_permission('users:manage') then raise exception 'forbidden' using errcode = '42501'; end if;
  insert into user_roles(user_id, role_id) select p_user, role_id from roles where name = p_role
    on conflict do nothing;
  if not found and not exists (select 1 from roles where name = p_role) then raise exception 'not_found'; end if;
  if p_role = 'coach' then insert into coaches(user_id) values (p_user) on conflict do nothing; end if;
  perform write_audit('role.assign', 'user_roles', p_user::text, jsonb_build_object('role', p_role));
end $$;

create function public.revoke_role(p_user uuid, p_role text) returns void
language plpgsql security definer set search_path = public as $$
begin
  if not has_permission('users:manage') then raise exception 'forbidden' using errcode = '42501'; end if;
  if p_user = auth.uid() and p_role = 'admin' then raise exception 'forbidden' using errcode = '42501'; end if;
  delete from user_roles where user_id = p_user and role_id = (select role_id from roles where name = p_role);
  perform write_audit('role.revoke', 'user_roles', p_user::text, jsonb_build_object('role', p_role));
end $$;

create function public.set_user_active(p_user uuid, p_active boolean) returns void
language plpgsql security definer set search_path = public as $$
begin
  if not has_permission('users:manage') then raise exception 'forbidden' using errcode = '42501'; end if;
  update profiles set is_active = p_active where id = p_user;
  perform write_audit('user.active', 'profiles', p_user::text, jsonb_build_object('is_active', p_active));
end $$;

create function public.revenue_summary(p_from date, p_to date)
returns table(day date, cur char(3), total numeric)
language plpgsql security definer set search_path = public as $$
begin
  if not has_permission('revenue:read') then raise exception 'forbidden' using errcode = '42501'; end if;
  return query
    select p.verified_at::date, p.currency, sum(p.amount)
    from payments p
    where p.status = 'verified' and p.verified_at::date between p_from and p_to
    group by 1, 2 order by 1;
end $$;

create function public.expire_memberships() returns void
language plpgsql security definer set search_path = public as $$
begin
  update user_membership_packages set status = 'expired' where status = 'active' and ends_at < now();
  insert into user_notifications(user_id, type, title, reference)
  select u.user_id, 'membership', 'Membership expires in ' || d.days || ' day(s)',
         jsonb_build_object('user_membership_package_id', u.user_membership_package_id, 'reminder_days', d.days)::text
  from user_membership_packages u cross join (values (7), (1)) as d(days)
  where u.status = 'active' and u.ends_at::date = current_date + d.days
    and not exists (select 1 from user_notifications n where n.user_id = u.user_id and n.type = 'membership'
      and n.reference = jsonb_build_object('user_membership_package_id', u.user_membership_package_id, 'reminder_days', d.days)::text);
end $$;
revoke execute on function public.expire_memberships() from public, anon, authenticated;
```

- [ ] **Step 4: Write `20261004000600_storage_cron.sql`**

```sql
insert into storage.buckets(id, name, public) values ('payment-proofs','payment-proofs',false), ('amenity-proofs','amenity-proofs',false)
on conflict do nothing;

create policy proofs_insert_own on storage.objects for insert to authenticated
  with check (bucket_id in ('payment-proofs','amenity-proofs') and (storage.foldername(name))[1] = auth.uid()::text);
create policy proofs_select_own on storage.objects for select to authenticated
  using (bucket_id in ('payment-proofs','amenity-proofs') and (storage.foldername(name))[1] = auth.uid()::text);
create policy proofs_select_staff on storage.objects for select to authenticated
  using ((bucket_id = 'payment-proofs' and has_permission('payments:verify'))
      or (bucket_id = 'amenity-proofs' and has_permission('amenities:verify')));

do $$ begin
  create extension if not exists pg_cron;
  perform cron.schedule('expire-memberships', '5 0 * * *', 'select public.expire_memberships()');
exception when others then
  raise notice 'pg_cron unavailable (%); schedule expire_memberships() manually in the dashboard', sqlerrm;
end $$;
```

- [ ] **Step 5: Run — expect PASS** (`supabase db reset && supabase test db`; all four files green).

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(db): session, attendance, amenity, admin rpcs; storage; cron"`

---

# PART B — Client (.NET MAUI)

### Task 6: Solution scaffold, Core models, gateway, error mapping

**Files:**
- Create: `GymMembership.slnx`, `src/GymMembership.Core/*`, `src/GymMembership.App/*` (from template), `tests/GymMembership.Tests/*`
- Create: `src/GymMembership.Core/Models/Status.cs`, `Models/Models.cs`, `Services/AppResult.cs`, `Services/ErrorMapper.cs`, `Services/ISupabaseGateway.cs`, `Services/SupabaseGateway.cs`, `ViewModels/LoadableViewModel.cs`
- Test: `tests/GymMembership.Tests/ErrorMapperTests.cs`, `tests/GymMembership.Tests/FakeGateway.cs`

**Interfaces:**
- Produces:
  - `enum AppErrorKind { None, Offline, Unauthorized, Conflict, Validation, Unknown }`
  - `record AppResult<T>(T? Value, AppErrorKind Error = AppErrorKind.None, string? Message = null)` with `bool Ok`, `static AppResult<T> Success(T)`, `static AppResult<T> Fail(AppErrorKind, string?)`
  - `static class Safe { Task<AppResult<T>> RunAsync<T>(Func<Task<T>>) }`
  - `static class ErrorMapper { AppErrorKind Map(Exception) }`, `static class ErrorText { string For(AppErrorKind) }`
  - `ISupabaseGateway` (below) and `abstract partial class LoadableViewModel : ObservableObject` with `IsBusy`, `ErrorMessage`, `Task<bool> RunAsync<T>(Func<Task<AppResult<T>>>, Action<T>)`.

- [ ] **Step 1: Scaffold projects**

```bash
cd C:/Users/bowie/OneDrive/Desktop/projects/gym-membership
dotnet new sln -n GymMembership
dotnet new classlib -n GymMembership.Core -o src/GymMembership.Core -f net10.0
dotnet new maui -n GymMembership.App -o src/GymMembership.App
dotnet new xunit -n GymMembership.Tests -o tests/GymMembership.Tests -f net10.0
dotnet sln add src/GymMembership.Core src/GymMembership.App tests/GymMembership.Tests
dotnet add src/GymMembership.Core package Supabase   # NuGet id is "Supabase" (repo name: supabase-csharp); 8.2.0 at scaffold time
dotnet add src/GymMembership.Core package CommunityToolkit.Mvvm
dotnet add src/GymMembership.App package CommunityToolkit.Maui
dotnet add src/GymMembership.App reference src/GymMembership.Core
dotnet add tests/GymMembership.Tests reference src/GymMembership.Core
rm src/GymMembership.Core/Class1.cs
```
In `src/GymMembership.App/GymMembership.App.csproj` set `<TargetFrameworks>net10.0-android;net10.0-ios;net10.0-windows10.0.19041.0</TargetFrameworks>` (drop `maccatalyst` and gate iOS on macOS: `Condition="!$([MSBuild]::IsOSPlatform('windows'))"` is already in the template — keep it) and `<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">10.0.17763.0</SupportedOSPlatformVersion>`.
Add `<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>` to the Core csproj if missing.
Run: `dotnet build src/GymMembership.Core` → Expected: Build succeeded. Record the resolved `supabase-csharp` and MAUI versions in `docs/specs/technical-spec.md` §1 ("Pin exact package versions").

- [ ] **Step 2: Write the failing test**

`tests/GymMembership.Tests/ErrorMapperTests.cs`:
```csharp
using GymMembership.Core.Services;
using Xunit;

public class ErrorMapperTests
{
    [Theory]
    [InlineData("forbidden", AppErrorKind.Unauthorized)]
    [InlineData("Invalid login credentials", AppErrorKind.Unauthorized)]
    [InlineData("already_processed", AppErrorKind.Conflict)]
    [InlineData("overlap", AppErrorKind.Conflict)]
    [InlineData("duplicate key value violates unique constraint", AppErrorKind.Conflict)]
    [InlineData("no_active_membership", AppErrorKind.Validation)]
    [InlineData("self_verify", AppErrorKind.Validation)]
    [InlineData("something odd", AppErrorKind.Unknown)]
    public void Maps_message_tokens(string message, AppErrorKind expected) =>
        Assert.Equal(expected, ErrorMapper.Map(new Exception(message)));

    [Fact]
    public void Maps_network_failures_to_offline() =>
        Assert.Equal(AppErrorKind.Offline, ErrorMapper.Map(new HttpRequestException("no route")));

    [Fact]
    public async Task Safe_wraps_exceptions()
    {
        var r = await Safe.RunAsync<int>(() => throw new Exception("overlap"));
        Assert.False(r.Ok);
        Assert.Equal(AppErrorKind.Conflict, r.Error);
    }
}
```

- [ ] **Step 3: Run — expect FAIL** (`dotnet test tests/GymMembership.Tests` → compile error, types missing).

- [ ] **Step 4: Implement**

`src/GymMembership.Core/Services/AppResult.cs`:
```csharp
namespace GymMembership.Core.Services;

public enum AppErrorKind { None, Offline, Unauthorized, Conflict, Validation, Unknown }

public sealed record AppResult<T>(T? Value, AppErrorKind Error = AppErrorKind.None, string? Message = null)
{
    public bool Ok => Error == AppErrorKind.None;
    public static AppResult<T> Success(T value) => new(value);
    public static AppResult<T> Fail(AppErrorKind kind, string? message = null) => new(default, kind, message);
}

public static class Safe
{
    public static async Task<AppResult<T>> RunAsync<T>(Func<Task<T>> work)
    {
        try { return AppResult<T>.Success(await work()); }
        catch (Exception ex) { return AppResult<T>.Fail(ErrorMapper.Map(ex), ex.Message); }
    }
}

public static class ErrorText
{
    public static string For(AppErrorKind kind) => kind switch
    {
        AppErrorKind.Offline => "You appear to be offline. Check your connection and try again.",
        AppErrorKind.Unauthorized => "You are not allowed to do that, or your session expired. Please sign in again.",
        AppErrorKind.Conflict => "That conflicts with an existing record. Refresh and try again.",
        AppErrorKind.Validation => "That action isn't allowed right now (for example, no active membership).",
        _ => "Something went wrong. Please try again."
    };
}
```
`src/GymMembership.Core/Services/ErrorMapper.cs`:
```csharp
namespace GymMembership.Core.Services;

public static class ErrorMapper
{
    public static AppErrorKind Map(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException or TimeoutException => AppErrorKind.Offline,
        _ => FromMessage(ex.Message)
    };

    static AppErrorKind FromMessage(string message)
    {
        var m = message.ToLowerInvariant();
        if (Has(m, "forbidden", "42501", "jwt", "row-level security", "invalid login", "not authenticated")) return AppErrorKind.Unauthorized;
        if (Has(m, "already_processed", "already_availed", "renewal_pending", "overlap", "23505", "23p01", "duplicate key")) return AppErrorKind.Conflict;
        if (Has(m, "no_active_membership", "self_verify", "not_renewable", "wrong_member", "not_found", "23514")) return AppErrorKind.Validation;
        return AppErrorKind.Unknown;
    }

    static bool Has(string m, params string[] tokens) => tokens.Any(m.Contains);
}
```
`src/GymMembership.Core/Services/ISupabaseGateway.cs`:
```csharp
using System.Linq.Expressions;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Services;

public interface ISupabaseGateway
{
    string? CurrentUserId { get; }
    Task InitializeAsync();
    Task SignInAsync(string email, string password);
    Task SignUpAsync(string email, string password, string username);
    Task SignOutAsync();
    Task<bool> RestoreSessionAsync();

    Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new();
    Task<T> InsertAsync<T>(T row) where T : BaseModel, new();
    Task UpdateAsync<T>(T row) where T : BaseModel, new();
    Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null);
    Task RpcAsync(string function, Dictionary<string, object>? args = null);
    Task<string> UploadAsync(string bucket, string path, byte[] data);
    Task SubscribeInsertsAsync<T>(Action<T> onInsert) where T : BaseModel, new();
}
```
`src/GymMembership.Core/Services/SupabaseGateway.cs`:
```csharp
using System.Linq.Expressions;
using Supabase;
using Supabase.Postgrest.Models;
using static Supabase.Postgrest.Constants;

namespace GymMembership.Core.Services;

public sealed class SupabaseGateway(Client client) : ISupabaseGateway
{
    public string? CurrentUserId => client.Auth.CurrentUser?.Id;

    public async Task InitializeAsync() => await client.InitializeAsync();
    public async Task SignInAsync(string email, string password) => await client.Auth.SignIn(email, password);
    public async Task SignUpAsync(string email, string password, string username) =>
        await client.Auth.SignUp(email, password, new Supabase.Gotrue.SignUpOptions
        { Data = new Dictionary<string, object> { ["username"] = username } });
    public async Task SignOutAsync() => await client.Auth.SignOut();

    public async Task<bool> RestoreSessionAsync()
    {
        var session = await client.Auth.RetrieveSessionAsync();
        return session?.User is not null;
    }

    public async Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new()
    {
        var table = client.From<T>();
        var res = where is null ? await table.Get() : await table.Where(where).Get();
        return res.Models;
    }

    public async Task<T> InsertAsync<T>(T row) where T : BaseModel, new() =>
        (await client.From<T>().Insert(row)).Models.First();

    public async Task UpdateAsync<T>(T row) where T : BaseModel, new() => await client.From<T>().Update(row);

    public async Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null) =>
        (await client.Rpc<TResult>(function, args))!;

    public async Task RpcAsync(string function, Dictionary<string, object>? args = null) => await client.Rpc(function, args);

    public async Task<string> UploadAsync(string bucket, string path, byte[] data)
    {
        await client.Storage.From(bucket).Upload(data, path);
        return path;
    }

    public async Task SubscribeInsertsAsync<T>(Action<T> onInsert) where T : BaseModel, new() =>
        await client.From<T>().On(Supabase.Realtime.PostgresChanges.PostgresChangesOptions.ListenType.Inserts,
            (_, change) => { var m = change.Model<T>(); if (m is not null) onInsert(m); });
}
```
Note: if a method/namespace name differs in the version pinned in Step 1, check it with Context7 (`/supabase-community/supabase-csharp`) and adjust the `using`/call only; the `ISupabaseGateway` contract does not change.

`src/GymMembership.Core/ViewModels/LoadableViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public abstract partial class LoadableViewModel : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;

    protected async Task<bool> RunAsync<T>(Func<Task<AppResult<T>>> operation, Action<T> onOk)
    {
        IsBusy = true; ErrorMessage = null;
        try
        {
            var result = await operation();
            if (result.Ok) { onOk(result.Value!); return true; }
            ErrorMessage = ErrorText.For(result.Error);
            return false;
        }
        finally { IsBusy = false; }
    }
}
```
`tests/GymMembership.Tests/FakeGateway.cs`:
```csharp
using System.Linq.Expressions;
using GymMembership.Core.Services;
using Supabase.Postgrest.Models;

public sealed class FakeGateway : ISupabaseGateway
{
    public string? CurrentUserId { get; set; } = "user-1";
    public Dictionary<Type, List<object>> Tables { get; } = new();
    public List<(string Fn, Dictionary<string, object>? Args)> RpcCalls { get; } = new();
    public Dictionary<string, object?> RpcResults { get; } = new();
    public Exception? Throw { get; set; }
    public Action<object>? InsertHandler { get; set; }
    public List<(string Bucket, string Path)> Uploads { get; } = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public bool SessionRestorable { get; set; }
    public Task SignInAsync(string email, string password) => Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    public Task SignUpAsync(string email, string password, string username) => Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    public Task SignOutAsync() => Task.CompletedTask;
    public Task<bool> RestoreSessionAsync() => Throw is null ? Task.FromResult(SessionRestorable) : Task.FromException<bool>(Throw);

    public void Seed<T>(params T[] rows) where T : BaseModel => Tables[typeof(T)] = rows.Cast<object>().ToList();

    public Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new()
    {
        if (Throw is not null) return Task.FromException<IReadOnlyList<T>>(Throw);
        var rows = Tables.TryGetValue(typeof(T), out var l) ? l.Cast<T>() : Enumerable.Empty<T>();
        if (where is not null) rows = rows.Where(where.Compile());
        return Task.FromResult<IReadOnlyList<T>>(rows.ToList());
    }
    public Task<T> InsertAsync<T>(T row) where T : BaseModel, new()
    {
        if (Throw is not null) return Task.FromException<T>(Throw);
        InsertHandler?.Invoke(row);
        if (!Tables.TryGetValue(typeof(T), out var l)) Tables[typeof(T)] = l = new();
        l.Add(row);
        return Task.FromResult(row);
    }
    public Task UpdateAsync<T>(T row) where T : BaseModel, new() => Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    public Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null)
    {
        RpcCalls.Add((function, args));
        if (Throw is not null) return Task.FromException<TResult>(Throw);
        return Task.FromResult(RpcResults.TryGetValue(function, out var v) ? (TResult)v! : default!);
    }
    public Task RpcAsync(string function, Dictionary<string, object>? args = null)
    {
        RpcCalls.Add((function, args));
        return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    }
    public Task<string> UploadAsync(string bucket, string path, byte[] data) { Uploads.Add((bucket, path)); return Task.FromResult(path); }
    public Action<object>? InsertSubscriber { get; private set; }
    public Task SubscribeInsertsAsync<T>(Action<T> onInsert) where T : BaseModel, new()
    { InsertSubscriber = o => onInsert((T)o); return Task.CompletedTask; }
}
```
`src/GymMembership.Core/Models/Status.cs`:
```csharp
namespace GymMembership.Core.Models;

public static class Status
{
    public const string Pending = "pending", Verified = "verified", Rejected = "rejected", Expired = "expired",
        Active = "active", Cancelled = "cancelled", Completed = "completed", Approved = "approved",
        Scheduled = "scheduled", NoShow = "no_show", Ended = "ended";
    public static class Role { public const string Admin = "admin", Employee = "employee", Coach = "coach", Member = "member"; }
    public static class Requester { public const string Member = "member", Coach = "coach"; }
    public static class Attendance { public const string Present = "present", Late = "late", Absent = "absent", Excused = "excused"; }
}
```
`src/GymMembership.Core/Models/Models.cs` (all models used by later tasks; `Supabase.Postgrest.Attributes` + `Models`):
```csharp
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Models;

[Table("profiles")] public class Profile : BaseModel
{ [PrimaryKey("id")] public string Id { get; set; } = ""; [Column("username")] public string Username { get; set; } = ""; [Column("is_active")] public bool IsActive { get; set; } }

[Table("roles")] public class Role : BaseModel
{ [PrimaryKey("role_id")] public int RoleId { get; set; } [Column("name")] public string Name { get; set; } = ""; }

[Table("user_roles")] public class UserRole : BaseModel
{ [PrimaryKey("user_role_id")] public int UserRoleId { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("role_id")] public int RoleId { get; set; } }

[Table("user_settings")] public class UserSettings : BaseModel
{ [PrimaryKey("user_setting_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = "";
  [Column("theme")] public string Theme { get; set; } = "system"; [Column("language")] public string Language { get; set; } = "en"; [Column("timezone")] public string Timezone { get; set; } = "UTC"; }

[Table("membership_packages")] public class MembershipPackage : BaseModel
{ [PrimaryKey("membership_package_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; [Column("currency")] public string Currency { get; set; } = "USD"; }

[Table("user_membership_packages")] public class UserMembershipPackage : BaseModel
{ [PrimaryKey("user_membership_package_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("membership_package_id")] public int MembershipPackageId { get; set; }
  [Column("starts_at")] public DateTime? StartsAt { get; set; } [Column("ends_at")] public DateTime? EndsAt { get; set; } [Column("status")] public string Status { get; set; } = ""; }

[Table("payments")] public class Payment : BaseModel
{ [PrimaryKey("payment_id")] public int Id { get; set; } [Column("qr")] public string Qr { get; set; } = ""; [Column("status")] public string Status { get; set; } = "";
  [Column("amount")] public decimal Amount { get; set; } [Column("currency")] public string Currency { get; set; } = "USD"; [Column("user_id")] public string UserId { get; set; } = "";
  [Column("user_membership_package_id")] public int? UserMembershipPackageId { get; set; } [Column("proof_path")] public string? ProofPath { get; set; }
  [Column("created_at")] public DateTime CreatedAt { get; set; } [Column("verified_at")] public DateTime? VerifiedAt { get; set; } }

[Table("amenities")] public class Amenity : BaseModel
{ [PrimaryKey("amenity_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; }

[Table("user_amenities")] public class UserAmenity : BaseModel
{ [PrimaryKey("user_amenity_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("amenity_id")] public int AmenityId { get; set; } }

[Table("amenity_usages")] public class AmenityUsage : BaseModel
{ [PrimaryKey("amenity_usage_id")] public int Id { get; set; } [Column("user_amenity_id")] public int UserAmenityId { get; set; } [Column("used_at")] public DateTime UsedAt { get; set; }
  [Column("proof")] public string? Proof { get; set; } [Column("status")] public string Status { get; set; } = "pending"; [Column("notes")] public string? Notes { get; set; } }

[Table("coaches")] public class Coach : BaseModel
{ [PrimaryKey("coach_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("bio")] public string? Bio { get; set; }
  [Column("specialty")] public string? Specialty { get; set; } [Column("hourly_rate")] public decimal? HourlyRate { get; set; } [Column("is_available")] public bool IsAvailable { get; set; } }

[Table("members")] public class Member : BaseModel
{ [PrimaryKey("member_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("goals")] public string? Goals { get; set; } }

[Table("coach_hires")] public class CoachHire : BaseModel
{ [PrimaryKey("coach_hire_id")] public int Id { get; set; } [Column("member_id")] public int MemberId { get; set; } [Column("coach_id")] public int CoachId { get; set; }
  [Column("status")] public string Status { get; set; } = "active"; [Column("ended_at")] public DateTime? EndedAt { get; set; } }

[Table("time_requests")] public class TimeRequest : BaseModel
{ [PrimaryKey("time_request_id")] public int Id { get; set; } [Column("coach_id")] public int CoachId { get; set; } [Column("member_id")] public int? MemberId { get; set; }
  [Column("requested_by")] public string RequestedBy { get; set; } = ""; [Column("requested_start")] public DateTime RequestedStart { get; set; } [Column("requested_end")] public DateTime RequestedEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("message")] public string? Message { get; set; } }

[Table("training_sessions")] public class TrainingSession : BaseModel
{ [PrimaryKey("training_session_id")] public int Id { get; set; } [Column("coach_id")] public int CoachId { get; set; } [Column("member_id")] public int MemberId { get; set; }
  [Column("title")] public string? Title { get; set; } [Column("scheduled_start")] public DateTime ScheduledStart { get; set; } [Column("scheduled_end")] public DateTime ScheduledEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "scheduled"; [Column("notes")] public string? Notes { get; set; } }

[Table("user_notifications")] public class UserNotification : BaseModel
{ [PrimaryKey("notification_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("type")] public string Type { get; set; } = "";
  [Column("title")] public string Title { get; set; } = ""; [Column("body")] public string? Body { get; set; } [Column("is_read")] public bool IsRead { get; set; } [Column("created_at")] public DateTime CreatedAt { get; set; } }

[Table("audit_log")] public class AuditEntry : BaseModel
{ [PrimaryKey("audit_id")] public int Id { get; set; } [Column("actor_id")] public string? ActorId { get; set; } [Column("action")] public string Action { get; set; } = "";
  [Column("entity")] public string Entity { get; set; } = ""; [Column("entity_id")] public string? EntityId { get; set; } [Column("created_at")] public DateTime CreatedAt { get; set; } }

public record RevenueRow(DateTime Day, string Cur, decimal Total);
```
Note: `RevenueRow` is deserialized from the `revenue_summary` RPC by property name; in `AdminService` (Task 15) the RPC result is read as `List<RevenueRow>` — property names are matched case-insensitively (`day`, `cur`, `total`).

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test tests/GymMembership.Tests`
Expected: ErrorMapper tests PASS (10 cases).

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(core): scaffold solution, models, gateway, error mapping"`

---

### Task 7: Auth service + session persistence + role resolution

**Files:**
- Create: `src/GymMembership.Core/Services/AuthService.cs`, `Services/RoleResolver.cs`, `ViewModels/LoginViewModel.cs`
- Test: `tests/GymMembership.Tests/AuthTests.cs`

**Interfaces:**
- Consumes: `ISupabaseGateway`, `Safe`, `LoadableViewModel`, models `UserRole`, `Role`.
- Produces:
  - `enum AppRole { Member, Coach, Employee, Admin }`
  - `IAuthService { Task<AppResult<bool>> SignInAsync(string email,string password); Task<AppResult<bool>> SignUpAsync(string email,string password,string username); Task SignOutAsync(); Task<AppResult<AppRole?>> RestoreAsync(); Task<AppResult<AppRole>> ResolveRoleAsync(); string? UserId {get;} }`
  - `RoleResolver.Pick(IEnumerable<string> roleNames) → AppRole` (admin > employee > coach > member).
  - `LoginViewModel(IAuthService, Action<AppRole> onSignedIn)` with `Email`, `Password`, `SignInCommand`.

- [ ] **Step 1: Write failing tests (Review Focus #5)**

`tests/GymMembership.Tests/AuthTests.cs`:
```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AuthTests
{
    [Theory]
    [InlineData(new[] { "member" }, AppRole.Member)]
    [InlineData(new[] { "member", "coach" }, AppRole.Coach)]
    [InlineData(new[] { "member", "coach", "employee" }, AppRole.Employee)]
    [InlineData(new[] { "member", "admin" }, AppRole.Admin)]
    [InlineData(new string[0], AppRole.Member)]
    public void Picks_highest_role(string[] names, AppRole expected) =>
        Assert.Equal(expected, RoleResolver.Pick(names));

    [Fact]
    public async Task Restore_offline_returns_error_not_exception()
    {
        var gw = new FakeGateway { Throw = new HttpRequestException("offline") };
        var r = await new AuthService(gw).RestoreAsync();
        Assert.Equal(AppErrorKind.Offline, r.Error);
    }

    [Fact]
    public async Task Restore_without_stored_session_returns_null_role()
    {
        var r = await new AuthService(new FakeGateway { SessionRestorable = false }).RestoreAsync();
        Assert.True(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Login_vm_sets_error_on_bad_credentials_and_stays_put()
    {
        var gw = new FakeGateway { Throw = new Exception("Invalid login credentials") };
        AppRole? navigated = null;
        var vm = new LoginViewModel(new AuthService(gw), r => navigated = r) { Email = "a@b.c", Password = "x" };
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Null(navigated);
        Assert.Contains("not allowed", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_vm_navigates_with_resolved_role()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new UserRole { UserId = "u1", RoleId = 3 }, new UserRole { UserId = "u1", RoleId = 1 });
        gw.Seed(new Role { RoleId = 1, Name = "member" }, new Role { RoleId = 3, Name = "coach" });
        AppRole? navigated = null;
        var vm = new LoginViewModel(new AuthService(gw), r => navigated = r) { Email = "a@b.c", Password = "x" };
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(AppRole.Coach, navigated);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (types missing).

- [ ] **Step 3: Implement**

`src/GymMembership.Core/Services/RoleResolver.cs`:
```csharp
namespace GymMembership.Core.Services;

public enum AppRole { Member, Coach, Employee, Admin }

public static class RoleResolver
{
    public static AppRole Pick(IEnumerable<string> names)
    {
        var set = names.ToHashSet();
        if (set.Contains("admin")) return AppRole.Admin;
        if (set.Contains("employee")) return AppRole.Employee;
        if (set.Contains("coach")) return AppRole.Coach;
        return AppRole.Member;
    }
}
```
`src/GymMembership.Core/Services/AuthService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface IAuthService
{
    string? UserId { get; }
    Task<AppResult<bool>> SignInAsync(string email, string password);
    Task<AppResult<bool>> SignUpAsync(string email, string password, string username);
    Task SignOutAsync();
    Task<AppResult<AppRole?>> RestoreAsync();
    Task<AppResult<AppRole>> ResolveRoleAsync();
}

public sealed class AuthService(ISupabaseGateway gw) : IAuthService
{
    public string? UserId => gw.CurrentUserId;

    public Task<AppResult<bool>> SignInAsync(string email, string password) =>
        Safe.RunAsync(async () => { await gw.SignInAsync(email, password); return true; });

    public Task<AppResult<bool>> SignUpAsync(string email, string password, string username) =>
        Safe.RunAsync(async () => { await gw.SignUpAsync(email, password, username); return true; });

    public Task SignOutAsync() => gw.SignOutAsync();

    public Task<AppResult<AppRole?>> RestoreAsync() => Safe.RunAsync<AppRole?>(async () =>
    {
        if (!await gw.RestoreSessionAsync()) return null;
        return await ResolveAsync();
    });

    public Task<AppResult<AppRole>> ResolveRoleAsync() => Safe.RunAsync(ResolveAsync);

    async Task<AppRole> ResolveAsync()
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var mine = await gw.ListAsync<UserRole>(u => u.UserId == uid);
        var roles = await gw.ListAsync<Role>();
        var names = mine.Select(m => roles.FirstOrDefault(r => r.RoleId == m.RoleId)?.Name).OfType<string>();
        return RoleResolver.Pick(names);
    }
}
```
`src/GymMembership.Core/ViewModels/LoginViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class LoginViewModel(IAuthService auth, Action<AppRole> onSignedIn) : LoadableViewModel
{
    [ObservableProperty] private string email = "";
    [ObservableProperty] private string password = "";

    [RelayCommand]
    private async Task SignInAsync()
    {
        var ok = await RunAsync(() => auth.SignInAsync(Email.Trim(), Password), _ => { });
        if (!ok) return;
        await RunAsync(auth.ResolveRoleAsync, onSignedIn);
    }
}
```

- [ ] **Step 4: Run — expect PASS** (`dotnet test tests/GymMembership.Tests`).

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(core): auth service, role resolution, login view model"`

---

### Task 8: MAUI host — DI, session storage, role Shells, login page

**Files:**
- Create: `src/GymMembership.App/Services/SecureSessionPersistence.cs`, `Services/AppConfig.cs`, `Views/LoginPage.xaml(.cs)`, `Views/ErrorBanner.xaml(.cs)`, `MemberShell.xaml(.cs)`, `CoachShell.xaml(.cs)`, `StaffShell.xaml(.cs)`, `Services/ShellNavigator.cs`
- Modify: `src/GymMembership.App/MauiProgram.cs`, `App.xaml.cs`, delete template `AppShell.xaml(.cs)`/`MainPage.xaml(.cs)`

**Interfaces:**
- Consumes: `IAuthService`, `LoginViewModel`, `ISupabaseGateway`.
- Produces: `ShellNavigator.GoTo(AppRole)` (sets the window root to the correct Shell); DI registrations; `ErrorBanner` ContentView with `Message` bindable property (used by every page).

No unit-testable logic here beyond Task 7 (platform wiring). Verification is a manual run.

- [ ] **Step 1: Config + persistence**

`src/GymMembership.App/Services/AppConfig.cs`:
```csharp
namespace GymMembership.App.Services;

// Local dev values from `supabase start`. Replace with the hosted project's URL + ANON key for release.
// Never put the service_role key here.
public static class AppConfig
{
    public const string SupabaseUrl = "http://10.0.2.2:54321"; // Android emulator → host; use http://127.0.0.1:54321 on Windows
    public const string SupabaseAnonKey = "<paste anon key from `supabase status`>";

    public static string Url => DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:54321" : "http://127.0.0.1:54321";
}
```
`src/GymMembership.App/Services/SecureSessionPersistence.cs`:
```csharp
using System.Text.Json;
using Supabase.Gotrue;
using Supabase.Gotrue.Interfaces;

namespace GymMembership.App.Services;

public sealed class SecureSessionPersistence : IGotrueSessionPersistence<Session>
{
    const string Key = "gym.supabase.session";

    public void SaveSession(Session session) =>
        SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(session)).GetAwaiter().GetResult();

    public void DestroySession() => SecureStorage.Default.Remove(Key);

    public Session? LoadSession()
    {
        try
        {
            var json = SecureStorage.Default.GetAsync(Key).GetAwaiter().GetResult();
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Session>(json);
        }
        catch { return null; } // corrupt/unavailable store → behave as signed out
    }
}
```
(If the pinned `supabase-csharp` exposes the interface under a different namespace or `SessionHandler` property name, adjust only the `using`/option name; the Save/Destroy/Load contract is the same.)

- [ ] **Step 2: DI in `MauiProgram.cs`**

```csharp
using CommunityToolkit.Maui;
using GymMembership.App.Services;
using GymMembership.App.Views;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Supabase;

namespace GymMembership.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseMauiCommunityToolkit();

        builder.Services.AddSingleton(_ => new Client(AppConfig.Url, AppConfig.SupabaseAnonKey,
            new SupabaseOptions { AutoRefreshToken = true, AutoConnectRealtime = true, SessionHandler = new SecureSessionPersistence() }));
        builder.Services.AddSingleton<ISupabaseGateway, SupabaseGateway>();
        builder.Services.AddSingleton<IAuthService, AuthService>();
        builder.Services.AddSingleton<ShellNavigator>();

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient(sp => new LoginViewModel(sp.GetRequiredService<IAuthService>(),
            role => sp.GetRequiredService<ShellNavigator>().GoTo(role)));
        // module registrations are appended in Tasks 9–16
        return builder.Build();
    }
}
```

- [ ] **Step 3: Navigator, shells, login page, banner**

`src/GymMembership.App/Services/ShellNavigator.cs`:
```csharp
using GymMembership.Core.Services;

namespace GymMembership.App.Services;

public sealed class ShellNavigator(IServiceProvider sp)
{
    public void GoTo(AppRole role)
    {
        Page page = role switch
        {
            AppRole.Admin or AppRole.Employee => new StaffShell(role),
            AppRole.Coach => new CoachShell(),
            _ => new MemberShell()
        };
        MainThread.BeginInvokeOnMainThread(() => Application.Current!.Windows[0].Page = page);
    }

    public void GoToLogin() =>
        MainThread.BeginInvokeOnMainThread(() => Application.Current!.Windows[0].Page = sp.GetRequiredService<Views.LoginPage>());
}
```
`App.xaml.cs`:
```csharp
using GymMembership.App.Services;
using GymMembership.Core.Services;

namespace GymMembership.App;

public partial class App(IServiceProvider sp) : Application
{
    protected override Window CreateWindow(IActivationState? state)
    {
        var window = new Window(new ContentPage { Content = new ActivityIndicator { IsRunning = true, VerticalOptions = LayoutOptions.Center } });
        _ = StartAsync(window);
        return window;
    }

    async Task StartAsync(Window window)
    {
        var nav = sp.GetRequiredService<ShellNavigator>();
        try
        {
            await sp.GetRequiredService<ISupabaseGateway>().InitializeAsync();
            var restored = await sp.GetRequiredService<IAuthService>().RestoreAsync();
            if (restored.Ok && restored.Value is { } role) nav.GoTo(role); else nav.GoToLogin();
        }
        catch { nav.GoToLogin(); } // offline / corrupt session → Login, never crash
    }
}
```
`Views/ErrorBanner.xaml`:
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="GymMembership.App.Views.ErrorBanner" x:Name="Root">
    <Border IsVisible="{Binding Message, Source={x:Reference Root}, Converter={StaticResource IsNotNullConverter}}"
            BackgroundColor="#FDECEA" Stroke="#D93025" Padding="12" Margin="0,0,0,8">
        <Label Text="{Binding Message, Source={x:Reference Root}}" TextColor="#B3261E" />
    </Border>
</ContentView>
```
`Views/ErrorBanner.xaml.cs`:
```csharp
namespace GymMembership.App.Views;

public partial class ErrorBanner : ContentView
{
    public static readonly BindableProperty MessageProperty =
        BindableProperty.Create(nameof(Message), typeof(string), typeof(ErrorBanner));
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public ErrorBanner() => InitializeComponent();
}
```
Register `IsNotNullConverter` in `App.xaml` resources: `<toolkit:IsNotNullConverter x:Key="IsNotNullConverter" />` with `xmlns:toolkit="http://schemas.microsoft.com/dotnet/2022/maui/toolkit"`.

`Views/LoginPage.xaml`:
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
             xmlns:v="clr-namespace:GymMembership.App.Views"
             x:Class="GymMembership.App.Views.LoginPage" x:DataType="vm:LoginViewModel" Title="Sign in">
    <VerticalStackLayout Padding="24" Spacing="12" MaximumWidthRequest="420" VerticalOptions="Center">
        <Label Text="Gym Membership" FontSize="28" FontAttributes="Bold" />
        <v:ErrorBanner Message="{Binding ErrorMessage}" />
        <Entry Placeholder="Email" Keyboard="Email" Text="{Binding Email}" />
        <Entry Placeholder="Password" IsPassword="True" Text="{Binding Password}" />
        <Button Text="Sign in" Command="{Binding SignInCommand}" IsEnabled="{Binding IsBusy, Converter={StaticResource InvertedBoolConverter}}" />
        <ActivityIndicator IsRunning="{Binding IsBusy}" />
    </VerticalStackLayout>
</ContentPage>
```
`Views/LoginPage.xaml.cs`:
```csharp
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class LoginPage : ContentPage
{
    public LoginPage(LoginViewModel vm) { InitializeComponent(); BindingContext = vm; }
}
```
Add `<toolkit:InvertedBoolConverter x:Key="InvertedBoolConverter" />` to `App.xaml` resources.

Shells (pages are added by later tasks; start each with a placeholder so the app runs):
`MemberShell.xaml.cs`: `public partial class MemberShell : Shell { public MemberShell() { InitializeComponent(); } }`; XAML: `<Shell ... x:Class="GymMembership.App.MemberShell" FlyoutBehavior="Disabled"><TabBar><ShellContent Title="Home" ContentTemplate="{DataTemplate local:LoginPage}" /></TabBar></Shell>` (temporary content, replaced in Task 9). Create `CoachShell` and `StaffShell` the same way (`StaffShell` takes `AppRole role` in its constructor and stores it for Task 16's admin-only tabs).

- [ ] **Step 4: Manual verification**

Run: `dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0` → Expected: Build succeeded.
Run the app (Windows target, local Supabase running). In Supabase Studio (`http://127.0.0.1:54323`) create a user `alice@x.io`/`password123`. Expected: Login page appears; wrong password shows the red banner; correct password lands on the member shell. Close and reopen: lands on the member shell without logging in. Stop `supabase stop` and reopen: lands on Login (no crash).

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(app): DI, secure session, role shells, login"`

---

### Task 9: Member — packages, payments, renewals, proof upload (F2, F3, F4)

**Files:**
- Create: `Core/Services/MembershipService.cs`, `Core/ViewModels/PackagesViewModel.cs`, `Core/ViewModels/MyMembershipViewModel.cs`, `Core/Services/IProofPicker.cs`, `App/Views/PackagesPage.xaml(.cs)`, `App/Views/MyMembershipPage.xaml(.cs)`, `App/Services/MauiProofPicker.cs`
- Modify: `App/MemberShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/MembershipTests.cs`

**Interfaces:**
- Consumes: `ISupabaseGateway`, models `MembershipPackage`, `UserMembershipPackage`, `Payment`; RPCs `avail_membership(p_package_id)`, `renew_membership(p_ump_id)`.
- Produces:
  - `IMembershipService { Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync(); Task<AppResult<IReadOnlyList<UserMembershipPackage>>> MyMembershipsAsync(); Task<AppResult<IReadOnlyList<Payment>>> MyPaymentsAsync(); Task<AppResult<int>> AvailAsync(int packageId); Task<AppResult<int>> RenewAsync(int membershipId); Task<AppResult<bool>> AttachProofAsync(int paymentId, string fileName, byte[] data); }`
  - `IProofPicker { Task<(string FileName, byte[] Data)?> PickAsync(); }`

- [ ] **Step 1: Write failing tests**

`tests/GymMembership.Tests/MembershipTests.cs`:
```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MembershipTests
{
    [Fact]
    public async Task Avail_calls_rpc_with_package_id_and_returns_payment_id()
    {
        var gw = new FakeGateway();
        gw.RpcResults["avail_membership"] = 42;
        var r = await new MembershipService(gw).AvailAsync(7);
        Assert.Equal(42, r.Value);
        Assert.Equal(("avail_membership", 7), (gw.RpcCalls[0].Fn, gw.RpcCalls[0].Args!["p_package_id"]));
    }

    [Fact]
    public async Task Avail_conflict_maps_to_conflict()
    {
        var gw = new FakeGateway { Throw = new Exception("already_availed") };
        Assert.Equal(AppErrorKind.Conflict, (await new MembershipService(gw).AvailAsync(1)).Error);
    }

    [Fact]
    public async Task AttachProof_uploads_under_user_folder_then_sets_proof_path()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new Payment { Id = 5, UserId = "u1" });
        var r = await new MembershipService(gw).AttachProofAsync(5, "slip.jpg", new byte[] { 1 });
        Assert.True(r.Ok);
        Assert.Equal(("payment-proofs", "u1/5-slip.jpg"), gw.Uploads[0]);
    }

    [Fact]
    public async Task Packages_vm_loads_only_active_and_shows_error_on_failure()
    {
        var gw = new FakeGateway();
        gw.Seed(new MembershipPackage { Id = 1, Name = "A", IsActive = true }, new MembershipPackage { Id = 2, Name = "B", IsActive = false });
        var vm = new PackagesViewModel(new MembershipService(gw), new StubPicker());
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(vm.Packages);

        gw.Throw = new HttpRequestException();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains("offline", vm.ErrorMessage);
    }

    [Fact]
    public async Task MyMembership_vm_flags_renewable_when_expiring_within_7_days_or_expired()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new UserMembershipPackage { Id = 1, UserId = "u1", Status = "active", EndsAt = DateTime.UtcNow.AddDays(3) },
                new UserMembershipPackage { Id = 2, UserId = "u1", Status = "active", EndsAt = DateTime.UtcNow.AddDays(30) },
                new UserMembershipPackage { Id = 3, UserId = "u1", Status = "expired", EndsAt = DateTime.UtcNow.AddDays(-2) });
        var vm = new MyMembershipViewModel(new MembershipService(gw), new StubPicker());
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 1, 3 }, vm.Memberships.Where(m => m.CanRenew).Select(m => m.Id).OrderBy(i => i));
    }

    sealed class StubPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string, byte[])?>(null); }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement Core**

`Core/Services/IProofPicker.cs`:
```csharp
namespace GymMembership.Core.Services;

public interface IProofPicker { Task<(string FileName, byte[] Data)?> PickAsync(); }
```
`Core/Services/MembershipService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface IMembershipService
{
    Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync();
    Task<AppResult<IReadOnlyList<UserMembershipPackage>>> MyMembershipsAsync();
    Task<AppResult<IReadOnlyList<Payment>>> MyPaymentsAsync();
    Task<AppResult<int>> AvailAsync(int packageId);
    Task<AppResult<int>> RenewAsync(int membershipId);
    Task<AppResult<bool>> AttachProofAsync(int paymentId, string fileName, byte[] data);
}

public sealed class MembershipService(ISupabaseGateway gw) : IMembershipService
{
    public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() =>
        Safe.RunAsync(() => gw.ListAsync<MembershipPackage>(p => p.IsActive == true));

    public Task<AppResult<IReadOnlyList<UserMembershipPackage>>> MyMembershipsAsync() =>
        Safe.RunAsync(() => { var uid = gw.CurrentUserId; return gw.ListAsync<UserMembershipPackage>(m => m.UserId == uid); });

    public Task<AppResult<IReadOnlyList<Payment>>> MyPaymentsAsync() =>
        Safe.RunAsync(() => { var uid = gw.CurrentUserId; return gw.ListAsync<Payment>(p => p.UserId == uid); });

    public Task<AppResult<int>> AvailAsync(int packageId) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("avail_membership", new() { ["p_package_id"] = packageId }));

    public Task<AppResult<int>> RenewAsync(int membershipId) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("renew_membership", new() { ["p_ump_id"] = membershipId }));

    public Task<AppResult<bool>> AttachProofAsync(int paymentId, string fileName, byte[] data) =>
        Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            var path = await gw.UploadAsync("payment-proofs", $"{uid}/{paymentId}-{fileName}", data);
            var row = (await gw.ListAsync<Payment>(p => p.Id == paymentId)).First();
            row.ProofPath = path;
            await gw.UpdateAsync(row);
            return true;
        });
}
```
`Core/ViewModels/PackagesViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class PackagesViewModel(IMembershipService svc, IProofPicker picker) : LoadableViewModel
{
    public ObservableCollection<MembershipPackage> Packages { get; } = new();
    public string? Notice { get; private set; }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.ListPackagesAsync, rows =>
    { Packages.Clear(); foreach (var p in rows.Where(p => p.IsActive)) Packages.Add(p); });

    [RelayCommand]
    private async Task AvailAsync(MembershipPackage package)
    {
        int paymentId = 0;
        if (!await RunAsync(() => svc.AvailAsync(package.Id), id => paymentId = id)) return;
        Notice = "Payment created. Pay at the front desk or attach your proof of payment.";
        if (await picker.PickAsync() is { } file)
            await RunAsync(() => svc.AttachProofAsync(paymentId, file.FileName, file.Data), _ => { });
        OnPropertyChanged(nameof(Notice));
    }
}
```
`Core/ViewModels/MyMembershipViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public sealed record MembershipItem(int Id, string Status, DateTime? EndsAt, bool CanRenew);

public partial class MyMembershipViewModel(IMembershipService svc, IProofPicker picker) : LoadableViewModel
{
    public ObservableCollection<MembershipItem> Memberships { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.MyMembershipsAsync, rows =>
    {
        Memberships.Clear();
        foreach (var m in rows)
        {
            var soon = m.Status == Status_.Active && m.EndsAt is { } e && e <= DateTime.UtcNow.AddDays(7);
            Memberships.Add(new(m.Id, m.Status, m.EndsAt, soon || m.Status == Status_.Expired));
        }
    });

    [RelayCommand]
    private async Task RenewAsync(MembershipItem item)
    {
        int paymentId = 0;
        if (!await RunAsync(() => svc.RenewAsync(item.Id), id => paymentId = id)) return;
        if (await picker.PickAsync() is { } file)
            await RunAsync(() => svc.AttachProofAsync(paymentId, file.FileName, file.Data), _ => { });
        await LoadAsync();
    }
}

file static class Status_ { public const string Active = Models.Status.Active, Expired = Models.Status.Expired; }
```
(The `Status_` alias avoids a clash with the `Status` property name on `UserMembershipPackage`.)

- [ ] **Step 4: Run — expect PASS** (`dotnet test tests/GymMembership.Tests`).

- [ ] **Step 5: App wiring**

`App/Services/MauiProofPicker.cs`:
```csharp
using GymMembership.Core.Services;

namespace GymMembership.App.Services;

public sealed class MauiProofPicker : IProofPicker
{
    public async Task<(string FileName, byte[] Data)?> PickAsync()
    {
        var file = await MediaPicker.Default.PickPhotoAsync();
        if (file is null) return null;
        await using var s = await file.OpenReadAsync();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return (file.FileName, ms.ToArray());
    }
}
```
`App/Views/PackagesPage.xaml`:
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui" xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
             xmlns:m="clr-namespace:GymMembership.Core.Models;assembly=GymMembership.Core"
             xmlns:v="clr-namespace:GymMembership.App.Views"
             x:Class="GymMembership.App.Views.PackagesPage" x:DataType="vm:PackagesViewModel" Title="Packages">
    <Grid RowDefinitions="Auto,*" Padding="16">
        <v:ErrorBanner Message="{Binding ErrorMessage}" />
        <RefreshView Grid.Row="1" Command="{Binding LoadCommand}" IsRefreshing="{Binding IsBusy}">
            <CollectionView ItemsSource="{Binding Packages}">
                <CollectionView.ItemTemplate>
                    <DataTemplate x:DataType="m:MembershipPackage">
                        <Border Padding="16" Margin="0,0,0,12" StrokeShape="RoundRectangle 12">
                            <VerticalStackLayout Spacing="4">
                                <Label Text="{Binding Name}" FontSize="18" FontAttributes="Bold" />
                                <Label Text="{Binding Description}" />
                                <Label Text="{Binding DurationDays, StringFormat='{0} days'}" />
                                <Label Text="{Binding Price, StringFormat='{0:N2}'}" FontSize="20" />
                                <Button Text="Avail"
                                        Command="{Binding Source={RelativeSource AncestorType={x:Type vm:PackagesViewModel}}, Path=AvailCommand}"
                                        CommandParameter="{Binding .}" />
                            </VerticalStackLayout>
                        </Border>
                    </DataTemplate>
                </CollectionView.ItemTemplate>
            </CollectionView>
        </RefreshView>
    </Grid>
</ContentPage>
```
`PackagesPage.xaml.cs`: constructor `(PackagesViewModel vm) { InitializeComponent(); BindingContext = vm; }` and `protected override void OnAppearing() { base.OnAppearing(); ((PackagesViewModel)BindingContext).LoadCommand.Execute(null); }`.
`App/Views/MyMembershipPage.xaml`: same structure bound to `MyMembershipViewModel`; item template `x:DataType="vm:MembershipItem"` showing `Status`, `EndsAt` (`StringFormat='Ends {0:d}'`) and a `Renew` button (`IsVisible="{Binding CanRenew}"`, command `RenewCommand` via RelativeSource, parameter `{Binding .}`). Code-behind identical to `PackagesPage` with its VM type.

`MauiProgram.cs` additions:
```csharp
builder.Services.AddSingleton<IProofPicker, MauiProofPicker>();
builder.Services.AddSingleton<IMembershipService, MembershipService>();
builder.Services.AddTransient<PackagesViewModel>();
builder.Services.AddTransient<MyMembershipViewModel>();
builder.Services.AddTransient<PackagesPage>();
builder.Services.AddTransient<MyMembershipPage>();
```
`MemberShell.xaml`: replace the placeholder with
```xml
<TabBar>
    <ShellContent Title="Packages" Icon="dotnet_bot.png" ContentTemplate="{DataTemplate views:PackagesPage}" />
    <ShellContent Title="My Membership" Icon="dotnet_bot.png" ContentTemplate="{DataTemplate views:MyMembershipPage}" />
</TabBar>
```
(with `xmlns:views="clr-namespace:GymMembership.App.Views"`; swap the template icon for real icons during Task 17.)

- [ ] **Step 6: Manual verification**

Run the app as `alice`; in Studio insert a package (`insert into membership_packages(name,price,duration_days) values ('Monthly',30,30)`). Expected: package appears; **Avail** creates a payment (check `payments` in Studio); picking a photo sets `proof_path`; offline (stop Supabase) shows the offline banner.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: member packages, renewals, proof upload"`

---

### Task 10: Staff — payment verification (F3)

**Files:**
- Create: `Core/Services/PaymentStaffService.cs`, `Core/ViewModels/VerifyPaymentsViewModel.cs`, `App/Views/VerifyPaymentsPage.xaml(.cs)`
- Modify: `App/StaffShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/PaymentStaffTests.cs`

**Interfaces:**
- Consumes: `Payment`, `Profile`; RPC `verify_payment(p_payment_id, p_approve)`.
- Produces: `IPaymentStaffService { Task<AppResult<IReadOnlyList<Payment>>> PendingAsync(); Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve); }`; `VerifyPaymentsViewModel` with `Pending`, `LoadCommand`, `ApproveCommand(Payment)`, `RejectCommand(Payment)`; after a successful action the row is removed from `Pending`.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class PaymentStaffTests
{
    [Fact]
    public async Task Verify_calls_rpc_with_flags()
    {
        var gw = new FakeGateway();
        await new PaymentStaffService(gw).VerifyAsync(9, false);
        Assert.Equal(("verify_payment", 9, false), (gw.RpcCalls[0].Fn, gw.RpcCalls[0].Args!["p_payment_id"], gw.RpcCalls[0].Args!["p_approve"]));
    }

    [Fact]
    public async Task Approve_removes_row_on_success()
    {
        var gw = new FakeGateway();
        var p = new Payment { Id = 1, Status = "pending" };
        gw.Seed(p);
        var vm = new VerifyPaymentsViewModel(new PaymentStaffService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.ApproveCommand.ExecuteAsync(p);
        Assert.Empty(vm.Pending);
    }

    [Fact]
    public async Task Already_processed_keeps_row_and_shows_conflict_message()
    {
        var gw = new FakeGateway();
        var p = new Payment { Id = 1, Status = "pending" };
        gw.Seed(p);
        var vm = new VerifyPaymentsViewModel(new PaymentStaffService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        gw.Throw = new Exception("already_processed");
        await vm.ApproveCommand.ExecuteAsync(p);
        Assert.Single(vm.Pending);
        Assert.Contains("conflicts", vm.ErrorMessage);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/PaymentStaffService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface IPaymentStaffService
{
    Task<AppResult<IReadOnlyList<Payment>>> PendingAsync();
    Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve);
}

public sealed class PaymentStaffService(ISupabaseGateway gw) : IPaymentStaffService
{
    public Task<AppResult<IReadOnlyList<Payment>>> PendingAsync() =>
        Safe.RunAsync(() => gw.ListAsync<Payment>(p => p.Status == "pending"));

    public Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve) =>
        Safe.RunAsync(async () =>
        {
            await gw.RpcAsync("verify_payment", new() { ["p_payment_id"] = paymentId, ["p_approve"] = approve });
            return true;
        });
}
```
`Core/ViewModels/VerifyPaymentsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class VerifyPaymentsViewModel(IPaymentStaffService svc) : LoadableViewModel
{
    public ObservableCollection<Payment> Pending { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.PendingAsync, rows =>
    { Pending.Clear(); foreach (var p in rows.OrderBy(p => p.CreatedAt)) Pending.Add(p); });

    [RelayCommand] private Task ApproveAsync(Payment p) => DecideAsync(p, true);
    [RelayCommand] private Task RejectAsync(Payment p) => DecideAsync(p, false);

    async Task DecideAsync(Payment p, bool approve)
    {
        if (await RunAsync(() => svc.VerifyAsync(p.Id, approve), _ => { })) Pending.Remove(p);
    }
}
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring**

`VerifyPaymentsPage.xaml`: same layout pattern as `PackagesPage` (ErrorBanner, RefreshView + CollectionView over `Pending`); item template shows `Amount` (`StringFormat='{0:N2}'`), `Currency`, `CreatedAt`, `Qr`, and two buttons `Approve` / `Reject` bound to `ApproveCommand`/`RejectCommand` (RelativeSource ancestor `vm:VerifyPaymentsViewModel`, `CommandParameter="{Binding .}"`). Code-behind identical pattern (ctor with VM, `OnAppearing` → `LoadCommand`). Register `IPaymentStaffService`, `VerifyPaymentsViewModel`, `VerifyPaymentsPage` in `MauiProgram.cs`. In `StaffShell.xaml` add a `FlyoutItem Title="Payments"` with `ShellContent ContentTemplate="{DataTemplate views:VerifyPaymentsPage}"` and set `FlyoutBehavior="Locked"` on Windows (`FlyoutBehavior="{OnPlatform WinUI=Locked, Default=Flyout}"`).

- [ ] **Step 6: Manual verification**

As `alice` avail a package, then sign in as an employee (assign via Studio: `insert into user_roles(user_id,role_id) select '<uid>', role_id from roles where name='employee'`). Expected: the pending payment appears; Approve removes it; as `alice` the membership shows `active`. Approving your own payment shows the validation banner.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: staff payment verification"`

---

### Task 11: Amenities (F5)

**Files:**
- Create: `Core/Services/AmenityService.cs`, `Core/ViewModels/AmenitiesViewModel.cs`, `Core/ViewModels/VerifyAmenityUsagesViewModel.cs`, `App/Views/AmenitiesPage.xaml(.cs)`, `App/Views/VerifyAmenityUsagesPage.xaml(.cs)`
- Modify: `App/MemberShell.xaml`, `App/StaffShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/AmenityTests.cs`

**Interfaces:**
- Consumes: `Amenity`, `UserAmenity`, `AmenityUsage`, `IProofPicker`; RPCs `avail_amenity(p_amenity_id)`, `verify_amenity_usage(p_id, p_approve)`.
- Produces: `IAmenityService { CatalogAsync(); AvailAsync(int amenityId) → AppResult<int>; LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof) → AppResult<bool>; MyEntitlementsAsync(); PendingUsagesAsync(); VerifyUsageAsync(int usageId, bool approve) }` and the two ViewModels.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AmenityTests
{
    [Fact]
    public async Task Avail_requires_membership_error_is_validation()
    {
        var gw = new FakeGateway { Throw = new Exception("no_active_membership") };
        Assert.Equal(AppErrorKind.Validation, (await new AmenityService(gw).AvailAsync(1)).Error);
    }

    [Fact]
    public async Task LogUsage_uploads_proof_and_inserts_pending_row()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        AmenityUsage? inserted = null;
        gw.InsertHandler = o => inserted = (AmenityUsage)o;
        var r = await new AmenityService(gw).LogUsageAsync(3, ("pool.jpg", new byte[] { 1 }));
        Assert.True(r.Ok);
        Assert.Equal(("amenity-proofs", "u1/3-pool.jpg"), gw.Uploads[0]);
        Assert.Equal(3, inserted!.UserAmenityId);
        Assert.Equal("u1/3-pool.jpg", inserted.Proof);
        Assert.Equal("pending", inserted.Status);
    }

    [Fact]
    public async Task LogUsage_without_proof_still_inserts()
    {
        var gw = new FakeGateway();
        var r = await new AmenityService(gw).LogUsageAsync(3, null);
        Assert.True(r.Ok);
        Assert.Empty(gw.Uploads);
    }

    [Fact]
    public async Task Verify_vm_removes_row_after_decision()
    {
        var gw = new FakeGateway();
        var u = new AmenityUsage { Id = 1, Status = "pending" };
        gw.Seed(u);
        var vm = new VerifyAmenityUsagesViewModel(new AmenityService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.ApproveCommand.ExecuteAsync(u);
        Assert.Empty(vm.Pending);
        Assert.Equal("verify_amenity_usage", gw.RpcCalls[0].Fn);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/AmenityService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface IAmenityService
{
    Task<AppResult<IReadOnlyList<Amenity>>> CatalogAsync();
    Task<AppResult<IReadOnlyList<UserAmenity>>> MyEntitlementsAsync();
    Task<AppResult<int>> AvailAsync(int amenityId);
    Task<AppResult<bool>> LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof);
    Task<AppResult<IReadOnlyList<AmenityUsage>>> PendingUsagesAsync();
    Task<AppResult<bool>> VerifyUsageAsync(int usageId, bool approve);
}

public sealed class AmenityService(ISupabaseGateway gw) : IAmenityService
{
    public Task<AppResult<IReadOnlyList<Amenity>>> CatalogAsync() =>
        Safe.RunAsync(() => gw.ListAsync<Amenity>(a => a.IsActive == true));

    public Task<AppResult<IReadOnlyList<UserAmenity>>> MyEntitlementsAsync() =>
        Safe.RunAsync(() => { var uid = gw.CurrentUserId; return gw.ListAsync<UserAmenity>(a => a.UserId == uid); });

    public Task<AppResult<int>> AvailAsync(int amenityId) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("avail_amenity", new() { ["p_amenity_id"] = amenityId }));

    public Task<AppResult<bool>> LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof) =>
        Safe.RunAsync(async () =>
        {
            string? path = null;
            if (proof is { } p)
            {
                var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
                path = await gw.UploadAsync("amenity-proofs", $"{uid}/{userAmenityId}-{p.FileName}", p.Data);
            }
            await gw.InsertAsync(new AmenityUsage { UserAmenityId = userAmenityId, Proof = path, Status = "pending" });
            return true;
        });

    public Task<AppResult<IReadOnlyList<AmenityUsage>>> PendingUsagesAsync() =>
        Safe.RunAsync(() => gw.ListAsync<AmenityUsage>(u => u.Status == "pending"));

    public Task<AppResult<bool>> VerifyUsageAsync(int usageId, bool approve) =>
        Safe.RunAsync(async () =>
        {
            await gw.RpcAsync("verify_amenity_usage", new() { ["p_id"] = usageId, ["p_approve"] = approve });
            return true;
        });
}
```
`Core/ViewModels/AmenitiesViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public sealed record AmenityItem(Amenity Amenity, int? UserAmenityId);

public partial class AmenitiesViewModel(IAmenityService svc, IProofPicker picker) : LoadableViewModel
{
    public ObservableCollection<AmenityItem> Items { get; } = new();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IReadOnlyList<Amenity> catalog = [];
        IReadOnlyList<UserAmenity> mine = [];
        if (!await RunAsync(svc.CatalogAsync, c => catalog = c)) return;
        if (!await RunAsync(svc.MyEntitlementsAsync, m => mine = m)) return;
        Items.Clear();
        foreach (var a in catalog) Items.Add(new(a, mine.FirstOrDefault(m => m.AmenityId == a.Id)?.Id));
    }

    [RelayCommand]
    private async Task AvailAsync(AmenityItem item)
    {
        if (await RunAsync(() => svc.AvailAsync(item.Amenity.Id), _ => { })) await LoadAsync();
    }

    [RelayCommand]
    private async Task LogUsageAsync(AmenityItem item)
    {
        if (item.UserAmenityId is not { } id) return;
        var proof = await picker.PickAsync();
        await RunAsync(() => svc.LogUsageAsync(id, proof), _ => { });
    }
}
```
`Core/ViewModels/VerifyAmenityUsagesViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class VerifyAmenityUsagesViewModel(IAmenityService svc) : LoadableViewModel
{
    public ObservableCollection<AmenityUsage> Pending { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.PendingUsagesAsync, rows =>
    { Pending.Clear(); foreach (var u in rows) Pending.Add(u); });

    [RelayCommand] private Task ApproveAsync(AmenityUsage u) => DecideAsync(u, true);
    [RelayCommand] private Task RejectAsync(AmenityUsage u) => DecideAsync(u, false);

    async Task DecideAsync(AmenityUsage u, bool approve)
    {
        if (await RunAsync(() => svc.VerifyUsageAsync(u.Id, approve), _ => { })) Pending.Remove(u);
    }
}
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring**

`AmenitiesPage.xaml`: ErrorBanner + RefreshView/CollectionView over `Items` (`x:DataType="vm:AmenityItem"`): `Amenity.Name`, `Amenity.Description`; button **Avail** (`IsVisible="{Binding UserAmenityId, Converter={StaticResource IsNullConverter}}"`) → `AvailCommand`; button **Log usage** (`IsVisible="{Binding UserAmenityId, Converter={StaticResource IsNotNullConverter}}"`) → `LogUsageCommand`. `VerifyAmenityUsagesPage.xaml`: same pattern as `VerifyPaymentsPage` over `Pending` showing `UsedAt`, `Proof`, **Approve**/**Reject**. Register the service, both VMs, both pages; add `IsNullConverter` (`toolkit:IsNullConverter`) to `App.xaml`; add tab "Amenities" to `MemberShell`, flyout item "Amenity usage" to `StaffShell`.

- [ ] **Step 6: Manual verification**

Admin inserts an amenity in Studio; an active member avails it and logs usage with a photo; staff verifies. A member without an active membership sees the validation banner on Avail.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: amenities and usage verification"`

---

### Task 12: Coaches and hiring (F6)

**Files:**
- Create: `Core/Services/CoachService.cs`, `Core/ViewModels/CoachesViewModel.cs`, `Core/ViewModels/CoachProfileViewModel.cs`, `App/Views/CoachesPage.xaml(.cs)`, `App/Views/CoachProfilePage.xaml(.cs)`
- Modify: `App/MemberShell.xaml`, `App/CoachShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/CoachTests.cs`

**Interfaces:**
- Consumes: `Coach`, `Member`, `CoachHire`, `Profile`.
- Produces: `ICoachService { ListCoachesAsync() → AppResult<IReadOnlyList<CoachCard>>; MyHiresAsync() → AppResult<IReadOnlyList<CoachHire>>; HireAsync(int coachId); EndHireAsync(int hireId); MyCoachAsync() → AppResult<Coach?>; SetAvailabilityAsync(bool); UpdateProfileAsync(string? bio, string? specialty, decimal? rate) }`, `record CoachCard(Coach Coach, string Name, bool Hired, int? HireId)`.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CoachTests
{
    static FakeGateway Seeded()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new Member { Id = 10, UserId = "u1" });
        gw.Seed(new Coach { Id = 1, UserId = "c1", IsAvailable = true }, new Coach { Id = 2, UserId = "c2", IsAvailable = false });
        gw.Seed(new Profile { Id = "c1", Username = "Coach One" }, new Profile { Id = "c2", Username = "Coach Two" });
        gw.Seed(new CoachHire { Id = 7, MemberId = 10, CoachId = 1, Status = "active" });
        return gw;
    }

    [Fact]
    public async Task List_returns_available_coaches_with_names_and_hire_flag()
    {
        var r = await new CoachService(Seeded()).ListCoachesAsync();
        var card = Assert.Single(r.Value!);
        Assert.Equal(("Coach One", true, 7), (card.Name, card.Hired, card.HireId));
    }

    [Fact]
    public async Task Hire_inserts_active_hire_for_current_member()
    {
        var gw = Seeded();
        CoachHire? inserted = null;
        gw.InsertHandler = o => inserted = (CoachHire)o;
        var r = await new CoachService(gw).HireAsync(2);
        Assert.True(r.Ok);
        Assert.Equal((10, 2, "active"), (inserted!.MemberId, inserted.CoachId, inserted.Status));
    }

    [Fact]
    public async Task Hire_without_membership_rls_denial_maps_to_unauthorized()
    {
        var gw = Seeded();
        gw.Throw = new Exception("new row violates row-level security policy");
        Assert.Equal(AppErrorKind.Unauthorized, (await new CoachService(gw).HireAsync(2)).Error);
    }

    [Fact]
    public async Task Coaches_vm_loads_cards()
    {
        var vm = new CoachesViewModel(new CoachService(Seeded()));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Single(vm.Cards);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/CoachService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public sealed record CoachCard(Coach Coach, string Name, bool Hired, int? HireId);

public interface ICoachService
{
    Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync();
    Task<AppResult<bool>> HireAsync(int coachId);
    Task<AppResult<bool>> EndHireAsync(int hireId);
    Task<AppResult<Coach?>> MyCoachAsync();
    Task<AppResult<bool>> SetAvailabilityAsync(bool available);
    Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate);
}

public sealed class CoachService(ISupabaseGateway gw) : ICoachService
{
    async Task<Member?> MeAsync()
    {
        var uid = gw.CurrentUserId;
        return (await gw.ListAsync<Member>(m => m.UserId == uid)).FirstOrDefault();
    }

    public Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync() =>
        Safe.RunAsync<IReadOnlyList<CoachCard>>(async () =>
        {
            var coaches = await gw.ListAsync<Coach>(c => c.IsAvailable == true);
            var profiles = await gw.ListAsync<Profile>();
            var me = await MeAsync();
            var hires = me is null ? [] : await gw.ListAsync<CoachHire>(h => h.MemberId == me.Id && h.Status == "active");
            return coaches.Select(c => new CoachCard(c,
                profiles.FirstOrDefault(p => p.Id == c.UserId)?.Username ?? "Coach",
                hires.Any(h => h.CoachId == c.Id), hires.FirstOrDefault(h => h.CoachId == c.Id)?.Id)).ToList();
        });

    public Task<AppResult<bool>> HireAsync(int coachId) =>
        Safe.RunAsync(async () =>
        {
            var me = await MeAsync() ?? throw new Exception("not_found");
            await gw.InsertAsync(new CoachHire { MemberId = me.Id, CoachId = coachId, Status = "active" });
            return true;
        });

    public Task<AppResult<bool>> EndHireAsync(int hireId) =>
        Safe.RunAsync(async () =>
        {
            await gw.UpdateAsync(new CoachHire { Id = hireId, Status = "ended", EndedAt = DateTime.UtcNow });
            return true;
        });

    public Task<AppResult<Coach?>> MyCoachAsync() =>
        Safe.RunAsync<Coach?>(async () => { var uid = gw.CurrentUserId; return (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault(); });

    public Task<AppResult<bool>> SetAvailabilityAsync(bool available) =>
        Safe.RunAsync(async () =>
        {
            var c = (await MyCoachAsync()).Value ?? throw new Exception("not_found");
            c.IsAvailable = available;
            await gw.UpdateAsync(c);
            return true;
        });

    public Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate) =>
        Safe.RunAsync(async () =>
        {
            var c = (await MyCoachAsync()).Value ?? throw new Exception("not_found");
            c.Bio = bio; c.Specialty = specialty; c.HourlyRate = rate;
            await gw.UpdateAsync(c);
            return true;
        });
}
```
`Core/ViewModels/CoachesViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class CoachesViewModel(ICoachService svc) : LoadableViewModel
{
    public ObservableCollection<CoachCard> Cards { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.ListCoachesAsync, rows =>
    { Cards.Clear(); foreach (var c in rows) Cards.Add(c); });

    [RelayCommand]
    private async Task HireAsync(CoachCard card)
    {
        if (await RunAsync(() => svc.HireAsync(card.Coach.Id), _ => { })) await LoadAsync();
    }

    [RelayCommand]
    private async Task EndHireAsync(CoachCard card)
    {
        if (card.HireId is { } id && await RunAsync(() => svc.EndHireAsync(id), _ => { })) await LoadAsync();
    }
}
```
`Core/ViewModels/CoachProfileViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class CoachProfileViewModel(ICoachService svc) : LoadableViewModel
{
    [ObservableProperty] private string? bio;
    [ObservableProperty] private string? specialty;
    [ObservableProperty] private decimal? hourlyRate;
    [ObservableProperty] private bool isAvailable;

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.MyCoachAsync, c =>
    { if (c is null) return; Bio = c.Bio; Specialty = c.Specialty; HourlyRate = c.HourlyRate; IsAvailable = c.IsAvailable; });

    [RelayCommand]
    private async Task SaveAsync()
    {
        await RunAsync(() => svc.UpdateProfileAsync(Bio, Specialty, HourlyRate), _ => { });
        await RunAsync(() => svc.SetAvailabilityAsync(IsAvailable), _ => { });
    }
}
```

- [ ] **Step 4: Run — expect PASS.** (Note: `UpdateAsync(new CoachHire{...})` relies on PostgREST patching only provided non-null columns by primary key; confirm in the manual step that `member_id`/`coach_id` are not overwritten with 0 — if they are, change `EndHireAsync` to fetch the row first, set `Status`/`EndedAt`, then update, mirroring `SetAvailabilityAsync`.)

- [ ] **Step 5: App wiring**

`CoachesPage.xaml` (member): CollectionView over `Cards` (`x:DataType="svc:CoachCard"` with `xmlns:svc="clr-namespace:GymMembership.Core.Services;assembly=GymMembership.Core"`): `Name`, `Coach.Specialty`, `Coach.Bio`, `Coach.HourlyRate`; **Hire** (`IsVisible` when `Hired` is false — use `InvertedBoolConverter`) / **End hire** (`IsVisible="{Binding Hired}"`). `CoachProfilePage.xaml` (coach): `Editor` for bio, `Entry` for specialty and rate, `Switch` for availability, **Save**. Register services/VMs/pages; add "Coaches" tab to `MemberShell`, "Profile" tab to `CoachShell`.

- [ ] **Step 6: Manual verification**

In Studio make `bob` a coach (call `select assign_role('<bob uid>','coach')` as an admin or insert `user_roles` + `coaches`). As an active member, Hire shows in `coach_hires`; ending a hire sets `status = 'ended'` and leaves ids intact. Verify the Step 4 note.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: coaches and hiring"`

---

### Task 13: Time requests and training sessions (F7)

**Files:**
- Create: `Core/Services/SessionService.cs`, `Core/ViewModels/TimeRequestsViewModel.cs`, `Core/ViewModels/MySessionsViewModel.cs`, `App/Views/TimeRequestsPage.xaml(.cs)`, `App/Views/MySessionsPage.xaml(.cs)`
- Modify: `App/MemberShell.xaml`, `App/CoachShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/SessionTests.cs`

**Interfaces:**
- Consumes: `TimeRequest`, `TrainingSession`, `Member`, `Coach`; RPC `respond_time_request(p_id, p_approve)`.
- Produces: `ISessionService { MyRequestsAsync(); RequestAsync(int coachId, DateTime startUtc, DateTime endUtc, string? message); PostAvailabilityAsync(DateTime startUtc, DateTime endUtc); RespondAsync(int requestId, bool approve); CancelRequestAsync(int requestId); MySessionsAsync(); SetSessionStatusAsync(int sessionId, string status) }`. Time inputs must be UTC; `RequestAsync` rejects `end <= start` locally with `Validation`.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class SessionTests
{
    static readonly DateTime T0 = new(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Request_with_end_before_start_is_validation_error_and_never_hits_gateway()
    {
        var gw = new FakeGateway();
        var r = await new SessionService(gw).RequestAsync(1, T0, T0.AddHours(-1), null);
        Assert.Equal(AppErrorKind.Validation, r.Error);
        Assert.Empty(gw.Tables);
    }

    [Fact]
    public async Task Request_inserts_member_request_for_current_member()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new Member { Id = 10, UserId = "u1" });
        TimeRequest? inserted = null;
        gw.InsertHandler = o => inserted = o as TimeRequest;
        var r = await new SessionService(gw).RequestAsync(1, T0, T0.AddHours(1), "hi");
        Assert.True(r.Ok);
        Assert.Equal((1, 10, "member", "pending"), (inserted!.CoachId, inserted.MemberId, inserted.RequestedBy, inserted.Status));
    }

    [Fact]
    public async Task Coach_availability_post_has_null_member()
    {
        var gw = new FakeGateway { CurrentUserId = "c1" };
        gw.Seed(new Coach { Id = 3, UserId = "c1" });
        TimeRequest? inserted = null;
        gw.InsertHandler = o => inserted = o as TimeRequest;
        await new SessionService(gw).PostAvailabilityAsync(T0, T0.AddHours(1));
        Assert.Equal((3, (int?)null, "coach"), (inserted!.CoachId, inserted.MemberId, inserted.RequestedBy));
    }

    [Fact]
    public async Task Respond_overlap_surfaces_conflict_and_vm_keeps_request()
    {
        var gw = new FakeGateway { CurrentUserId = "c1" };
        var req = new TimeRequest { Id = 4, Status = "pending", RequestedBy = "member" };
        gw.Seed(req);
        var vm = new TimeRequestsViewModel(new SessionService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        gw.Throw = new Exception("overlap");
        await vm.ApproveCommand.ExecuteAsync(req);
        Assert.Contains("conflicts", vm.ErrorMessage);
        Assert.Equal("respond_time_request", gw.RpcCalls[0].Fn);
        Assert.Equal(4, gw.RpcCalls[0].Args!["p_id"]);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/SessionService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface ISessionService
{
    Task<AppResult<IReadOnlyList<TimeRequest>>> MyRequestsAsync();
    Task<AppResult<bool>> RequestAsync(int coachId, DateTime startUtc, DateTime endUtc, string? message);
    Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> RespondAsync(int requestId, bool approve);
    Task<AppResult<bool>> CancelRequestAsync(int requestId);
    Task<AppResult<IReadOnlyList<TrainingSession>>> MySessionsAsync();
    Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status);
}

public sealed class SessionService(ISupabaseGateway gw) : ISessionService
{
    // RLS already scopes both lists to the caller (member, coach, or open coach postings for hired members).
    public Task<AppResult<IReadOnlyList<TimeRequest>>> MyRequestsAsync() =>
        Safe.RunAsync(async () => (IReadOnlyList<TimeRequest>)(await gw.ListAsync<TimeRequest>(r => r.Status == "pending"))
            .OrderBy(r => r.RequestedStart).ToList());

    public async Task<AppResult<bool>> RequestAsync(int coachId, DateTime startUtc, DateTime endUtc, string? message)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId;
            var me = (await gw.ListAsync<Member>(m => m.UserId == uid)).FirstOrDefault() ?? throw new Exception("not_found");
            await gw.InsertAsync(new TimeRequest { CoachId = coachId, MemberId = me.Id, RequestedBy = "member",
                RequestedStart = startUtc, RequestedEnd = endUtc, Message = message, Status = "pending" });
            return true;
        });
    }

    public async Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId;
            var coach = (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault() ?? throw new Exception("not_found");
            await gw.InsertAsync(new TimeRequest { CoachId = coach.Id, MemberId = null, RequestedBy = "coach",
                RequestedStart = startUtc, RequestedEnd = endUtc, Status = "pending" });
            return true;
        });
    }

    public Task<AppResult<bool>> RespondAsync(int requestId, bool approve) =>
        Safe.RunAsync(async () =>
        {
            await gw.RpcAsync("respond_time_request", new() { ["p_id"] = requestId, ["p_approve"] = approve });
            return true;
        });

    public Task<AppResult<bool>> CancelRequestAsync(int requestId) =>
        Safe.RunAsync(async () =>
        {
            var row = (await gw.ListAsync<TimeRequest>(r => r.Id == requestId)).First();
            row.Status = "cancelled";
            await gw.UpdateAsync(row);
            return true;
        });

    public Task<AppResult<IReadOnlyList<TrainingSession>>> MySessionsAsync() =>
        Safe.RunAsync(async () => (IReadOnlyList<TrainingSession>)(await gw.ListAsync<TrainingSession>())
            .OrderBy(s => s.ScheduledStart).ToList());

    public Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status) =>
        Safe.RunAsync(async () =>
        {
            var row = (await gw.ListAsync<TrainingSession>(s => s.Id == sessionId)).First();
            row.Status = status;
            await gw.UpdateAsync(row);
            return true;
        });
}
```
`Core/ViewModels/TimeRequestsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class TimeRequestsViewModel(ISessionService svc) : LoadableViewModel
{
    public ObservableCollection<TimeRequest> Requests { get; } = new();
    [ObservableProperty] private int coachId;          // member: coach to request
    [ObservableProperty] private DateTime start = DateTime.Today.AddDays(1).AddHours(9);
    [ObservableProperty] private DateTime end = DateTime.Today.AddDays(1).AddHours(10);
    [ObservableProperty] private string? message;

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.MyRequestsAsync, rows =>
    { Requests.Clear(); foreach (var r in rows) Requests.Add(r); });

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (await RunAsync(() => svc.RequestAsync(CoachId, Start.ToUniversalTime(), End.ToUniversalTime(), Message), _ => { }))
            await LoadAsync();
    }

    [RelayCommand]
    private async Task PostAvailabilityAsync()
    {
        if (await RunAsync(() => svc.PostAvailabilityAsync(Start.ToUniversalTime(), End.ToUniversalTime()), _ => { }))
            await LoadAsync();
    }

    [RelayCommand] private Task ApproveAsync(TimeRequest r) => RespondAsync(r, true);
    [RelayCommand] private Task RejectAsync(TimeRequest r) => RespondAsync(r, false);
    [RelayCommand]
    private async Task CancelAsync(TimeRequest r)
    {
        if (await RunAsync(() => svc.CancelRequestAsync(r.Id), _ => { })) Requests.Remove(r);
    }

    async Task RespondAsync(TimeRequest r, bool approve)
    {
        if (await RunAsync(() => svc.RespondAsync(r.Id, approve), _ => { })) Requests.Remove(r);
    }
}
```
`Core/ViewModels/MySessionsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class MySessionsViewModel(ISessionService svc) : LoadableViewModel
{
    public ObservableCollection<TrainingSession> Sessions { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.MySessionsAsync, rows =>
    { Sessions.Clear(); foreach (var s in rows) Sessions.Add(s); });

    [RelayCommand] private Task CompleteAsync(TrainingSession s) => SetAsync(s, "completed");
    [RelayCommand] private Task CancelAsync(TrainingSession s) => SetAsync(s, "cancelled");
    [RelayCommand] private Task NoShowAsync(TrainingSession s) => SetAsync(s, "no_show");

    async Task SetAsync(TrainingSession s, string status)
    {
        if (await RunAsync(() => svc.SetSessionStatusAsync(s.Id, status), _ => { })) s.Status = status;
        await LoadAsync();
    }
}
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring**

`TimeRequestsPage.xaml`: top card with `DatePicker`/`TimePicker` pairs bound to `Start`/`End` (use `DatePicker.Date` + `TimePicker.Time` two-way to a small converter, or the CommunityToolkit `DateTimePicker`-style pair of controls), a `Picker`/`Entry` for `CoachId` (member), `Message` entry, **Submit** (member shell) and **Post availability** (coach shell) buttons; below it a CollectionView over `Requests` with **Approve/Reject** and **Cancel** buttons. `MySessionsPage.xaml`: CollectionView over `Sessions` showing `Title`, `ScheduledStart` (`StringFormat='{0:f}'`), `Status`; coach shell shows **Complete / Cancel / No-show** buttons (`IsVisible` from a `CanManage` page flag set in code-behind based on `Shell` type). Register everything; add "Sessions" and "Requests" tabs to `MemberShell` and `CoachShell`.

- [ ] **Step 6: Manual verification** (Review Focus #4)

Member (hired, active) submits a request → coach sees it (and gets a notification row) → Approve creates a session; a second overlapping request approved → conflict banner; a 11:00–12:00 request after a 10:00–11:00 session approves fine. As another member, approving someone else's request via the UI shows the unauthorized banner.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: time requests and training sessions"`

---

### Task 14: Attendance and gym check-ins (F8)

**Files:**
- Create: `Core/Services/AttendanceService.cs`, `Core/ViewModels/AttendanceViewModel.cs`, `Core/ViewModels/CheckInViewModel.cs`, `App/Views/AttendancePage.xaml(.cs)`, `App/Views/CheckInPage.xaml(.cs)`
- Modify: `App/CoachShell.xaml`, `App/StaffShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/AttendanceTests.cs`

**Interfaces:**
- Consumes: RPCs `record_session_attendance(p_session_id, p_member_id, p_status)`, `record_check_in(p_member_id)`; models `Member`, `Profile`, `TrainingSession`.
- Produces: `IAttendanceService { RecordAsync(int sessionId, int memberId, string status); CheckInAsync(int memberId); FindMembersAsync(string usernameFragment) → AppResult<IReadOnlyList<MemberHit>> }`, `record MemberHit(int MemberId, string Username)`.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AttendanceTests
{
    [Fact]
    public async Task Record_passes_all_three_rpc_args()
    {
        var gw = new FakeGateway();
        await new AttendanceService(gw).RecordAsync(5, 10, "late");
        var a = gw.RpcCalls[0].Args!;
        Assert.Equal(("record_session_attendance", 5, 10, "late"), (gw.RpcCalls[0].Fn, a["p_session_id"], a["p_member_id"], a["p_status"]));
    }

    [Fact]
    public async Task FindMembers_matches_username_fragment_case_insensitively()
    {
        var gw = new FakeGateway();
        gw.Seed(new Member { Id = 1, UserId = "u1" }, new Member { Id = 2, UserId = "u2" });
        gw.Seed(new Profile { Id = "u1", Username = "Alice" }, new Profile { Id = "u2", Username = "Bob" });
        var r = await new AttendanceService(gw).FindMembersAsync("ali");
        Assert.Equal(new[] { 1 }, r.Value!.Select(h => h.MemberId));
    }

    [Fact]
    public async Task CheckIn_vm_shows_validation_message_when_membership_inactive()
    {
        var gw = new FakeGateway { Throw = new Exception("no_active_membership") };
        var vm = new CheckInViewModel(new AttendanceService(gw));
        await vm.CheckInCommand.ExecuteAsync(new MemberHit(2, "Bob"));
        Assert.Contains("isn't allowed", vm.ErrorMessage);
        Assert.Null(vm.LastCheckedIn);
    }

    [Fact]
    public async Task CheckIn_vm_records_last_checked_in_on_success()
    {
        var vm = new CheckInViewModel(new AttendanceService(new FakeGateway()));
        await vm.CheckInCommand.ExecuteAsync(new MemberHit(1, "Alice"));
        Assert.Equal("Alice", vm.LastCheckedIn);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/AttendanceService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public sealed record MemberHit(int MemberId, string Username);

public interface IAttendanceService
{
    Task<AppResult<bool>> RecordAsync(int sessionId, int memberId, string status);
    Task<AppResult<bool>> CheckInAsync(int memberId);
    Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string usernameFragment);
}

public sealed class AttendanceService(ISupabaseGateway gw) : IAttendanceService
{
    public Task<AppResult<bool>> RecordAsync(int sessionId, int memberId, string status) =>
        Safe.RunAsync(async () =>
        {
            await gw.RpcAsync("record_session_attendance",
                new() { ["p_session_id"] = sessionId, ["p_member_id"] = memberId, ["p_status"] = status });
            return true;
        });

    public Task<AppResult<bool>> CheckInAsync(int memberId) =>
        Safe.RunAsync(async () => { await gw.RpcAsync("record_check_in", new() { ["p_member_id"] = memberId }); return true; });

    public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string fragment) =>
        Safe.RunAsync<IReadOnlyList<MemberHit>>(async () =>
        {
            var members = await gw.ListAsync<Member>();
            var profiles = await gw.ListAsync<Profile>();
            return members.Join(profiles, m => m.UserId, p => p.Id, (m, p) => new MemberHit(m.Id, p.Username))
                .Where(h => h.Username.Contains(fragment, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
        });
}
```
`Core/ViewModels/CheckInViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class CheckInViewModel(IAttendanceService svc) : LoadableViewModel
{
    public ObservableCollection<MemberHit> Hits { get; } = new();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string? lastCheckedIn;

    [RelayCommand]
    private Task SearchAsync() => RunAsync(() => svc.FindMembersAsync(Search), rows =>
    { Hits.Clear(); foreach (var h in rows) Hits.Add(h); });

    [RelayCommand]
    private async Task CheckInAsync(MemberHit hit)
    {
        LastCheckedIn = null;
        if (await RunAsync(() => svc.CheckInAsync(hit.MemberId), _ => { })) LastCheckedIn = hit.Username;
    }
}
```
`Core/ViewModels/AttendanceViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class AttendanceViewModel(ISessionService sessions, IAttendanceService attendance) : LoadableViewModel
{
    public ObservableCollection<TrainingSession> Sessions { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(sessions.MySessionsAsync, rows =>
    { Sessions.Clear(); foreach (var s in rows.Where(s => s.Status == "scheduled" || s.Status == "completed")) Sessions.Add(s); });

    // parameter format "<sessionId>|<status>" keeps the XAML button binding to a single string
    [RelayCommand]
    private async Task MarkAsync(string arg)
    {
        var parts = arg.Split('|');
        var session = Sessions.First(s => s.Id == int.Parse(parts[0]));
        await RunAsync(() => attendance.RecordAsync(session.Id, session.MemberId, parts[1]), _ => { });
    }
}
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring**

`AttendancePage.xaml` (coach): CollectionView over `Sessions`; per row four buttons (Present/Late/Absent/Excused) with `CommandParameter="{Binding Id, StringFormat='{0}|present'}"` etc., bound to `MarkCommand` via RelativeSource. `CheckInPage.xaml` (staff): `SearchBar` bound to `Search` with `SearchCommand`, CollectionView over `Hits` with a **Check in** button; a green `Label` showing `LastCheckedIn`. Register services/VMs/pages; add "Attendance" to `CoachShell`, "Check-in" flyout item to `StaffShell`.

- [ ] **Step 6: Manual verification**

Coach marks attendance (re-marking updates, not duplicates — `session_attendance` has one row per session/member). Employee checks in an active member (row in `check_ins`) and is blocked for a member without membership.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: attendance and check-ins"`

---

### Task 15: Notifications with Realtime (F9)

**Files:**
- Create: `Core/Services/NotificationService.cs`, `Core/ViewModels/NotificationsViewModel.cs`, `App/Views/NotificationsPage.xaml(.cs)`
- Modify: `App/MemberShell.xaml`, `CoachShell.xaml`, `StaffShell.xaml`, `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/NotificationTests.cs`

**Interfaces:**
- Consumes: `UserNotification`; `ISupabaseGateway.SubscribeInsertsAsync<T>`.
- Produces: `INotificationService { ListAsync(); MarkReadAsync(int id); SubscribeAsync(Action<UserNotification> onNew) }`; `NotificationsViewModel` exposing `Items`, `UnreadCount`, `LoadCommand`, `MarkReadCommand(UserNotification)`, and starting the subscription in `StartAsync()`.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class NotificationTests
{
    [Fact]
    public async Task Load_orders_newest_first_and_counts_unread()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new UserNotification { Id = 1, UserId = "u1", Title = "old", CreatedAt = DateTime.UtcNow.AddDays(-1), IsRead = true },
                new UserNotification { Id = 2, UserId = "u1", Title = "new", CreatedAt = DateTime.UtcNow, IsRead = false });
        var vm = new NotificationsViewModel(new NotificationService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "new", "old" }, vm.Items.Select(n => n.Title));
        Assert.Equal(1, vm.UnreadCount);
    }

    [Fact]
    public async Task Realtime_insert_is_prepended_and_bumps_unread()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        var vm = new NotificationsViewModel(new NotificationService(gw));
        await vm.StartAsync();
        gw.InsertSubscriber!(new UserNotification { Id = 9, UserId = "u1", Title = "live" });
        Assert.Equal("live", vm.Items[0].Title);
        Assert.Equal(1, vm.UnreadCount);
    }

    [Fact]
    public async Task Realtime_insert_for_another_user_is_ignored()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        var vm = new NotificationsViewModel(new NotificationService(gw));
        await vm.StartAsync();
        gw.InsertSubscriber!(new UserNotification { Id = 9, UserId = "someone-else", Title = "nope" });
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task MarkRead_updates_item_and_count()
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        var n = new UserNotification { Id = 2, UserId = "u1", IsRead = false, CreatedAt = DateTime.UtcNow };
        gw.Seed(n);
        var vm = new NotificationsViewModel(new NotificationService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.MarkReadCommand.ExecuteAsync(n);
        Assert.Equal(0, vm.UnreadCount);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/NotificationService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface INotificationService
{
    string? UserId { get; }
    Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync();
    Task<AppResult<bool>> MarkReadAsync(int id);
    Task<AppResult<bool>> SubscribeAsync(Action<UserNotification> onNew);
}

public sealed class NotificationService(ISupabaseGateway gw) : INotificationService
{
    public string? UserId => gw.CurrentUserId;

    public Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync() =>
        Safe.RunAsync(() => { var uid = gw.CurrentUserId; return gw.ListAsync<UserNotification>(n => n.UserId == uid); });

    public Task<AppResult<bool>> MarkReadAsync(int id) =>
        Safe.RunAsync(async () =>
        {
            var row = (await gw.ListAsync<UserNotification>(n => n.Id == id)).First();
            row.IsRead = true;
            await gw.UpdateAsync(row);
            return true;
        });

    public Task<AppResult<bool>> SubscribeAsync(Action<UserNotification> onNew) =>
        Safe.RunAsync(async () => { await gw.SubscribeInsertsAsync(onNew); return true; });
}
```
`Core/ViewModels/NotificationsViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class NotificationsViewModel(INotificationService svc) : LoadableViewModel
{
    public ObservableCollection<UserNotification> Items { get; } = new();
    [ObservableProperty] private int unreadCount;
    bool subscribed;

    public async Task StartAsync()
    {
        if (subscribed) return;
        subscribed = await RunAsync(() => svc.SubscribeAsync(OnNew), _ => { });
    }

    void OnNew(UserNotification n)
    {
        if (n.UserId != svc.UserId) return;
        Items.Insert(0, n);
        Recount();
    }

    void Recount() => UnreadCount = Items.Count(i => !i.IsRead);

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.ListAsync, rows =>
        { Items.Clear(); foreach (var n in rows.OrderByDescending(n => n.CreatedAt)) Items.Add(n); Recount(); });
        await StartAsync();
    }

    [RelayCommand]
    private async Task MarkReadAsync(UserNotification n)
    {
        if (await RunAsync(() => svc.MarkReadAsync(n.Id), _ => { })) { n.IsRead = true; Recount(); }
    }
}
```
(Realtime callbacks may arrive on a background thread; the MAUI page marshals via `MainThread` — see Step 5.)

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring**

`NotificationsPage.xaml`: CollectionView over `Items` showing `Title`, `Body`, `CreatedAt` with unread items in bold (`FontAttributes` via a `DataTrigger` on `IsRead`); tap → `MarkReadCommand`. In `NotificationsPage.xaml.cs`, wrap the VM's collection updates: since `ObservableCollection.Insert` must run on the UI thread, register the VM with a dispatcher by subscribing in the page: `protected override async void OnAppearing() { base.OnAppearing(); await MainThread.InvokeOnMainThreadAsync(() => vm.LoadCommand.ExecuteAsync(null)); }` and in `MauiProgram.cs` register `INotificationService` with a decorator-free service but set `BindingBase.EnableCollectionSynchronization`-free approach: change `NotificationsViewModel.OnNew` to `MainThread`-safe by injecting an `Action<Action> post` constructor parameter defaulting to direct invocation (`post ??= a => a()`); the App passes `a => MainThread.BeginInvokeOnMainThread(a)`. (Core stays MAUI-free; tests use the default.) Add a Notifications tab to all three shells; show `UnreadCount` as the tab badge via `Shell.TabBarBadge`-style label binding in each shell's page title (`Title="{Binding UnreadCount, StringFormat='Alerts ({0})'}"`).

- [ ] **Step 6: Manual verification**

With the app open as `alice`, verify one of her payments as staff in Studio (`select verify_payment(...)` as an employee JWT or via the staff app). Expected: a "Payment verified" row appears live without refresh; marking it read lowers the count; restarting shows it persisted.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: notifications with realtime"`

---

### Task 16: Admin management, revenue, audit (F10, F11)

**Files:**
- Create: `Core/Services/AdminService.cs`, `Core/ViewModels/UsersViewModel.cs`, `Core/ViewModels/CatalogAdminViewModel.cs`, `Core/ViewModels/RevenueViewModel.cs`, `Core/ViewModels/AuditViewModel.cs`, `App/Views/UsersPage.xaml(.cs)`, `CatalogAdminPage.xaml(.cs)`, `RevenuePage.xaml(.cs)`, `AuditPage.xaml(.cs)`
- Modify: `App/StaffShell.xaml` (+ `.cs`), `App/MauiProgram.cs`
- Test: `tests/GymMembership.Tests/AdminTests.cs`

**Interfaces:**
- Consumes: `Profile`, `UserRole`, `Role`, `MembershipPackage`, `Amenity`, `AuditEntry`, `RevenueRow`; RPCs `assign_role`, `revoke_role`, `set_user_active`, `revenue_summary(p_from, p_to)`.
- Produces: `IAdminService { UsersAsync() → AppResult<IReadOnlyList<UserRow>>; AssignRoleAsync(string userId, string role); RevokeRoleAsync(...); SetActiveAsync(string userId, bool active); SavePackageAsync(MembershipPackage); SaveAmenityAsync(Amenity); RevenueAsync(DateOnly from, DateOnly to); AuditAsync() }`, `record UserRow(string UserId, string Username, bool IsActive, IReadOnlyList<string> Roles)`. Admin-only shell items appear only when `StaffShell` was built with `AppRole.Admin`.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AdminTests
{
    [Fact]
    public async Task Users_joins_profiles_with_role_names()
    {
        var gw = new FakeGateway();
        gw.Seed(new Profile { Id = "u1", Username = "alice", IsActive = true });
        gw.Seed(new Role { RoleId = 1, Name = "member" }, new Role { RoleId = 2, Name = "coach" });
        gw.Seed(new UserRole { UserId = "u1", RoleId = 1 }, new UserRole { UserId = "u1", RoleId = 2 });
        var row = Assert.Single((await new AdminService(gw).UsersAsync()).Value!);
        Assert.Equal(new[] { "coach", "member" }, row.Roles.OrderBy(r => r));
    }

    [Fact]
    public async Task AssignRole_calls_rpc_with_named_args()
    {
        var gw = new FakeGateway();
        await new AdminService(gw).AssignRoleAsync("u1", "coach");
        Assert.Equal(("assign_role", "u1", "coach"), (gw.RpcCalls[0].Fn, gw.RpcCalls[0].Args!["p_user"], gw.RpcCalls[0].Args!["p_role"]));
    }

    [Fact]
    public async Task Revenue_rejects_inverted_range_locally()
    {
        var gw = new FakeGateway();
        var r = await new AdminService(gw).RevenueAsync(new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 1));
        Assert.Equal(AppErrorKind.Validation, r.Error);
        Assert.Empty(gw.RpcCalls);
    }

    [Fact]
    public async Task Forbidden_rpc_surfaces_unauthorized_in_vm()
    {
        var gw = new FakeGateway { Throw = new Exception("forbidden") };
        var vm = new RevenueViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains("not allowed", vm.ErrorMessage);
    }

    [Fact]
    public async Task Audit_lists_newest_first()
    {
        var gw = new FakeGateway();
        gw.Seed(new AuditEntry { Id = 1, Action = "a", CreatedAt = DateTime.UtcNow.AddHours(-2) }, new AuditEntry { Id = 2, Action = "b", CreatedAt = DateTime.UtcNow });
        var vm = new AuditViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "b", "a" }, vm.Entries.Select(e => e.Action));
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/AdminService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public sealed record UserRow(string UserId, string Username, bool IsActive, IReadOnlyList<string> Roles);

public interface IAdminService
{
    Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync();
    Task<AppResult<bool>> AssignRoleAsync(string userId, string role);
    Task<AppResult<bool>> RevokeRoleAsync(string userId, string role);
    Task<AppResult<bool>> SetActiveAsync(string userId, bool active);
    Task<AppResult<bool>> SavePackageAsync(MembershipPackage package);
    Task<AppResult<bool>> SaveAmenityAsync(Amenity amenity);
    Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync();
    Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync();
    Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to);
    Task<AppResult<IReadOnlyList<AuditEntry>>> AuditAsync();
}

public sealed class AdminService(ISupabaseGateway gw) : IAdminService
{
    public Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync() =>
        Safe.RunAsync<IReadOnlyList<UserRow>>(async () =>
        {
            var profiles = await gw.ListAsync<Profile>();
            var roles = await gw.ListAsync<Role>();
            var links = await gw.ListAsync<UserRole>();
            return profiles.Select(p => new UserRow(p.Id, p.Username, p.IsActive,
                links.Where(l => l.UserId == p.Id).Select(l => roles.First(r => r.RoleId == l.RoleId).Name).ToList())).ToList();
        });

    public Task<AppResult<bool>> AssignRoleAsync(string userId, string role) => Rpc("assign_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> RevokeRoleAsync(string userId, string role) => Rpc("revoke_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> SetActiveAsync(string userId, bool active) => Rpc("set_user_active", new() { ["p_user"] = userId, ["p_active"] = active });

    public Task<AppResult<bool>> SavePackageAsync(MembershipPackage p) =>
        Safe.RunAsync(async () => { if (p.Id == 0) await gw.InsertAsync(p); else await gw.UpdateAsync(p); return true; });

    public Task<AppResult<bool>> SaveAmenityAsync(Amenity a) =>
        Safe.RunAsync(async () => { if (a.Id == 0) await gw.InsertAsync(a); else await gw.UpdateAsync(a); return true; });

    public Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync() => Safe.RunAsync(() => gw.ListAsync<MembershipPackage>());
    public Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync() => Safe.RunAsync(() => gw.ListAsync<Amenity>());

    public async Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<RevenueRow>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () => (IReadOnlyList<RevenueRow>)await gw.RpcAsync<List<RevenueRow>>("revenue_summary",
            new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }));
    }

    public Task<AppResult<IReadOnlyList<AuditEntry>>> AuditAsync() =>
        Safe.RunAsync(async () => (IReadOnlyList<AuditEntry>)(await gw.ListAsync<AuditEntry>()).OrderByDescending(a => a.CreatedAt).ToList());

    Task<AppResult<bool>> Rpc(string fn, Dictionary<string, object> args) =>
        Safe.RunAsync(async () => { await gw.RpcAsync(fn, args); return true; });
}
```
In the fake, `RpcAsync<List<RevenueRow>>` returns `default` when no result is set — the revenue test cases above don't depend on its value (the forbidden test throws first); `RevenueViewModel` must treat a null list as empty.

`Core/ViewModels/RevenueViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class RevenueViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<RevenueRow> Rows { get; } = new();
    [ObservableProperty] private DateTime from = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime to = DateTime.Today;
    [ObservableProperty] private decimal total;

    [RelayCommand]
    private Task LoadAsync() => RunAsync(() => svc.RevenueAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To)), rows =>
    {
        Rows.Clear();
        foreach (var r in rows ?? []) Rows.Add(r);
        Total = Rows.Sum(r => r.Total);
    });
}
```
`Core/ViewModels/AuditViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class AuditViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<AuditEntry> Entries { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.AuditAsync, rows => { Entries.Clear(); foreach (var e in rows) Entries.Add(e); });
}
```
`Core/ViewModels/UsersViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class UsersViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<UserRow> Users { get; } = new();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.UsersAsync, rows => { Users.Clear(); foreach (var u in rows) Users.Add(u); });

    // arg "<userId>|<role>"
    [RelayCommand]
    private async Task AssignAsync(string arg)
    {
        var p = arg.Split('|');
        if (await RunAsync(() => svc.AssignRoleAsync(p[0], p[1]), _ => { })) await LoadAsync();
    }

    [RelayCommand]
    private async Task RevokeAsync(string arg)
    {
        var p = arg.Split('|');
        if (await RunAsync(() => svc.RevokeRoleAsync(p[0], p[1]), _ => { })) await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(UserRow u)
    {
        if (await RunAsync(() => svc.SetActiveAsync(u.UserId, !u.IsActive), _ => { })) await LoadAsync();
    }
}
```
`Core/ViewModels/CatalogAdminViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class CatalogAdminViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<MembershipPackage> Packages { get; } = new();
    public ObservableCollection<Amenity> Amenities { get; } = new();
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private decimal newPrice;
    [ObservableProperty] private int newDurationDays = 30;

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.AllPackagesAsync, r => { Packages.Clear(); foreach (var p in r) Packages.Add(p); });
        await RunAsync(svc.AllAmenitiesAsync, r => { Amenities.Clear(); foreach (var a in r) Amenities.Add(a); });
    }

    [RelayCommand]
    private async Task AddPackageAsync()
    {
        if (await RunAsync(() => svc.SavePackageAsync(new MembershipPackage { Name = NewName.Trim(), Price = NewPrice, DurationDays = NewDurationDays }), _ => { }))
        { NewName = ""; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task TogglePackageAsync(MembershipPackage p)
    {
        p.IsActive = !p.IsActive;
        if (await RunAsync(() => svc.SavePackageAsync(p), _ => { })) await LoadAsync(); else p.IsActive = !p.IsActive;
    }

    [RelayCommand]
    private async Task AddAmenityAsync()
    {
        if (await RunAsync(() => svc.SaveAmenityAsync(new Amenity { Name = NewName.Trim() }), _ => { })) { NewName = ""; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task ToggleAmenityAsync(Amenity a)
    {
        a.IsActive = !a.IsActive;
        if (await RunAsync(() => svc.SaveAmenityAsync(a), _ => { })) await LoadAsync(); else a.IsActive = !a.IsActive;
    }
}
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring**

Four pages with the established layout (ErrorBanner + list). `UsersPage`: each row shows username, roles, **Toggle active**, and role buttons (`CommandParameter="{Binding UserId, StringFormat='{0}|coach'}"` for Assign coach / `|employee` / `|admin`; Revoke likewise). `CatalogAdminPage`: entry + price + duration + **Add package**; lists with an **Active/Inactive** toggle button. `RevenuePage`: two `DatePicker`s bound to `From`/`To`, **Load**, list of `Day / Cur / Total`, and a total label. `AuditPage`: list of `CreatedAt`, `Action`, `Entity`, `EntityId`. In `StaffShell.xaml.cs` take the role: `public StaffShell(AppRole role) { InitializeComponent(); if (role != AppRole.Admin) foreach (var item in Items.Where(i => (i.ClassId ?? "") == "admin").ToList()) Items.Remove(item); }` and set `ClassId="admin"` on the Users/Catalog/Revenue/Audit `FlyoutItem`s. Register the service, four VMs, four pages.

- [ ] **Step 6: Manual verification**

As admin: create a package → appears for members; promote a user to coach (a `coaches` row appears); deactivate a user; revenue shows verified payments grouped by day; audit lists `payment.verify` and `role.assign` entries. As employee: the admin-only items are absent, and calling an admin RPC directly (from the Supabase SQL editor with the employee JWT) returns `forbidden`.

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: admin users, catalog, revenue, audit"`

---

### Task 17: Frontend polish, theming, settings, and release checks

**Files:**
- Create: `Core/Services/SettingsService.cs`, `Core/ViewModels/SettingsViewModel.cs`, `App/Views/SettingsPage.xaml(.cs)`, `App/Resources/Styles/AppTheme.xaml`
- Modify: `App/App.xaml`, all three shells (Settings tab + sign-out), `App/MauiProgram.cs`, `docs/specs/PRD.md` (open questions answered)
- Test: `tests/GymMembership.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: `UserSettings`; `IAuthService.SignOutAsync`; `ShellNavigator.GoToLogin()`.
- Produces: `ISettingsService { LoadAsync() → AppResult<UserSettings>; SaveThemeAsync(string theme) }` (theme ∈ `system|light|dark`; anything else → `Validation`, never reaching the DB `CHECK`); Settings page with theme picker, language, and **Sign out**.

- [ ] **Step 1: Failing tests**

```csharp
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class SettingsTests
{
    [Theory]
    [InlineData("dark")] [InlineData("light")] [InlineData("system")]
    public async Task Valid_themes_are_saved(string theme)
    {
        var gw = new FakeGateway { CurrentUserId = "u1" };
        gw.Seed(new UserSettings { Id = 1, UserId = "u1" });
        Assert.True((await new SettingsService(gw).SaveThemeAsync(theme)).Ok);
    }

    [Fact]
    public async Task Unknown_theme_is_rejected_locally()
    {
        var r = await new SettingsService(new FakeGateway()).SaveThemeAsync("neon");
        Assert.Equal(AppErrorKind.Validation, r.Error);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

`Core/Services/SettingsService.cs`:
```csharp
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface ISettingsService
{
    Task<AppResult<UserSettings>> LoadAsync();
    Task<AppResult<bool>> SaveThemeAsync(string theme);
}

public sealed class SettingsService(ISupabaseGateway gw) : ISettingsService
{
    static readonly string[] Themes = ["system", "light", "dark"];

    public Task<AppResult<UserSettings>> LoadAsync() =>
        Safe.RunAsync(async () => { var uid = gw.CurrentUserId; return (await gw.ListAsync<UserSettings>(s => s.UserId == uid)).First(); });

    public async Task<AppResult<bool>> SaveThemeAsync(string theme)
    {
        if (!Themes.Contains(theme)) return AppResult<bool>.Fail(AppErrorKind.Validation, "unknown theme");
        return await Safe.RunAsync(async () =>
        {
            var s = (await LoadAsync()).Value ?? throw new Exception("not_found");
            s.Theme = theme;
            await gw.UpdateAsync(s);
            return true;
        });
    }
}
```
`Core/ViewModels/SettingsViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class SettingsViewModel(ISettingsService settings, IAuthService auth, Action<string> applyTheme, Action signedOut) : LoadableViewModel
{
    public string[] Themes { get; } = ["system", "light", "dark"];
    [ObservableProperty] private string theme = "system";

    [RelayCommand]
    private Task LoadAsync() => RunAsync(settings.LoadAsync, s => { Theme = s.Theme; applyTheme(s.Theme); });

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (await RunAsync(() => settings.SaveThemeAsync(Theme), _ => { })) applyTheme(Theme);
    }

    [RelayCommand]
    private async Task SignOutAsync() { await auth.SignOutAsync(); signedOut(); }
}
```

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: App wiring and design pass**

Invoke the `frontend-design:frontend-design` skill before touching XAML. Then:
- `AppTheme.xaml`: define light/dark `AppThemeBinding` colours as named resources (`Surface`, `OnSurface`, `Accent`, `Danger`) and default `Border`, `Button`, `Label` styles; replace the hard-coded colours in `ErrorBanner` and pages with these resources. Replace the `dotnet_bot.png` tab icons with real icons (`SymbolIcon`/font glyphs).
- `applyTheme`: `s => Application.Current!.UserAppTheme = s switch { "dark" => AppTheme.Dark, "light" => AppTheme.Light, _ => AppTheme.Unspecified }`; `signedOut`: `() => nav.GoToLogin()`. Register in `MauiProgram.cs` and add the Settings tab to all three shells. Apply the saved theme right after login by invoking the settings load once in `ShellNavigator.GoTo`.
- Windows: staff pages use a two-column `Grid` (list + detail) when `DeviceInfo.Idiom == DeviceIdiom.Desktop`; mobile pages keep the single-column cards.
- Accessibility: set `SemanticProperties.Description` on every icon-only button and verify contrast ≥ 4.5:1 for text in both themes.

- [ ] **Step 6: Final verification**

Run: `supabase db reset && supabase test db` → Expected: all pgTAP files pass.
Run: `dotnet test` → Expected: all xUnit tests pass.
Run the manual UAT script for each role covering PRD §4 flows 1–4 (avail → verify → active; renew; book training; expiry — trigger by `select expire_memberships();` after back-dating `ends_at` in Studio). Record results in `docs/uat-phase1.md`.
Run the `ponytail:ponytail-review` skill on the full change set and the `superpowers:requesting-code-review` skill before merging; address findings.
Update PRD §8 with the answers to the open questions and adjust `has_active_membership()` gating if the answer differs (single-function change in `20261004000200_rbac.sql` plus a new migration).

- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: settings, theming, accessibility, release checks"`

---

## Self-Review

**Spec coverage:**
- Stack/architecture (§1–2): Tasks 6–8. Schema changes (§3): Task 1 (profiles, enums, timestamptz, `audit_log`, `check_ins`, overlap constraint, `payments.proof_path`, payment CHECK, indexes), Task 2 (auth trigger). The spec's "fix FK names / duplicate relationship" is satisfied by the rewritten DDL (no stale `clients` names).
- RLS (§4): Task 3. RPCs (§5): Tasks 4–5 (`avail_membership`, `verify_payment`, `renew_membership`, `respond_time_request`, `record_session_attendance`, `record_check_in`, `expire_memberships`, plus `avail_amenity`, `verify_amenity_usage`, admin RPCs).
- Storage/Realtime (§6): Task 5 (buckets) and Task 15. Error handling (§7): Task 6. Testing (§8): pgTAP in Tasks 1–5, xUnit in 6–17.
- PRD F1 Task 7–8/17; F2–F4 Tasks 9–10; F5 Task 11; F6 Task 12; F7 Task 13; F8 Task 14; F9 Task 15; F10–F11 Task 16.
- Gap handled: sign-up UI. `IAuthService.SignUpAsync` exists (Task 7) but no sign-up page is planned; new members are created by sign-up in Studio during development. **Add a `SignUpPage` before launch if self-registration is wanted** (it is one page + view model over the existing service) — flagged rather than silently dropped.

**Placeholder scan:** no TBD/TODO. XAML for pages after the first two is described structurally against a fully specified pattern (Tasks 9–16); executors write the XAML from the described bindings — each binding target (property/command names) is defined in the task's ViewModel code.

**Type consistency:** RPC parameter names (`p_package_id`, `p_ump_id`, `p_payment_id`, `p_approve`, `p_id`, `p_session_id`, `p_member_id`, `p_status`, `p_user`, `p_role`, `p_active`, `p_from`, `p_to`, `p_amenity_id`) match between SQL (Tasks 4–5) and C# (Tasks 9–16). `Status` constants and model property names are defined once in Task 6 and reused unchanged. `LoadableViewModel.RunAsync` signature is identical everywhere. `AppRole` is defined in Task 7 and consumed by Tasks 8 and 16.

**Review Focus coverage:** #1 → Task 3 tests; #2 → Task 4 tests; #3 → Task 4 test "renewal extends from now"; #4 → Task 5 tests and Task 13 manual check; #5 → Task 7 tests and Task 8 manual check.
