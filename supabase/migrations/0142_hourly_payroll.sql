-- 0142: hourly and per-session pay on Payroll (plan docs/superpowers/plans/2026-11-24-hourly-payroll.md, H1-H17).
-- Pay type per person and per role (H1); a personal rate beats the role default (H14: no staff_salaries row = follows the role);
-- front desk hours = closed cash shifts (H2), hourly coach = check-in/out (H3), per-session coach = completed sessions (H15);
-- each record is approved or rejected before it pays (H12); overlapping approved time counts once (H13); hour corrections (H5);
-- categories on bonus/deduction lines (H16); gross (H17); Mark as paid freezes type, quantity and rate (H7).

-- Tables ---------------------------------------------------------------------------------------------------------------
alter table public.staff_salaries add column pay_type text not null default 'fixed' check (pay_type in ('fixed', 'hourly', 'per_session'));
alter table public.role_salaries add column pay_type text not null default 'fixed' check (pay_type in ('fixed', 'hourly', 'per_session'));

alter table public.payroll_lines
  add column pay_type text check (pay_type in ('fixed', 'hourly', 'per_session')),
  add column quantity numeric(8,2),
  add column rate numeric(12,2),
  add column category text check (category in ('regular_bonus', 'overtime', 'holiday', 'other'));
update public.payroll_lines set category = 'other' where kind <> 'salary';
alter table public.payroll_lines add constraint payroll_lines_category_needed check (kind = 'salary' or category is not null);
-- an hourly or per-session person with nothing approved is still paid (₱0) and listed (H11)
alter table public.payroll_lines drop constraint payroll_lines_amount_check;
alter table public.payroll_lines add constraint payroll_lines_amount_check check (amount > 0 or (kind = 'salary' and amount >= 0));

create table public.payroll_hour_adjustments (
  adjustment_id bigint generated always as identity primary key,
  period_id bigint not null references public.payroll_periods(period_id),
  user_id uuid not null references public.profiles(id),
  quantity numeric(8,2) not null check (quantity <> 0 and abs(quantity) <= 1000),   -- hours, or sessions for per-session pay
  note text not null check (length(btrim(note)) between 1 and 200),
  created_by uuid not null references public.profiles(id),
  created_at timestamptz not null default now()
);
create index payroll_hour_adjustments_period_user on public.payroll_hour_adjustments(period_id, user_id);
alter table public.payroll_hour_adjustments enable row level security;
comment on table public.payroll_hour_adjustments is '+/- hours (or sessions) per person per period, with a note (0142)';

create table public.payroll_time_reviews (
  source text not null check (source in ('cash_shift', 'coach_visit', 'training_session')),
  source_id bigint not null,
  status text not null check (status in ('approved', 'rejected')),
  reason text check (reason is null or length(btrim(reason)) between 1 and 200),
  reviewed_by uuid not null references public.profiles(id),
  reviewed_at timestamptz not null default now(),
  primary key (source, source_id),
  check (status = 'approved' or reason is not null)
);
alter table public.payroll_time_reviews enable row level security;
comment on table public.payroll_time_reviews is 'Approved/rejected shifts, visits and sessions for payroll; no row = Pending (0142)';

-- H14 backfill: 0137 copied the role default into every person's row; rows equal to their role default follow the role again
delete from public.staff_salaries ss
using public.role_salaries rs
where rs.amount = ss.amount and rs.pay_type = ss.pay_type
  and rs.role = (select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
                 where ur.user_id = ss.user_id and r.name in ('employee', 'coach', 'subadmin', 'admin')
                 order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end limit 1);

-- Private helpers (called only from the payroll functions below) ------------------------------------------------------
create function public.payroll_role(p_user uuid) returns text
language sql stable set search_path = '' as $$
  select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
  where ur.user_id = p_user and r.name in ('employee', 'coach', 'subadmin', 'admin')
  order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end
  limit 1
$$;

