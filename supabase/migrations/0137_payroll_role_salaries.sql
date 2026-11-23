-- Phase 19 follow-up (2026-11-22): Role-level fixed salaries for Front desk (employee) and Coach.

create table public.role_salaries (
  role text primary key check (role in ('employee', 'coach')),
  amount numeric(12,2) not null check (amount > 0),
  updated_at timestamptz not null default now(),
  updated_by uuid not null references public.profiles(id)
);
alter table public.role_salaries enable row level security;
comment on table public.role_salaries is 'Default fixed salary per pay period by role (employee or coach) (0137)';

create function public.set_role_salary(p_role text, p_amount numeric) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
  v_amount numeric(12,2);
  v_old numeric(12,2);
begin
  perform public.require_permission('reports:revenue');
  if p_role is null or p_role not in ('employee', 'coach')
     or p_amount is null or p_amount <= 0 or p_amount > 10000000 then
    perform public.fail('bad_input', '22023');
  end if;

  v_amount := round(p_amount, 2);
  select amount into v_old from public.role_salaries where role = p_role;

  insert into public.role_salaries(role, amount, updated_at, updated_by)
  values (p_role, v_amount, now(), auth.uid())
  on conflict (role) do update
    set amount = excluded.amount,
        updated_at = excluded.updated_at,
        updated_by = excluded.updated_by;

  -- Apply to all active, non-archived staff whose primary payroll role matches p_role.
  insert into public.staff_salaries(user_id, amount, updated_at, updated_by)
  select p.id, v_amount, now(), auth.uid()
  from public.profiles p
  where p.is_active and p.deleted_at is null and p.id <> auth.uid()
    and (
      select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
      where ur.user_id = p.id and r.name in ('employee', 'coach', 'subadmin', 'admin')
      order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end
      limit 1
    ) = p_role
  on conflict (user_id) do update
    set amount = excluded.amount,
        updated_at = excluded.updated_at,
        updated_by = excluded.updated_by;

  perform public.audit('payroll.role_salary_set', 'role_salaries', p_role,
    jsonb_build_object('old', v_old, 'new', v_amount));
end $$;

create function public.payroll_role_salaries()
returns table (
  role text,
  amount numeric,
  updated_at timestamptz,
  updated_by_name text
)
language plpgsql stable security definer
set search_path = ''
as $$
begin
  perform public.require_permission('reports:revenue');
  return query
  select r.role,
         rs.amount,
         rs.updated_at,
         coalesce(nullif(btrim(ub.full_name), ''), ub.username) as updated_by_name
  from (values ('employee'::text), ('coach'::text)) as r(role)
  left join public.role_salaries rs on rs.role = r.role
  left join public.profiles ub on ub.id = rs.updated_by
  order by case r.role when 'employee' then 1 else 2 end;
end $$;

create or replace function public.payroll_staff()
returns table (
  user_id uuid,
  full_name text,
  role text,
  amount numeric,
  updated_at timestamptz,
  updated_by_name text
)
language plpgsql stable security definer
set search_path = ''
as $$
begin
  perform public.require_permission('reports:revenue');
  return query
  with ranked as (
    select p.id as user_id,
           coalesce(nullif(btrim(p.full_name), ''), p.username) as full_name,
           (select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
            where ur.user_id = p.id and r.name in ('employee', 'coach', 'subadmin', 'admin')
            order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end
            limit 1) as role,
           ss.amount as user_amount,
           ss.updated_at as user_updated_at,
           coalesce(nullif(btrim(ub.full_name), ''), ub.username) as user_updated_by
    from public.profiles p
    left join public.staff_salaries ss on ss.user_id = p.id
    left join public.profiles ub on ub.id = ss.updated_by
    where p.is_active and p.deleted_at is null
  )
  select r.user_id,
         r.full_name,
         r.role,
         coalesce(r.user_amount, rs.amount) as amount,
         coalesce(r.user_updated_at, rs.updated_at) as updated_at,
         coalesce(r.user_updated_by, coalesce(nullif(btrim(rub.full_name), ''), rub.username)) as updated_by_name
  from ranked r
  left join public.role_salaries rs on rs.role = r.role
  left join public.profiles rub on rub.id = rs.updated_by
  where r.role in ('employee', 'coach')
     or (r.role in ('subadmin', 'admin') and r.user_amount is not null)
  order by case r.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, r.full_name;
