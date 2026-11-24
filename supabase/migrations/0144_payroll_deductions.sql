-- 0144: government deductions on payroll (plan docs/superpowers/plans/2026-11-24-payroll-deductions.md, T1-T12).
-- SSS, PhilHealth and Pag-IBIG employee shares once a month, in the 16th-end pay, on the whole month's pay (T1, T2); BIR withholding
-- tax every pay on that pay minus the contributions taken in it (T3); a switch per person (T4); rates in one table (T5); computed on
-- drafts, frozen as deduction lines on Mark as paid (T6); the 16th-end waits for the 1st-15th (T7); never below zero (T8).
-- Rates: 2026 published tables (SSS RA 11199 schedule, PhilHealth 5% / 10k-100k, Pag-IBIG 2% on up to 10k, BIR RR 11-2018 Annex E).

create table public.payroll_deduction_rates (
  id boolean primary key default true check (id),
  sss_rate numeric(6,4) not null default 0.05,
  sss_msc_min numeric(12,2) not null default 5000,
  sss_msc_max numeric(12,2) not null default 35000,
  sss_msc_step numeric(12,2) not null default 500,
  philhealth_rate numeric(6,4) not null default 0.025,
  philhealth_floor numeric(12,2) not null default 10000,
  philhealth_ceiling numeric(12,2) not null default 100000,
  pagibig_low_rate numeric(6,4) not null default 0.01,
  pagibig_rate numeric(6,4) not null default 0.02,
  pagibig_low_limit numeric(12,2) not null default 1500,
  pagibig_cap numeric(12,2) not null default 10000,
  -- semi-monthly withholding: tax = base + rate x (taxable - over), the highest row with over < taxable
  tax_brackets jsonb not null default '[{"over":0,"base":0,"rate":0},{"over":10417,"base":0,"rate":0.15},
    {"over":16667,"base":937.50,"rate":0.20},{"over":33333,"base":4270.70,"rate":0.25},
    {"over":83333,"base":16770.70,"rate":0.30},{"over":333333,"base":91770.70,"rate":0.35}]',
  effective_from date not null default '2026-01-01',
  source text not null default 'SSS 15% (employee 5%) MSC 5,000-35,000; PhilHealth 5% (employee 2.5%) 10,000-100,000; Pag-IBIG 2% (1% up to 1,500) on up to 10,000; BIR semi-monthly table RR 11-2018 Annex E (TRAIN, 2023 on). Confirm with the accountant.'
);
insert into public.payroll_deduction_rates default values;
alter table public.payroll_deduction_rates enable row level security;
comment on table public.payroll_deduction_rates is 'Government deduction rates used by payroll; changed by a migration when the law changes (0144, T5)';

create table public.payroll_no_deductions (
  user_id uuid primary key references public.profiles(id),
  set_by uuid not null references public.profiles(id),
  set_at timestamptz not null default now()
);
alter table public.payroll_no_deductions enable row level security;
comment on table public.payroll_no_deductions is 'People with government deductions switched off (0144, T4); no row = on';

alter table public.payroll_lines drop constraint payroll_lines_category_check;
alter table public.payroll_lines add constraint payroll_lines_category_check
  check (category in ('regular_bonus', 'overtime', 'holiday', 'other', 'sss', 'philhealth', 'pagibig', 'tax'));

-- The four amounts (private; employee shares, rounded to the centavo) --------------------------------------------------------
create function public.payroll_sss(p_month_pay numeric) returns numeric
language sql stable set search_path = '' as $$
  select round(r.sss_rate * least(r.sss_msc_max, greatest(r.sss_msc_min, r.sss_msc_step * round(p_month_pay / r.sss_msc_step))), 2)
  from public.payroll_deduction_rates r
$$;
create function public.payroll_philhealth(p_month_base numeric) returns numeric
language sql stable set search_path = '' as $$
  select round(r.philhealth_rate * least(r.philhealth_ceiling, greatest(r.philhealth_floor, p_month_base)), 2)
  from public.payroll_deduction_rates r
$$;
create function public.payroll_pagibig(p_month_pay numeric) returns numeric
language sql stable set search_path = '' as $$
  select round(case when p_month_pay <= r.pagibig_low_limit then r.pagibig_low_rate else r.pagibig_rate end * least(greatest(p_month_pay, 0), r.pagibig_cap), 2)
  from public.payroll_deduction_rates r
$$;
create function public.payroll_tax(p_taxable numeric) returns numeric
language sql stable set search_path = '' as $$
  select coalesce((
    select round((b ->> 'base')::numeric + (b ->> 'rate')::numeric * (p_taxable - (b ->> 'over')::numeric), 2)
    from public.payroll_deduction_rates r, jsonb_array_elements(r.tax_brackets) b
    where p_taxable > (b ->> 'over')::numeric
    order by (b ->> 'over')::numeric desc limit 1), 0)