-- Who is on the period and how they are paid: the current pay while a draft, the frozen salary line once paid.
create function public.payroll_pay(p_period bigint)
returns table (user_id uuid, full_name text, role text, pay_type text, rate numeric, no_salary boolean, personal boolean)
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  roster as (
    select p.id, coalesce(nullif(btrim(p.full_name), ''), p.username) as full_name,
           coalesce(public.payroll_role(p.id), 'employee') as role
    from public.profiles p, per
    where (per.paid_at is null and p.is_active and p.deleted_at is null and (
             exists (select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id
                     where ur.user_id = p.id and r.name in ('employee', 'coach'))
             or exists (select 1 from public.staff_salaries ss where ss.user_id = p.id)
             or exists (select 1 from public.payroll_lines pl where pl.period_id = per.period_id and pl.user_id = p.id)))
       or (per.paid_at is not null and exists (select 1 from public.payroll_lines pl where pl.period_id = per.period_id and pl.user_id = p.id))
  )
  select r.id, r.full_name, r.role,
         case when per.paid_at is null then coalesce(ss.pay_type, rs.pay_type, 'fixed') else coalesce(sl.pay_type, 'fixed') end,
         case when per.paid_at is null then coalesce(ss.amount, rs.amount, 0) else coalesce(sl.rate, sl.amount, 0) end,
         case when per.paid_at is null then ss.amount is null and rs.amount is null else sl.line_id is null end,
         ss.user_id is not null
  from roster r cross join per
  left join public.staff_salaries ss on ss.user_id = r.id
  left join public.role_salaries rs on rs.role = r.role
  left join lateral (select * from public.payroll_lines pl where pl.period_id = per.period_id and pl.user_id = r.id and pl.kind = 'salary'
                     order by pl.line_id limit 1) sl on true
$$;

-- Every shift / visit / session that can pay someone in the period, with its review status and flags (H6, H12, H13).
create function public.payroll_records(p_period bigint)
returns table (user_id uuid, source text, source_id bigint, starts_at timestamptz, ends_at timestamptz, hours numeric,
               label text, status text, reason text, flags text[])
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  pay as (select * from public.payroll_pay(p_period)),
  raw as (
    select cs.cashier_id as user_id, 'cash_shift'::text as source, cs.shift_id as source_id, cs.opened_at as starts_at,
           cs.closed_at as ends_at, 'Cash shift #' || cs.shift_id as label, false as auto_closed
    from public.cash_shifts cs join pay on pay.user_id = cs.cashier_id and pay.pay_type = 'hourly' and pay.role <> 'coach', per
    where (cs.opened_at at time zone 'Asia/Manila')::date between per.starts_on and per.ends_on
    union all
    select cv.coach_id, 'coach_visit', cv.coach_visit_id, cv.checked_in_at, cv.checked_out_at, 'Check-in', cv.auto_closed
    from public.coach_visits cv join pay on pay.user_id = cv.coach_id and pay.pay_type = 'hourly' and pay.role = 'coach', per
    where (cv.checked_in_at at time zone 'Asia/Manila')::date between per.starts_on and per.ends_on
    union all
    select ts.coach_id, 'training_session', ts.training_session_id, ts.scheduled_start, ts.scheduled_end,
           coalesce(nullif(btrim(m.full_name), ''), m.username, nullif(btrim(ts.walk_in_name), ''), 'Walk-in'), false
    from public.training_sessions ts join pay on pay.user_id = ts.coach_id and pay.pay_type = 'per_session'
    left join public.profiles m on m.id = ts.member_id
    cross join per
    where ts.status = 'completed' and (ts.held_until is null or ts.held_until > now())
      and (ts.scheduled_start at time zone 'Asia/Manila')::date between per.starts_on and per.ends_on
  )
  select r.user_id, r.source, r.source_id, r.starts_at, r.ends_at,
         case when r.ends_at is null then 0 else round(extract(epoch from (r.ends_at - r.starts_at)) / 3600.0, 2) end,
         r.label,
         coalesce(tr.status, 'pending'), tr.reason,
         array_remove(array[
           case when r.ends_at is null then 'open' end,
           case when r.auto_closed then 'auto_closed' end,
           case when r.ends_at - r.starts_at > interval '12 hours' then 'long' end,
           case when r.source <> 'training_session' and r.ends_at is not null and exists (
             select 1 from raw o where o.user_id = r.user_id and o.source = r.source and o.source_id <> r.source_id
               and o.ends_at is not null and tstzrange(o.starts_at, o.ends_at) && tstzrange(r.starts_at, r.ends_at)) then 'overlap' end
         ], null)
  from raw r
  left join public.payroll_time_reviews tr on tr.source = r.source and tr.source_id = r.source_id
$$;