end $$;

create or replace function public.payroll_period(p_starts_on date) returns jsonb
language plpgsql security definer
set search_path = ''
as $$
declare
  v_ends_on date := public.payroll_period_end(p_starts_on);
  v_per public.payroll_periods;
  v_paid_by_name text;
  v_rows jsonb;
  v_total numeric(12,2);
begin
  perform public.require_permission('reports:revenue');

  insert into public.payroll_periods(starts_on, ends_on)
  values (p_starts_on, v_ends_on)
  on conflict (starts_on) do nothing;

  select * into v_per from public.payroll_periods where starts_on = p_starts_on;

  if v_per.paid_by is not null then
    select coalesce(nullif(btrim(full_name), ''), username) into v_paid_by_name
    from public.profiles where id = v_per.paid_by;
  end if;

  with roster as (
    select p.id as user_id,
           coalesce(nullif(btrim(p.full_name), ''), p.username) as full_name,
           coalesce((
             select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
             where ur.user_id = p.id and r.name in ('employee', 'coach', 'subadmin', 'admin')
             order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end
             limit 1
           ), 'employee') as role
    from public.profiles p
    where (
      v_per.paid_at is null and p.is_active and p.deleted_at is null and (
        exists (
          select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id
          where ur.user_id = p.id and r.name in ('employee', 'coach')
        )
        or exists (select 1 from public.staff_salaries ss where ss.user_id = p.id)
        or exists (select 1 from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = p.id)
      )
    ) or (
      v_per.paid_at is not null and exists (
        select 1 from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = p.id
      )
    )
  ),
  computed as (
    select r.user_id,
           r.full_name,
           r.role,
           case when r.role = 'coach' then
             coalesce((
               select count(*)::int from public.training_sessions ts
               where ts.coach_id = r.user_id and ts.status = 'completed'
                 and (ts.scheduled_start at time zone 'Asia/Manila')::date between v_per.starts_on and v_per.ends_on
                 and (ts.held_until is null or ts.held_until > now())
             ), 0)
           else 0 end as sessions_count,
           case
             when r.role = 'coach' then coalesce((
               select sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0)::numeric(10,2)
               from public.training_sessions ts
               where ts.coach_id = r.user_id and ts.status = 'completed'
                 and (ts.scheduled_start at time zone 'Asia/Manila')::date between v_per.starts_on and v_per.ends_on
                 and (ts.held_until is null or ts.held_until > now())
             ), 0)::numeric(10,2)
             when r.role = 'employee' then coalesce((
               select sum(extract(epoch from (cs.closed_at - cs.opened_at)) / 3600.0)::numeric(10,2)
               from public.cash_shifts cs
               where cs.cashier_id = r.user_id and cs.closed_at is not null
                 and (cs.opened_at at time zone 'Asia/Manila')::date between v_per.starts_on and v_per.ends_on
             ), 0)::numeric(10,2)
             else 0::numeric(10,2)
           end as hours,
           case when v_per.paid_at is null then coalesce(ss.amount, rs.amount, 0)
                else coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'salary'), 0)
           end::numeric(12,2) as salary,
           case when v_per.paid_at is null then (ss.amount is null and rs.amount is null)
                else not exists (select 1 from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'salary')
           end as no_salary,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'bonus'), 0)::numeric(12,2) as bonuses,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'deduction'), 0)::numeric(12,2) as deductions
    from roster r
    left join public.staff_salaries ss on ss.user_id = r.user_id
    left join public.role_salaries rs on rs.role = r.role
  ),
  with_net as (
    select c.*, (c.salary + c.bonuses - c.deductions)::numeric(12,2) as net
    from computed c
  )
  select coalesce(jsonb_agg(
           jsonb_build_object(
             'user_id', w.user_id,
             'full_name', w.full_name,
             'role', w.role,
             'sessions_count', w.sessions_count,
             'hours', w.hours,
             'salary', w.salary,
             'bonuses', w.bonuses,
             'deductions', w.deductions,
             'net', w.net,
             'no_salary', w.no_salary
           )
           order by case w.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, w.full_name
         ), '[]'::jsonb),
         coalesce(sum(w.net), 0)::numeric(12,2)
  into v_rows, v_total
  from with_net w;

  return jsonb_build_object(
    'period_id', v_per.period_id,
    'starts_on', v_per.starts_on,
    'ends_on', v_per.ends_on,
    'paid_at', v_per.paid_at,
    'paid_by_name', v_paid_by_name,
    'total_net', v_total,
    'rows', v_rows
  );