$$;

-- The pay maths without government deductions (0143's payroll_basis; manual deductions only) ---------------------------------
create function public.payroll_basis_core(p_period bigint)
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
           coalesce((select sum(pl.amount) from public.payroll_lines pl where pl.period_id = p_period and pl.user_id = pay.user_id and pl.kind = 'deduction' and coalesce(pl.category, 'other') not in ('sss', 'philhealth', 'pagibig', 'tax')), 0)::numeric(12,2) as deductions,
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
  -- 3: once paid, "Approved" is what the frozen line was paid for (payable minus corrections), not today's records
  select b.user_id, b.full_name, b.role, b.pay_type, b.rate, b.no_salary, b.personal,
         case when (select paid_at from per) is not null
              then case when b.pay_type = 'fixed' then 0 else greatest(0, b.payable - b.adjustment) end
              else b.logged end::numeric(8,2),
         b.adjustment, b.payable, b.base_pay,
         b.bonuses, b.deductions, (b.base_pay + b.bonuses)::numeric(12,2), (b.base_pay + b.bonuses - b.deductions)::numeric(12,2),
         b.pending, b.flagged
  from based b
$$;

-- The pay maths with government deductions: what payroll_period, payroll_mark_paid and the payslip read ------------------------
drop function public.payroll_basis(bigint);
create function public.payroll_basis(p_period bigint)
returns table (user_id uuid, full_name text, role text, pay_type text, rate numeric, no_salary boolean, personal boolean,
               logged numeric, adjustment numeric, payable numeric, base_pay numeric, bonuses numeric, deductions numeric,
               gross numeric, net numeric, pending int, flagged int,
               deductions_on boolean, manual_deductions numeric, sss numeric, philhealth numeric, pagibig numeric, tax numeric,
               gov_total numeric, gov_cut boolean, month_gross numeric, month_base numeric, taxable numeric)
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  half1 as (select f.period_id from public.payroll_periods f, per
            where extract(day from per.starts_on) = 16 and f.starts_on = date_trunc('month', per.starts_on)::date),
  core as (select * from public.payroll_basis_core(p_period)),
  fh as (select * from public.payroll_basis_core((select period_id from half1))),
  frozen as (
    select pl.user_id,
           sum(pl.amount) filter (where pl.category = 'sss') as sss, sum(pl.amount) filter (where pl.category = 'philhealth') as philhealth,
           sum(pl.amount) filter (where pl.category = 'pagibig') as pagibig, sum(pl.amount) filter (where pl.category = 'tax') as tax
    from public.payroll_lines pl where pl.period_id = p_period and pl.kind = 'deduction' group by pl.user_id),
  raw as (
    select c.*, per.paid_at is not null as paid,
           not c.no_salary and not exists (select 1 from public.payroll_no_deductions nd where nd.user_id = c.user_id) as on_,
           extract(day from per.starts_on) = 16 as second_half,
           (c.gross + coalesce(f.gross, 0))::numeric(12,2) as m_gross,
           (c.base_pay + coalesce(f.base_pay, 0))::numeric(12,2) as m_base
    from core c cross join per left join fh f on f.user_id = c.user_id),
  wanted as (
    select r.*,
           case when r.on_ and r.second_half then public.payroll_sss(r.m_gross) else 0 end as w_sss,
           case when r.on_ and r.second_half then public.payroll_philhealth(r.m_base) else 0 end as w_ph,
           case when r.on_ and r.second_half then public.payroll_pagibig(r.m_gross) else 0 end as w_pi
    from raw r),
  wanted2 as (
    select w.*, greatest(0, w.gross - w.w_sss - w.w_ph - w.w_pi)::numeric(12,2) as w_taxable,
           case when w.on_ then public.payroll_tax(greatest(0, w.gross - w.w_sss - w.w_ph - w.w_pi)) else 0 end as w_tax,
           greatest(0, w.gross - w.deductions) as room
    from wanted w),
  -- T8: taken in this order, each only up to what is left after the manual deductions
  taken as (
    select w.*,
           case when w.paid then coalesce(fz.sss, 0) else least(w.w_sss, w.room) end::numeric(12,2) as t_sss,
           case when w.paid then coalesce(fz.philhealth, 0) else least(w.w_ph, greatest(0, w.room - w.w_sss)) end::numeric(12,2) as t_ph,
           case when w.paid then coalesce(fz.pagibig, 0) else least(w.w_pi, greatest(0, w.room - w.w_sss - w.w_ph)) end::numeric(12,2) as t_pi,
           case when w.paid then coalesce(fz.tax, 0) else least(w.w_tax, greatest(0, w.room - w.w_sss - w.w_ph - w.w_pi)) end::numeric(12,2) as t_tax
    from wanted2 w left join frozen fz on fz.user_id = w.user_id)
  select t.user_id, t.full_name, t.role, t.pay_type, t.rate, t.no_salary, t.personal, t.logged, t.adjustment, t.payable, t.base_pay,
         t.bonuses, (t.deductions + t.t_sss + t.t_ph + t.t_pi + t.t_tax)::numeric(12,2), t.gross,
         (t.gross - t.deductions - t.t_sss - t.t_ph - t.t_pi - t.t_tax)::numeric(12,2), t.pending, t.flagged,
         t.on_, t.deductions, t.t_sss, t.t_ph, t.t_pi, t.t_tax, (t.t_sss + t.t_ph + t.t_pi + t.t_tax)::numeric(12,2),
         not t.paid and (t.t_sss + t.t_ph + t.t_pi + t.t_tax) < (t.w_sss + t.w_ph + t.w_pi + t.w_tax),
         case when t.second_half then t.m_gross else t.gross end, case when t.second_half then t.m_base else t.base_pay end, t.w_taxable
  from taken t
$$;

-- Switch per person (T4) --------------------------------------------------------------------------------------------------------
create function public.set_payroll_deductions(p_user uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
declare v_was boolean;
begin
  perform public.require_permission('reports:revenue');
  if p_user is null or p_on is null or public.payroll_role(p_user) is null then perform public.fail('bad_input', '22023'); end if;
  if p_user = auth.uid() then perform public.fail('own_salary', '42501'); end if;
  v_was := not exists (select 1 from public.payroll_no_deductions where user_id = p_user);
  if p_on then delete from public.payroll_no_deductions where user_id = p_user;
  else insert into public.payroll_no_deductions(user_id, set_by) values (p_user, auth.uid()) on conflict (user_id) do nothing;
  end if;
  if v_was <> p_on then
    perform public.audit('payroll.deductions_set', 'staff_salaries', p_user::text, jsonb_build_object('old', v_was, 'new', p_on));
  end if;
end $$;

create function public.payroll_deduction_rates_info() returns jsonb
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  return (select to_jsonb(r) - 'id' from public.payroll_deduction_rates r);
end $$;

drop function public.payroll_staff();
create function public.payroll_staff()
returns table (user_id uuid, full_name text, role text, amount numeric, pay_type text, personal boolean,
               updated_at timestamptz, updated_by_name text, deductions_on boolean)
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
         coalesce(r.updated_at, rs.updated_at), coalesce(r.updated_by_name, coalesce(nullif(btrim(rub.full_name), ''), rub.username)),
         not exists (select 1 from public.payroll_no_deductions nd where nd.user_id = r.id)
  from ranked r
  left join public.role_salaries rs on rs.role = r.role
  left join public.profiles rub on rub.id = rs.updated_by
  where r.role in ('employee', 'coach') or (r.role in ('subadmin', 'admin') and r.amount is not null)
  order by case r.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, r.full_name;
end $$;

-- Pay period: the government fields; opening a 16th-end period also opens its 1st-15th (the month's first half) ---------------
create or replace function public.payroll_period(p_starts_on date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_per public.payroll_periods;
  v_paid_by_name text;
  v_rows jsonb;
  v_net numeric(12,2);
  v_gross numeric(12,2);
  v_gov numeric(12,2);
begin
  perform public.require_permission('reports:revenue');
  insert into public.payroll_periods(starts_on, ends_on) values (p_starts_on, public.payroll_period_end(p_starts_on))
  on conflict (starts_on) do nothing;
  if extract(day from p_starts_on) = 16 then
    insert into public.payroll_periods(starts_on, ends_on)
    values (date_trunc('month', p_starts_on)::date, public.payroll_period_end(date_trunc('month', p_starts_on)::date))
    on conflict (starts_on) do nothing;
  end if;
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
           'deductions', b.deductions, 'net', b.net, 'no_salary', b.no_salary, 'pending', b.pending, 'flagged', b.flagged,
           'deductions_on', b.deductions_on, 'manual_deductions', b.manual_deductions, 'sss', b.sss, 'philhealth', b.philhealth,
           'pagibig', b.pagibig, 'tax', b.tax, 'gov_total', b.gov_total, 'gov_cut', b.gov_cut,
           'month_gross', b.month_gross, 'month_base', b.month_base, 'taxable', b.taxable)
         order by case b.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, b.full_name), '[]'::jsonb),
         coalesce(sum(b.net), 0), coalesce(sum(b.gross), 0), coalesce(sum(b.gov_total), 0)
  into v_rows, v_net, v_gross, v_gov
  from public.payroll_basis(v_per.period_id) b;

  return jsonb_build_object('period_id', v_per.period_id, 'starts_on', v_per.starts_on, 'ends_on', v_per.ends_on,
    'paid_at', v_per.paid_at, 'paid_by_name', v_paid_by_name, 'total_net', v_net, 'total_gross', v_gross, 'total_gov', v_gov,
    'rows', v_rows);
end $$;

-- Mark as paid: the 16th-end waits for the 1st-15th (T7); the government lines are frozen (T6) ----------------------------------
create or replace function public.payroll_mark_paid(p_period bigint) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_per public.payroll_periods;
  v_name text;
  v_count int;
  v_total numeric(12,2);
  v_month text;
  v_span text;
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
  if extract(day from v_per.starts_on) = 16
     and not exists (select 1 from public.payroll_periods f
                     where f.starts_on = date_trunc('month', v_per.starts_on)::date and f.paid_at is not null)
     and exists (select 1 from public.payroll_basis(p_period) b where b.deductions_on) then
    perform public.fail('first_half_unpaid', '22023');
  end if;

  select b.full_name into v_name from public.payroll_basis(p_period) b where b.pending > 0 order by b.full_name limit 1;
  if v_name is not null then perform public.fail('time_pending:' || v_name, '22023'); end if;
  select b.full_name into v_name from public.payroll_basis(p_period) b where b.net < 0 order by b.full_name limit 1;
  if v_name is not null then perform public.fail('net_below_zero:' || v_name, '22023'); end if;

  create temp table payroll_mark_rows on commit drop as select * from public.payroll_basis(p_period);

  insert into public.payroll_lines(period_id, user_id, kind, amount, pay_type, quantity, rate, created_by)
  select p_period, m.user_id, 'salary', m.base_pay, m.pay_type, m.payable, m.rate, auth.uid()
  from payroll_mark_rows m
  where not m.no_salary and (m.base_pay > 0 or m.pay_type <> 'fixed');

  v_month := to_char(v_per.starts_on, 'Mon YYYY');
  v_span := to_char(v_per.starts_on, 'FMDD') || '–' || to_char(v_per.ends_on, 'FMDD Mon YYYY');
  insert into public.payroll_lines(period_id, user_id, kind, amount, note, category, created_by)
  select p_period, m.user_id, 'deduction', x.amount, x.note, x.category, auth.uid()
  from payroll_mark_rows m
  cross join lateral (values
    ('sss', m.sss, 'SSS, ' || v_month || ', on ₱' || to_char(m.month_gross, 'FM999,999,990.00') || ' month pay'),
    ('philhealth', m.philhealth, 'PhilHealth, ' || v_month || ', on ₱' || to_char(m.month_base, 'FM999,999,990.00') || ' base pay'),
    ('pagibig', m.pagibig, 'Pag-IBIG, ' || v_month || ', on ₱' || to_char(least(m.month_gross, 10000), 'FM999,999,990.00')),
    ('tax', m.tax, 'Withholding tax, ' || v_span || ', on ₱' || to_char(m.taxable, 'FM999,999,990.00') || ' taxable')
  ) x(category, amount, note)
  where x.amount > 0;

  update public.payroll_periods set paid_at = now(), paid_by = auth.uid() where period_id = p_period;
  select count(distinct user_id)::int, coalesce(sum(case when kind = 'deduction' then -amount else amount end), 0)
  into v_count, v_total from public.payroll_lines where period_id = p_period;
  perform public.audit('payroll.paid', 'payroll_periods', p_period::text,
    jsonb_build_object('starts_on', v_per.starts_on, 'ends_on', v_per.ends_on, 'people', v_count, 'total', v_total));
  return public.payroll_period(v_per.starts_on);
end $$;

-- Grants (live default privileges reopen new and recreated functions): helpers callable by nobody --------------------------
revoke all on function
  public.payroll_sss(numeric), public.payroll_philhealth(numeric), public.payroll_pagibig(numeric), public.payroll_tax(numeric),
  public.payroll_basis_core(bigint), public.payroll_basis(bigint)
  from public, anon, authenticated;
revoke all on function
  public.set_payroll_deductions(uuid, boolean), public.payroll_deduction_rates_info(), public.payroll_staff(),
  public.payroll_period(date), public.payroll_mark_paid(bigint)
  from public, anon, authenticated;
grant execute on function
  public.set_payroll_deductions(uuid, boolean), public.payroll_deduction_rates_info(), public.payroll_staff(),
  public.payroll_period(date), public.payroll_mark_paid(bigint)
  to authenticated;