-- One person-row per period: the pay maths in one place, so the draft and the frozen line agree (H7, H13, H17).
create function public.payroll_basis(p_period bigint)
returns table (user_id uuid, full_name text, role text, pay_type text, rate numeric, no_salary boolean, personal boolean,
               logged numeric, adjustment numeric, payable numeric, base_pay numeric, bonuses numeric, deductions numeric,
               gross numeric, net numeric, pending int, flagged int)
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  recs as (select * from public.payroll_records(p_period)),
  calc as (
    select pay.*,
           case pay.pay_type
             when 'hourly' then coalesce((
               select round(sum(extract(epoch from (upper(x) - lower(x)))) / 3600.0, 2)
               from unnest((select range_agg(tstzrange(rc.starts_at, rc.ends_at)) from recs rc
                            where rc.user_id = pay.user_id and rc.status = 'approved' and rc.ends_at is not null)) x), 0)
             when 'per_session' then (select count(*) from recs rc where rc.user_id = pay.user_id and rc.status = 'approved')
             else 0 end::numeric(8,2) as logged,
           coalesce((select sum(a.quantity) from public.payroll_hour_adjustments a where a.period_id = p_period and a.user_id = pay.user_id), 0)::numeric(8,2) as adjustment,
           -- a paid period can't be decided any more, so nothing in it is pending (a shift closed after paying stays out)
           case when (select paid_at from per) is not null then 0
                else (select count(*) from recs rc where rc.user_id = pay.user_id and rc.status = 'pending' and rc.ends_at is not null) end::int as pending,
           (select count(*) from recs rc where rc.user_id = pay.user_id and cardinality(rc.flags) > 0)::int as flagged,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'bonus'), 0)::numeric(12,2) as bonuses,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'deduction'), 0)::numeric(12,2) as deductions,
           (select pl from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'salary' order by pl.line_id limit 1) as frozen
    from public.payroll_pay(p_period) pay
  ),
  based as (
    select c.*,
           case when (select paid_at from per) is not null then coalesce((c.frozen).quantity, case when c.pay_type = 'fixed' then 1 else 0 end)
                when c.pay_type = 'fixed' then 1
                else greatest(0, c.logged + c.adjustment) end::numeric(8,2) as payable,
           case when (select paid_at from per) is not null then coalesce((c.frozen).amount, 0)
                when c.pay_type = 'fixed' then c.rate
                else round(greatest(0, c.logged + c.adjustment) * c.rate, 2) end::numeric(12,2) as base_pay
    from calc c
  )
  select b.user_id, b.full_name, b.role, b.pay_type, b.rate, b.no_salary, b.personal, b.logged, b.adjustment, b.payable, b.base_pay,
         b.bonuses, b.deductions, (b.base_pay + b.bonuses)::numeric(12,2), (b.base_pay + b.bonuses - b.deductions)::numeric(12,2),
         b.pending, b.flagged
  from based b
$$;

-- Salaries ---------------------------------------------------------------------------------------------------------------
drop function public.set_staff_salary(uuid, numeric);
create function public.set_staff_salary(p_user uuid, p_amount numeric, p_pay_type text default null) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_old public.staff_salaries;
  v_role text := public.payroll_role(p_user);
  v_type text;
  v_amount numeric(12,2);
begin
  perform public.require_permission('reports:revenue');
  if p_user is null or p_amount is null or p_amount <= 0 or p_amount > 10000000
     or (p_pay_type is not null and p_pay_type not in ('fixed', 'hourly', 'per_session')) then
    perform public.fail('bad_input', '22023');
  end if;
  if p_user = auth.uid() then perform public.fail('own_salary', '42501'); end if;
  if v_role is null or not exists (select 1 from public.profiles p where p.id = p_user and p.is_active and p.deleted_at is null) then
    perform public.fail('bad_input', '22023');
  end if;
  select * into v_old from public.staff_salaries where user_id = p_user;
  -- the released app (before 0142) sends no type: keep the current one
  v_type := coalesce(p_pay_type, v_old.pay_type, (select rs.pay_type from public.role_salaries rs where rs.role = v_role), 'fixed');
  if v_type = 'per_session' and v_role <> 'coach' then perform public.fail('per_session_coach_only', '22023'); end if;
  v_amount := round(p_amount, 2);
  insert into public.staff_salaries(user_id, amount, pay_type, updated_at, updated_by)
  values (p_user, v_amount, v_type, now(), auth.uid())
  on conflict (user_id) do update set amount = excluded.amount, pay_type = excluded.pay_type,
                                      updated_at = excluded.updated_at, updated_by = excluded.updated_by;
  perform public.audit('payroll.salary_set', 'staff_salaries', p_user::text,
    jsonb_build_object('old', v_old.amount, 'new', v_amount, 'old_type', v_old.pay_type, 'new_type', v_type));