end $$;

create or replace function public.payroll_mark_paid(p_period bigint) returns jsonb
language plpgsql security definer
set search_path = ''
as $$
declare
  v_per public.payroll_periods;
  v_bad_name text;
  v_count int := 0;
  v_total numeric(12,2) := 0;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null then perform public.fail('bad_input', '22023'); end if;

  select * into v_per from public.payroll_periods where period_id = p_period for update;
  if not found then perform public.fail('bad_input', '22023'); end if;
  if v_per.paid_at is not null then perform public.fail('payroll_paid', 'P0002'); end if;
  if (now() at time zone 'Asia/Manila')::date < v_per.ends_on then
    perform public.fail('period_not_ended', '22023');
  end if;

  with roster as (
    select p.id as user_id,
           coalesce(nullif(btrim(p.full_name), ''), p.username) as full_name,
           coalesce((
             select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
             where ur.user_id = p.id and r.name in ('employee', 'coach', 'subadmin', 'admin')
             order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end
             limit 1
           ), 'employee') as role
    from public.profiles p
    where p.is_active and p.deleted_at is null and (
      exists (
        select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id
        where ur.user_id = p.id and r.name in ('employee', 'coach')
      )
      or exists (select 1 from public.staff_salaries ss where ss.user_id = p.id)
      or exists (select 1 from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = p.id)
    )
  ),
  calc as (
    select r.user_id,
           r.full_name,
           coalesce(ss.amount, rs.amount, 0) as salary,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = r.user_id and pl.kind = 'bonus'), 0) as bonuses,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = r.user_id and pl.kind = 'deduction'), 0) as deductions
    from roster r
    left join public.staff_salaries ss on ss.user_id = r.user_id
    left join public.role_salaries rs on rs.role = r.role
  )
  select c.full_name into v_bad_name
  from calc c
  where (c.salary + c.bonuses - c.deductions) < 0
  order by c.full_name
  limit 1;

  if v_bad_name is not null then
    perform public.fail('net_below_zero:' || v_bad_name, '22023');
  end if;

  -- Freeze current salaries (including role defaults) for everyone on the active roster who has a salary > 0.
  insert into public.payroll_lines(period_id, user_id, kind, amount, note, created_by)
  select p_period,
         p.id,
         'salary',
         coalesce(ss.amount, rs.amount),
         null,
         auth.uid()
  from public.profiles p
  left join public.staff_salaries ss on ss.user_id = p.id
  left join public.role_salaries rs on rs.role = (
    select r.name from public.user_roles ur join public.roles r on r.role_id = ur.role_id
    where ur.user_id = p.id and r.name in ('employee', 'coach', 'subadmin', 'admin')
    order by case r.name when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end
    limit 1
  )
  where p.is_active and p.deleted_at is null and coalesce(ss.amount, rs.amount, 0) > 0 and (
    exists (
      select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id
      where ur.user_id = p.id and r.name in ('employee', 'coach')
    )
    or exists (select 1 from public.staff_salaries s2 where s2.user_id = p.id)
    or exists (select 1 from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = p.id)
  );

  update public.payroll_periods
     set paid_at = now(),
         paid_by = auth.uid()
   where period_id = p_period;

  select count(distinct user_id)::int,
         coalesce(sum(case when kind = 'deduction' then -amount else amount end), 0)::numeric(12,2)
  into v_count, v_total
  from public.payroll_lines
  where period_id = p_period;

  perform public.audit('payroll.paid', 'payroll_periods', p_period::text,
    jsonb_build_object('starts_on', v_per.starts_on, 'ends_on', v_per.ends_on, 'people', v_count, 'total', v_total));

  return public.payroll_period(v_per.starts_on);
end $$;

revoke all on function
  public.set_role_salary(text, numeric),
  public.payroll_role_salaries(),
  public.payroll_staff(),
  public.payroll_period(date),
  public.payroll_mark_paid(bigint)
  from public, anon, authenticated;
grant execute on function
  public.set_role_salary(text, numeric),
  public.payroll_role_salaries(),
  public.payroll_staff(),
  public.payroll_period(date),
  public.payroll_mark_paid(bigint)
  to authenticated;
