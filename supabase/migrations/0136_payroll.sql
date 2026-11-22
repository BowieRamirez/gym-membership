-- Phase 17 (P1-P10, 2026-11-22): Payroll tables and functions (Admin and System admin only).

create table public.staff_salaries (
  user_id uuid primary key references public.profiles(id),
  amount numeric(12,2) not null check (amount > 0),
  updated_at timestamptz not null default now(),
  updated_by uuid not null references public.profiles(id)
);
alter table public.staff_salaries enable row level security;
comment on table public.staff_salaries is 'Fixed salary per pay period for staff (0136)';

create table public.payroll_periods (
  period_id bigint generated always as identity primary key,
  starts_on date not null unique check (extract(day from starts_on) in (1, 16)),
  ends_on date not null check (ends_on >= starts_on),
  paid_at timestamptz,
  paid_by uuid references public.profiles(id)
);
alter table public.payroll_periods enable row level security;
comment on table public.payroll_periods is 'Twice-monthly pay periods (1st-15th, 16th-end, Manila dates) (0136)';

create table public.payroll_lines (
  line_id bigint generated always as identity primary key,
  period_id bigint not null references public.payroll_periods(period_id),
  user_id uuid not null references public.profiles(id),
  kind text not null check (kind in ('salary', 'bonus', 'deduction')),
  amount numeric(12,2) not null check (amount > 0),
  note text,
  created_by uuid not null references public.profiles(id),
  created_at timestamptz not null default now(),
  check (kind = 'salary' or (note is not null and length(btrim(note)) between 1 and 200))
);
create index payroll_lines_period_user on public.payroll_lines(period_id, user_id, created_at, line_id);
alter table public.payroll_lines enable row level security;
comment on table public.payroll_lines is 'Frozen salary lines and manual bonus/deduction lines per period (0136)';

create function public.payroll_period_end(p_starts_on date) returns date
language plpgsql immutable
set search_path = ''
as $$
begin
  if p_starts_on is null or extract(day from p_starts_on) not in (1, 16) then
    perform public.fail('bad_input', '22023');
  end if;
  if extract(day from p_starts_on) = 1 then
    return p_starts_on + 14;
  end if;
  return (date_trunc('month', p_starts_on)::date + interval '1 month' - interval '1 day')::date;
end $$;

create function public.set_staff_salary(p_user uuid, p_amount numeric) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
  v_amount numeric(12,2);
  v_old numeric(12,2);
  v_ok boolean;
begin
  perform public.require_permission('reports:revenue');
  if p_user is null or p_amount is null or p_amount <= 0 or p_amount > 10000000 then
    perform public.fail('bad_input', '22023');
  end if;
  if p_user = auth.uid() then
    perform public.fail('own_salary', '42501');
  end if;
  select exists (
    select 1 from public.profiles p
    join public.user_roles ur on ur.user_id = p.id
    join public.roles r on r.role_id = ur.role_id
    where p.id = p_user and p.is_active and p.deleted_at is null
      and r.name in ('employee', 'coach', 'subadmin', 'admin')
  ) into v_ok;
  if not v_ok then
    perform public.fail('bad_input', '22023');
  end if;

  v_amount := round(p_amount, 2);
  select amount into v_old from public.staff_salaries where user_id = p_user;

  insert into public.staff_salaries(user_id, amount, updated_at, updated_by)
  values (p_user, v_amount, now(), auth.uid())
  on conflict (user_id) do update
    set amount = excluded.amount,
        updated_at = excluded.updated_at,
        updated_by = excluded.updated_by;

  perform public.audit('payroll.salary_set', 'staff_salaries', p_user::text,
    jsonb_build_object('old', v_old, 'new', v_amount));
end $$;

create function public.payroll_staff()
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
           ss.amount,
           ss.updated_at,
           coalesce(nullif(btrim(ub.full_name), ''), ub.username) as updated_by_name
    from public.profiles p
    left join public.staff_salaries ss on ss.user_id = p.id
    left join public.profiles ub on ub.id = ss.updated_by
    where p.is_active and p.deleted_at is null
  )
  select r.user_id, r.full_name, r.role, r.amount, r.updated_at, r.updated_by_name
  from ranked r
  where r.role in ('employee', 'coach')
     or (r.role in ('subadmin', 'admin') and r.amount is not null)
  order by case r.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, r.full_name;
end $$;

create function public.payroll_period(p_starts_on date) returns jsonb
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
           case when v_per.paid_at is null then coalesce((select ss.amount from public.staff_salaries ss where ss.user_id = r.user_id), 0)
                else coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'salary'), 0)
           end::numeric(12,2) as salary,
           case when v_per.paid_at is null then not exists (select 1 from public.staff_salaries ss where ss.user_id = r.user_id)
                else not exists (select 1 from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'salary')
           end as no_salary,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'bonus'), 0)::numeric(12,2) as bonuses,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = v_per.period_id and pl.user_id = r.user_id and pl.kind = 'deduction'), 0)::numeric(12,2) as deductions
    from roster r
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