end $$;

create function public.use_role_salary(p_user uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_old public.staff_salaries;
  v_role text := public.payroll_role(p_user);
  v_def public.role_salaries;
begin
  perform public.require_permission('reports:revenue');
  if p_user is null or v_role is null or v_role not in ('employee', 'coach') then perform public.fail('bad_input', '22023'); end if;
  if p_user = auth.uid() then perform public.fail('own_salary', '42501'); end if;
  delete from public.staff_salaries where user_id = p_user returning * into v_old;
  select * into v_def from public.role_salaries where role = v_role;
  perform public.audit('payroll.salary_set', 'staff_salaries', p_user::text,
    jsonb_build_object('old', v_old.amount, 'new', v_def.amount, 'old_type', v_old.pay_type, 'new_type', v_def.pay_type, 'follows_role', true));
end $$;

drop function public.set_role_salary(text, numeric);
create function public.set_role_salary(p_role text, p_amount numeric, p_pay_type text default null) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_old public.role_salaries;
  v_type text;
  v_amount numeric(12,2);
begin
  perform public.require_permission('reports:revenue');
  if p_role is null or p_role not in ('employee', 'coach') or p_amount is null or p_amount <= 0 or p_amount > 10000000
     or (p_pay_type is not null and p_pay_type not in ('fixed', 'hourly', 'per_session')) then
    perform public.fail('bad_input', '22023');
  end if;
  select * into v_old from public.role_salaries where role = p_role;
  v_type := coalesce(p_pay_type, v_old.pay_type, 'fixed');
  if v_type = 'per_session' and p_role <> 'coach' then perform public.fail('per_session_coach_only', '22023'); end if;
  v_amount := round(p_amount, 2);
  -- H14: only the default changes; people with their own rate keep it, everyone else follows the role
  insert into public.role_salaries(role, amount, pay_type, updated_at, updated_by)
  values (p_role, v_amount, v_type, now(), auth.uid())
  on conflict (role) do update set amount = excluded.amount, pay_type = excluded.pay_type,
                                   updated_at = excluded.updated_at, updated_by = excluded.updated_by;
  perform public.audit('payroll.role_salary_set', 'role_salaries', p_role,
    jsonb_build_object('old', v_old.amount, 'new', v_amount, 'old_type', v_old.pay_type, 'new_type', v_type));
end $$;

drop function public.payroll_role_salaries();
create function public.payroll_role_salaries()
returns table (role text, amount numeric, pay_type text, updated_at timestamptz, updated_by_name text)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  return query
  select r.role, rs.amount, coalesce(rs.pay_type, 'fixed'), rs.updated_at,
         coalesce(nullif(btrim(ub.full_name), ''), ub.username)
  from (values ('employee'::text), ('coach'::text)) as r(role)
  left join public.role_salaries rs on rs.role = r.role
  left join public.profiles ub on ub.id = rs.updated_by
  order by case r.role when 'employee' then 1 else 2 end;
end $$;

drop function public.payroll_staff();
create function public.payroll_staff()
returns table (user_id uuid, full_name text, role text, amount numeric, pay_type text, personal boolean,
               updated_at timestamptz, updated_by_name text)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  return query
  with ranked as (
    select p.id, coalesce(nullif(btrim(p.full_name), ''), p.username) as full_name, public.payroll_role(p.id) as role,
           ss.amount, ss.pay_type, ss.updated_at, coalesce(nullif(btrim(ub.full_name), ''), ub.username) as updated_by_name
    from public.profiles p
    left join public.staff_salaries ss on ss.user_id = p.id
    left join public.profiles ub on ub.id = ss.updated_by
    where p.is_active and p.deleted_at is null
  )
  select r.id, r.full_name, r.role, coalesce(r.amount, rs.amount), coalesce(r.pay_type, rs.pay_type, 'fixed'), r.amount is not null,
         coalesce(r.updated_at, rs.updated_at), coalesce(r.updated_by_name, coalesce(nullif(btrim(rub.full_name), ''), rub.username))
  from ranked r
  left join public.role_salaries rs on rs.role = r.role
  left join public.profiles rub on rub.id = rs.updated_by
  where r.role in ('employee', 'coach') or (r.role in ('subadmin', 'admin') and r.amount is not null)
  order by case r.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, r.full_name;
end $$;

-- Pay period -------------------------------------------------------------------------------------------------------------
create or replace function public.payroll_period(p_starts_on date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_per public.payroll_periods;
  v_paid_by_name text;
  v_rows jsonb;
  v_net numeric(12,2);
  v_gross numeric(12,2);
begin
  perform public.require_permission('reports:revenue');
  insert into public.payroll_periods(starts_on, ends_on) values (p_starts_on, public.payroll_period_end(p_starts_on))
  on conflict (starts_on) do nothing;
  select * into v_per from public.payroll_periods where starts_on = p_starts_on;
  if v_per.paid_by is not null then
    select coalesce(nullif(btrim(full_name), ''), username) into v_paid_by_name from public.profiles where id = v_per.paid_by;
  end if;

  select coalesce(jsonb_agg(jsonb_build_object(
           'user_id', b.user_id, 'full_name', b.full_name, 'role', b.role,
           -- reference numbers shown since Part 3 (closed shifts, completed sessions), unchanged
           'sessions_count', case when b.role = 'coach' then (
               select count(*)::int from public.training_sessions ts
               where ts.coach_id = b.user_id and ts.status = 'completed'
                 and (ts.scheduled_start at time zone 'Asia/Manila')::date between v_per.starts_on and v_per.ends_on
                 and (ts.held_until is null or ts.held_until > now())) else 0 end,
           'hours', case
               when b.role = 'coach' then coalesce((
                 select sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0)::numeric(10,2)
                 from public.training_sessions ts
                 where ts.coach_id = b.user_id and ts.status = 'completed'
                   and (ts.scheduled_start at time zone 'Asia/Manila')::date between v_per.starts_on and v_per.ends_on
                   and (ts.held_until is null or ts.held_until > now())), 0)
               when b.role = 'employee' then coalesce((
                 select sum(extract(epoch from (cs.closed_at - cs.opened_at)) / 3600.0)::numeric(10,2)
                 from public.cash_shifts cs
                 where cs.cashier_id = b.user_id and cs.closed_at is not null
                   and (cs.opened_at at time zone 'Asia/Manila')::date between v_per.starts_on and v_per.ends_on), 0)
               else 0 end,
           'pay_type', b.pay_type, 'rate', b.rate, 'personal', b.personal,
           'logged', b.logged, 'adjustment', b.adjustment, 'payable', b.payable,
           'salary', b.base_pay, 'base_pay', b.base_pay, 'bonuses', b.bonuses, 'gross', b.gross,
           'deductions', b.deductions, 'net', b.net, 'no_salary', b.no_salary, 'pending', b.pending, 'flagged', b.flagged)
         order by case b.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, b.full_name), '[]'::jsonb),
         coalesce(sum(b.net), 0), coalesce(sum(b.gross), 0)
  into v_rows, v_net, v_gross
  from public.payroll_basis(v_per.period_id) b;

  return jsonb_build_object('period_id', v_per.period_id, 'starts_on', v_per.starts_on, 'ends_on', v_per.ends_on,
    'paid_at', v_per.paid_at, 'paid_by_name', v_paid_by_name, 'total_net', v_net, 'total_gross', v_gross, 'rows', v_rows);
end $$;

-- Drawer: the person's shifts / visits / sessions and corrections
create function public.payroll_records_for(p_period bigint, p_user uuid)
returns table (source text, source_id bigint, starts_at timestamptz, ends_at timestamptz, hours numeric, label text,
               status text, reason text, flags text[])
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null then perform public.fail('bad_input', '22023'); end if;
  return query
  select r.source, r.source_id, r.starts_at, r.ends_at, r.hours, r.label, r.status, r.reason, r.flags
  from public.payroll_records(p_period) r where r.user_id = p_user
  order by r.starts_at, r.source_id;
end $$;

create function public.payroll_adjustments_for(p_period bigint, p_user uuid)
returns table (adjustment_id bigint, quantity numeric, note text, created_by_name text, created_at timestamptz)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null then perform public.fail('bad_input', '22023'); end if;
  return query
  select a.adjustment_id, a.quantity, a.note, coalesce(nullif(btrim(pr.full_name), ''), pr.username), a.created_at
  from public.payroll_hour_adjustments a join public.profiles pr on pr.id = a.created_by
  where a.period_id = p_period and a.user_id = p_user
  order by a.created_at, a.adjustment_id;
end $$;

-- The period row, locked, and refused once paid
create function public.payroll_draft(p_period bigint) returns public.payroll_periods
language plpgsql set search_path = '' as $$
declare v_per public.payroll_periods;
begin
  select * into v_per from public.payroll_periods where period_id = p_period for update;
  if not found then perform public.fail('bad_input', '22023'); end if;
  if v_per.paid_at is not null then perform public.fail('payroll_paid', 'P0002'); end if;
  return v_per;
end $$;

-- Approve / reject / reopen (H12)
create function public.payroll_review_time(p_period bigint, p_source text, p_source_id bigint, p_status text, p_reason text default null)
returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_rec record;
  v_reason text := nullif(btrim(coalesce(p_reason, '')), '');
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_source is null or p_source_id is null or p_status not in ('approved', 'rejected', 'pending')
     or (p_status = 'rejected' and (v_reason is null or length(v_reason) > 200)) then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.payroll_draft(p_period);
  select * into v_rec from public.payroll_records(p_period) r where r.source = p_source and r.source_id = p_source_id;
  if not found then perform public.fail('not_found', 'P0002'); end if;
  if v_rec.ends_at is null then perform public.fail('record_open', '22023'); end if;
  if p_status = 'pending' then
    delete from public.payroll_time_reviews where source = p_source and source_id = p_source_id;
  else
    insert into public.payroll_time_reviews(source, source_id, status, reason, reviewed_by, reviewed_at)
    values (p_source, p_source_id, p_status, case when p_status = 'rejected' then v_reason end, auth.uid(), now())
    on conflict (source, source_id) do update set status = excluded.status, reason = excluded.reason,
                                                  reviewed_by = excluded.reviewed_by, reviewed_at = excluded.reviewed_at;
  end if;
  perform public.audit(case p_status when 'approved' then 'payroll.time_approved' when 'rejected' then 'payroll.time_rejected'
                                     else 'payroll.time_reopened' end,
    'payroll_time_reviews', p_source || ':' || p_source_id,
    jsonb_build_object('period_id', p_period, 'user_id', v_rec.user_id, 'label', v_rec.label, 'old', v_rec.status,
                       'new', p_status, 'reason', v_reason));
end $$;

create function public.payroll_approve_all(p_period bigint, p_user uuid default null) returns int
language plpgsql security definer set search_path = '' as $$
declare v_count int;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null then perform public.fail('bad_input', '22023'); end if;
  perform public.payroll_draft(p_period);
  insert into public.payroll_time_reviews(source, source_id, status, reviewed_by, reviewed_at)
  select r.source, r.source_id, 'approved', auth.uid(), now()
  from public.payroll_records(p_period) r
  where (p_user is null or r.user_id = p_user) and r.status = 'pending' and r.ends_at is not null and cardinality(r.flags) = 0;
  get diagnostics v_count = row_count;
  if v_count > 0 then
    perform public.audit('payroll.time_approved', 'payroll_time_reviews', 'all:' || p_period,
      jsonb_build_object('period_id', p_period, 'user_id', p_user, 'count', v_count, 'all', true));
  end if;
  return v_count;
end $$;

-- Hour (or session) corrections (H5)
create function public.payroll_add_hours(p_period bigint, p_user uuid, p_quantity numeric, p_note text)
returns public.payroll_hour_adjustments
language plpgsql security definer set search_path = '' as $$
declare
  v_note text := nullif(btrim(coalesce(p_note, '')), '');
  v_row public.payroll_hour_adjustments;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null or p_quantity is null or round(p_quantity, 2) = 0 or abs(p_quantity) > 1000
     or v_note is null or length(v_note) > 200 then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.payroll_draft(p_period);
  if not exists (select 1 from public.payroll_pay(p_period) pay where pay.user_id = p_user and pay.pay_type in ('hourly', 'per_session')) then
    perform public.fail('bad_input', '22023');
  end if;
  insert into public.payroll_hour_adjustments(period_id, user_id, quantity, note, created_by)
  values (p_period, p_user, round(p_quantity, 2), v_note, auth.uid()) returning * into v_row;
  perform public.audit('payroll.hours_adjusted', 'payroll_hour_adjustments', v_row.adjustment_id::text,
    jsonb_build_object('period_id', p_period, 'user_id', p_user, 'quantity', v_row.quantity, 'note', v_note));
  return v_row;
end $$;

create function public.payroll_remove_hours(p_adjustment bigint) returns void
language plpgsql security definer set search_path = '' as $$
declare v_row public.payroll_hour_adjustments;
begin
  perform public.require_permission('reports:revenue');
  if p_adjustment is null then perform public.fail('bad_input', '22023'); end if;
  select * into v_row from public.payroll_hour_adjustments where adjustment_id = p_adjustment;
  if not found then perform public.fail('not_found', 'P0002'); end if;
  perform public.payroll_draft(v_row.period_id);
  delete from public.payroll_hour_adjustments where adjustment_id = p_adjustment;
  perform public.audit('payroll.hours_adjustment_removed', 'payroll_hour_adjustments', p_adjustment::text,
    jsonb_build_object('period_id', v_row.period_id, 'user_id', v_row.user_id, 'quantity', v_row.quantity, 'note', v_row.note));
end $$;

-- Bonus / deduction lines with a category (H16)
drop function public.payroll_add_line(bigint, uuid, text, numeric, text);
create function public.payroll_add_line(p_period bigint, p_user uuid, p_kind text, p_amount numeric, p_note text,
                                        p_category text default 'other')   -- default: the released app (before 0142) sends none
returns public.payroll_lines
language plpgsql security definer set search_path = '' as $$
declare
  v_note text := nullif(btrim(coalesce(p_note, '')), '');
  v_amount numeric(12,2);
  v_row public.payroll_lines;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null or p_kind not in ('bonus', 'deduction')
     or p_amount is null or p_amount <= 0 or p_amount > 10000000
     or v_note is null or length(v_note) > 200
     or p_category is null or p_category not in ('regular_bonus', 'overtime', 'holiday', 'other') then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.payroll_draft(p_period);
  if public.payroll_role(p_user) is null
     or not exists (select 1 from public.profiles p where p.id = p_user and p.is_active and p.deleted_at is null) then
    perform public.fail('bad_input', '22023');
  end if;
  v_amount := round(p_amount, 2);
  insert into public.payroll_lines(period_id, user_id, kind, amount, note, category, created_by)
  values (p_period, p_user, p_kind, v_amount, v_note, p_category, auth.uid()) returning * into v_row;
  perform public.audit('payroll.line_added', 'payroll_lines', v_row.line_id::text,
    jsonb_build_object('period_id', p_period, 'user_id', p_user, 'kind', p_kind, 'amount', v_amount, 'note', v_note, 'category', p_category));
  return v_row;
end $$;

drop function public.payroll_lines_for(bigint, uuid);
create function public.payroll_lines_for(p_period bigint, p_user uuid)
returns table (line_id bigint, kind text, amount numeric, note text, category text, pay_type text, quantity numeric, rate numeric,
               created_by_name text, created_at timestamptz)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null then perform public.fail('bad_input', '22023'); end if;
  return query
  select pl.line_id, pl.kind, pl.amount, pl.note, pl.category, pl.pay_type, pl.quantity, pl.rate,
         coalesce(nullif(btrim(pr.full_name), ''), pr.username), pl.created_at
  from public.payroll_lines pl join public.profiles pr on pr.id = pl.created_by
  where pl.period_id = p_period and pl.user_id = p_user
  order by pl.created_at, pl.line_id;
end $$;

-- Mark as paid (H7, H11, H12) ---------------------------------------------------------------------------------------------
create or replace function public.payroll_mark_paid(p_period bigint) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_per public.payroll_periods;
  v_name text;
  v_count int;
  v_total numeric(12,2);
begin
  perform public.require_permission('reports:revenue');
  if p_period is null then perform public.fail('bad_input', '22023'); end if;
  v_per := public.payroll_draft(p_period);
  if (now() at time zone 'Asia/Manila')::date < v_per.ends_on then perform public.fail('period_not_ended', '22023'); end if;
  -- hourly / per-session pay: a shift or session later on the last day would belong to a locked period and never be paid,
  -- so such periods are paid from the next day (fixed-only periods keep Part 3's "on the last day")
  if (now() at time zone 'Asia/Manila')::date = v_per.ends_on
     and exists (select 1 from public.payroll_basis(p_period) b where b.pay_type <> 'fixed' and not b.no_salary) then
    perform public.fail('period_not_ended', '22023');
  end if;

  select b.full_name into v_name from public.payroll_basis(p_period) b where b.pending > 0 order by b.full_name limit 1;
  if v_name is not null then perform public.fail('time_pending:' || v_name, '22023'); end if;
  select b.full_name into v_name from public.payroll_basis(p_period) b where b.net < 0 order by b.full_name limit 1;
  if v_name is not null then perform public.fail('net_below_zero:' || v_name, '22023'); end if;

  insert into public.payroll_lines(period_id, user_id, kind, amount, pay_type, quantity, rate, created_by)
  select p_period, m.user_id, 'salary', m.base_pay, m.pay_type, m.payable, m.rate, auth.uid()
  from public.payroll_basis(p_period) m
  where not m.no_salary and (m.base_pay > 0 or m.pay_type <> 'fixed');

  update public.payroll_periods set paid_at = now(), paid_by = auth.uid() where period_id = p_period;
  select count(distinct user_id)::int, coalesce(sum(case when kind = 'deduction' then -amount else amount end), 0)
  into v_count, v_total from public.payroll_lines where period_id = p_period;
  perform public.audit('payroll.paid', 'payroll_periods', p_period::text,
    jsonb_build_object('starts_on', v_per.starts_on, 'ends_on', v_per.ends_on, 'people', v_count, 'total', v_total));
  return public.payroll_period(v_per.starts_on);
end $$;

-- Logs & Archive: readable names on the new payroll rows (extends 0138)
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.audit_records(date,date,text,text,text,integer,integer,boolean,text,text,text,text)'::regprocedure);
  if position('payroll_time_reviews' in v_def) = 0 then
    v_def := replace(v_def, $q$a.entity = 'payroll_lines' and pl_u.id$q$,
      $q$a.entity in ('payroll_lines', 'payroll_hour_adjustments', 'payroll_time_reviews') and pl_u.id$q$);
    execute v_def;
  end if;
end $$;

-- Grants: private helpers callable by nobody; payroll functions by signed-in users, gated inside by reports:revenue
revoke all on function
  public.payroll_role(uuid), public.payroll_pay(bigint), public.payroll_records(bigint), public.payroll_basis(bigint),
  public.payroll_draft(bigint)
  from public, anon, authenticated;
revoke all on function
  public.set_staff_salary(uuid, numeric, text), public.use_role_salary(uuid), public.set_role_salary(text, numeric, text),
  public.payroll_role_salaries(), public.payroll_staff(), public.payroll_period(date),
  public.payroll_records_for(bigint, uuid), public.payroll_adjustments_for(bigint, uuid),
  public.payroll_review_time(bigint, text, bigint, text, text), public.payroll_approve_all(bigint, uuid),
  public.payroll_add_hours(bigint, uuid, numeric, text), public.payroll_remove_hours(bigint),
  public.payroll_add_line(bigint, uuid, text, numeric, text, text), public.payroll_lines_for(bigint, uuid),
  public.payroll_mark_paid(bigint)
  from public, anon, authenticated;
grant execute on function
  public.set_staff_salary(uuid, numeric, text), public.use_role_salary(uuid), public.set_role_salary(text, numeric, text),
  public.payroll_role_salaries(), public.payroll_staff(), public.payroll_period(date),
  public.payroll_records_for(bigint, uuid), public.payroll_adjustments_for(bigint, uuid),
  public.payroll_review_time(bigint, text, bigint, text, text), public.payroll_approve_all(bigint, uuid),
  public.payroll_add_hours(bigint, uuid, numeric, text), public.payroll_remove_hours(bigint),
  public.payroll_add_line(bigint, uuid, text, numeric, text, text), public.payroll_lines_for(bigint, uuid),
  public.payroll_mark_paid(bigint)
  to authenticated;