create function public.payroll_lines_for(p_period bigint, p_user uuid)
returns table (
  line_id bigint,
  kind text,
  amount numeric,
  note text,
  created_by_name text,
  created_at timestamptz
)
language plpgsql stable security definer
set search_path = ''
as $$
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null then
    perform public.fail('bad_input', '22023');
  end if;
  return query
  select pl.line_id, pl.kind, pl.amount, pl.note,
         coalesce(nullif(btrim(pr.full_name), ''), pr.username) as created_by_name,
         pl.created_at
  from public.payroll_lines pl
  join public.profiles pr on pr.id = pl.created_by
  where pl.period_id = p_period and pl.user_id = p_user
  order by pl.created_at, pl.line_id;
end $$;

create function public.payroll_add_line(p_period bigint, p_user uuid, p_kind text, p_amount numeric, p_note text)
returns public.payroll_lines
language plpgsql security definer
set search_path = ''
as $$
declare
  v_per public.payroll_periods;
  v_note text := nullif(btrim(coalesce(p_note, '')), '');
  v_amount numeric(12,2);
  v_ok boolean;
  v_row public.payroll_lines;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or p_user is null or p_kind not in ('bonus', 'deduction')
     or p_amount is null or p_amount <= 0 or p_amount > 10000000
     or v_note is null or length(v_note) > 200 then
    perform public.fail('bad_input', '22023');
  end if;

  select * into v_per from public.payroll_periods where period_id = p_period for update;
  if not found then perform public.fail('bad_input', '22023'); end if;
  if v_per.paid_at is not null then perform public.fail('payroll_paid', 'P0002'); end if;

  select exists (
    select 1 from public.profiles p
    join public.user_roles ur on ur.user_id = p.id
    join public.roles r on r.role_id = ur.role_id
    where p.id = p_user and p.is_active and p.deleted_at is null
      and r.name in ('employee', 'coach', 'subadmin', 'admin')
  ) into v_ok;
  if not v_ok then perform public.fail('bad_input', '22023'); end if;

  v_amount := round(p_amount, 2);
  insert into public.payroll_lines(period_id, user_id, kind, amount, note, created_by)
  values (p_period, p_user, p_kind, v_amount, v_note, auth.uid())
  returning * into v_row;

  perform public.audit('payroll.line_added', 'payroll_lines', v_row.line_id::text,
    jsonb_build_object('period_id', p_period, 'user_id', p_user, 'kind', p_kind, 'amount', v_amount, 'note', v_note));
  return v_row;
end $$;

create function public.payroll_remove_line(p_line bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
  v_line public.payroll_lines;
  v_per public.payroll_periods;
begin
  perform public.require_permission('reports:revenue');
  if p_line is null then perform public.fail('bad_input', '22023'); end if;

  select * into v_line from public.payroll_lines where line_id = p_line;
  if not found then perform public.fail('not_found', 'P0002'); end if;
  if v_line.kind = 'salary' then perform public.fail('bad_input', '22023'); end if;

  select * into v_per from public.payroll_periods where period_id = v_line.period_id for update;
  if v_per.paid_at is not null then perform public.fail('payroll_paid', 'P0002'); end if;

  delete from public.payroll_lines where line_id = p_line;

  perform public.audit('payroll.line_removed', 'payroll_lines', p_line::text,
    jsonb_build_object('period_id', v_line.period_id, 'user_id', v_line.user_id, 'kind', v_line.kind, 'amount', v_line.amount));
end $$;

create function public.payroll_mark_paid(p_period bigint) returns jsonb
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
           coalesce((select ss.amount from public.staff_salaries ss where ss.user_id = p.id), 0) as salary,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = p.id and pl.kind = 'bonus'), 0) as bonuses,
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = p.id and pl.kind = 'deduction'), 0) as deductions
    from public.profiles p
    where p.is_active and p.deleted_at is null and (
      exists (
        select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id
        where ur.user_id = p.id and r.name in ('employee', 'coach')
      )
      or exists (select 1 from public.staff_salaries ss where ss.user_id = p.id)
      or exists (select 1 from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = p.id)
    )
  )
  select r.full_name into v_bad_name
  from roster r
  where (r.salary + r.bonuses - r.deductions) < 0
  order by r.full_name
  limit 1;

  if v_bad_name is not null then
    perform public.fail('net_below_zero:' || v_bad_name, '22023');
  end if;

  -- Freeze current salaries for everyone on the active roster who has a salary set.
  insert into public.payroll_lines(period_id, user_id, kind, amount, note, created_by)
  select p_period, p.id, 'salary', ss.amount, null, auth.uid()
  from public.profiles p
  join public.staff_salaries ss on ss.user_id = p.id
  where p.is_active and p.deleted_at is null and (
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

revoke all on function public.payroll_period_end(date) from public, anon, authenticated;
revoke all on function
  public.set_staff_salary(uuid, numeric),
  public.payroll_staff(),
  public.payroll_period(date),
  public.payroll_lines_for(bigint, uuid),
  public.payroll_add_line(bigint, uuid, text, numeric, text),
  public.payroll_remove_line(bigint),
  public.payroll_mark_paid(bigint)
  from public, anon, authenticated;
grant execute on function
  public.set_staff_salary(uuid, numeric),
  public.payroll_staff(),
  public.payroll_period(date),
  public.payroll_lines_for(bigint, uuid),
  public.payroll_add_line(bigint, uuid, text, numeric, text),
  public.payroll_remove_line(bigint),
  public.payroll_mark_paid(bigint)
  to authenticated;
