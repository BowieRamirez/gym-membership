-- 0145: payroll exemptions, non-taxable pay, reopen before payout, retro pay (plan docs/superpowers/plans/2026-11-24-payroll-exemptions-reopen.md, U1-U12).
-- Two switches per person (contributions / withholding tax) with an optional reason (U1, U2); categories 13th month (tax-free up to
-- 90,000 a person a year, U4), de minimis (never taxed, U5), retro (U3); 13th month and de minimis left out of the SSS / Pag-IBIG
-- base (U6); Approve (paid_at, as before) -> Reopen with a reason while not paid out (U8) -> Mark as paid out (paid_out_at, U7, U9).

-- Tables ---------------------------------------------------------------------------------------------------------------------
alter table public.payroll_no_deductions
  add column contributions_off boolean not null default true,
  add column tax_off boolean not null default true,
  add column reason text check (reason is null or length(btrim(reason)) between 1 and 60);
comment on table public.payroll_no_deductions is 'People with government contributions and/or withholding tax switched off (0144, 0145); no row = both on';

alter table public.payroll_deduction_rates add column thirteenth_month_exempt numeric(12,2) not null default 90000;

alter table public.payroll_lines drop constraint payroll_lines_category_check;
alter table public.payroll_lines add constraint payroll_lines_category_check
  check (category in ('regular_bonus', 'overtime', 'holiday', 'other', 'thirteenth_month', 'de_minimis', 'retro',
                      'sss', 'philhealth', 'pagibig', 'tax'));

alter table public.payroll_periods
  add column paid_out_at timestamptz,
  add column paid_out_by uuid references public.profiles(id);
-- periods paid before this release were paid out (U7)
update public.payroll_periods set paid_out_at = paid_at, paid_out_by = paid_by where paid_at is not null;

-- The pay maths (0144's, plus U1 and U4-U6) ----------------------------------------------------------------------------------
drop function public.payroll_basis(bigint);
create function public.payroll_basis(p_period bigint)
returns table (user_id uuid, full_name text, role text, pay_type text, rate numeric, no_salary boolean, personal boolean,
               logged numeric, adjustment numeric, payable numeric, base_pay numeric, bonuses numeric, deductions numeric,
               gross numeric, net numeric, pending int, flagged int,
               deductions_on boolean, manual_deductions numeric, sss numeric, philhealth numeric, pagibig numeric, tax numeric,
               gov_total numeric, gov_cut boolean, month_gross numeric, month_base numeric, taxable numeric, msc numeric, pagibig_base numeric,
               contributions_on boolean, tax_on boolean, deductions_reason text, nontaxable numeric, thirteenth_used numeric)
language sql stable set search_path = '' as $$
  with per as (select * from public.payroll_periods where period_id = p_period),
  half1 as (select f.period_id from public.payroll_periods f, per
            where extract(day from per.starts_on) = 16 and f.starts_on = date_trunc('month', per.starts_on)::date),
  core as (select * from public.payroll_basis_core(p_period)),
  fh as (select * from public.payroll_basis_core((select period_id from half1))),
  -- 13th month and de minimis bonuses, and retro deductions (pay taken back), per person: this period and its first half
  cat as (
    select pl.period_id, pl.user_id,
           coalesce(sum(pl.amount) filter (where pl.kind = 'bonus' and pl.category = 'thirteenth_month'), 0) as m13,
           coalesce(sum(pl.amount) filter (where pl.kind = 'bonus' and pl.category = 'de_minimis'), 0) as dm,
           coalesce(sum(pl.amount) filter (where pl.kind = 'deduction' and pl.category = 'retro'), 0) as rd
    from public.payroll_lines pl
    where pl.period_id in (p_period, coalesce((select period_id from half1), -1))
    group by pl.period_id, pl.user_id),
  frozen as (
    select pl.user_id,
           sum(pl.amount) filter (where pl.category = 'sss') as sss, sum(pl.amount) filter (where pl.category = 'philhealth') as philhealth,
           sum(pl.amount) filter (where pl.category = 'pagibig') as pagibig, sum(pl.amount) filter (where pl.category = 'tax') as tax
    from public.payroll_lines pl where pl.period_id = p_period and pl.kind = 'deduction' group by pl.user_id),
  raw as (
    select c.*, per.paid_at is not null as paid,
           nd.reason as off_reason,
           not c.no_salary and coalesce(not nd.contributions_off, true) as c_on,
           not c.no_salary and coalesce(not nd.tax_off, true) as t_on,
           extract(day from per.starts_on) = 16 as second_half,
           coalesce(ct.m13, 0) as m13, coalesce(ct.dm, 0) as dm, coalesce(ct.rd, 0) as rd,
           -- U4: 13th month in the other approved periods of this calendar year, whatever order they were approved in
           coalesce((select sum(pl.amount) from public.payroll_lines pl join public.payroll_periods pp on pp.period_id = pl.period_id
                     where pl.user_id = c.user_id and pl.kind = 'bonus' and pl.category = 'thirteenth_month' and pp.paid_at is not null
                       and pp.period_id <> per.period_id and extract(year from pp.starts_on) = extract(year from per.starts_on)), 0) as m13_before,
           -- U6: the contribution base leaves out 13th month and de minimis and takes off retro deductions (both halves)
           greatest(0, c.gross + coalesce(f.gross, 0) - coalesce(ct.m13, 0) - coalesce(ct.dm, 0) - coalesce(cf.m13, 0) - coalesce(cf.dm, 0)
                       - coalesce(ct.rd, 0) - coalesce(cf.rd, 0))::numeric(12,2) as m_gross,
           (c.base_pay + coalesce(f.base_pay, 0))::numeric(12,2) as m_base
    from core c cross join per
    left join fh f on f.user_id = c.user_id
    left join public.payroll_no_deductions nd on nd.user_id = c.user_id
    left join cat ct on ct.user_id = c.user_id and ct.period_id = p_period
    left join cat cf on cf.user_id = c.user_id and cf.period_id = (select period_id from half1)),
  exempt as (
    select r.*,
           (least(r.m13, greatest(0, (select thirteenth_month_exempt from public.payroll_deduction_rates) - r.m13_before)) + r.dm)::numeric(12,2) as nontax
    from raw r),
  wanted as (
    select r.*,
           case when r.c_on and r.second_half and r.m_gross > 0 then public.payroll_sss(r.m_gross) else 0 end as w_sss,
           case when r.c_on and r.second_half and r.m_gross > 0 then public.payroll_philhealth(r.m_base) else 0 end as w_ph,
           case when r.c_on and r.second_half and r.m_gross > 0 then public.payroll_pagibig(r.m_gross) else 0 end as w_pi
    from exempt r),
  wanted2 as (
    select w.*, greatest(0, w.gross - w.nontax - w.rd - w.w_sss - w.w_ph - w.w_pi)::numeric(12,2) as w_taxable,
           case when w.t_on then public.payroll_tax(greatest(0, w.gross - w.nontax - w.rd - w.w_sss - w.w_ph - w.w_pi)) else 0 end as w_tax,
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
         t.c_on or t.t_on, t.deductions, t.t_sss, t.t_ph, t.t_pi, t.t_tax, (t.t_sss + t.t_ph + t.t_pi + t.t_tax)::numeric(12,2),
         not t.paid and (t.t_sss + t.t_ph + t.t_pi + t.t_tax) < (t.w_sss + t.w_ph + t.w_pi + t.w_tax),
         case when t.second_half then t.m_gross else greatest(0, t.gross - t.m13 - t.dm - t.rd) end, case when t.second_half then t.m_base else t.base_pay end, t.w_taxable,
         (select least(r.sss_msc_max, greatest(r.sss_msc_min, r.sss_msc_step * round(t.m_gross / r.sss_msc_step))) from public.payroll_deduction_rates r)::numeric(12,2),
         (select least(greatest(t.m_gross, 0), r.pagibig_cap) from public.payroll_deduction_rates r)::numeric(12,2),
         t.c_on, t.t_on, t.off_reason, t.nontax, (t.m13_before + t.m13)::numeric(12,2)
  from taken t
$$;

-- Two switches (U1, U2); the released app's one switch turns both on or off ----------------------------------------------------
create function public.set_payroll_deductions(p_user uuid, p_contributions boolean, p_tax boolean, p_reason text default null) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_old public.payroll_no_deductions;
  v_reason text := nullif(btrim(coalesce(p_reason, '')), '');
begin
  perform public.require_permission('reports:revenue');
  if p_user is null or p_contributions is null or p_tax is null or length(v_reason) > 60 or public.payroll_role(p_user) is null
     or not exists (select 1 from public.profiles p where p.id = p_user and p.is_active and p.deleted_at is null) then
    perform public.fail('bad_input', '22023');
  end if;
  if p_user = auth.uid() then perform public.fail('own_salary', '42501'); end if;
  select * into v_old from public.payroll_no_deductions where user_id = p_user;
  if p_contributions and p_tax then
    delete from public.payroll_no_deductions where user_id = p_user;
  else
    insert into public.payroll_no_deductions(user_id, set_by, contributions_off, tax_off, reason)
    values (p_user, auth.uid(), not p_contributions, not p_tax, v_reason)
    on conflict (user_id) do update set contributions_off = excluded.contributions_off, tax_off = excluded.tax_off,
                                        reason = excluded.reason, set_by = excluded.set_by, set_at = now();
  end if;
  -- log real changes only
  if coalesce(not v_old.contributions_off, true) <> p_contributions or coalesce(not v_old.tax_off, true) <> p_tax
     or (not (p_contributions and p_tax) and v_old.reason is distinct from v_reason) then
    perform public.audit('payroll.deductions_set', 'staff_salaries', p_user::text, jsonb_build_object(
      'old_contributions', coalesce(not v_old.contributions_off, true), 'old_tax', coalesce(not v_old.tax_off, true),
      'contributions', p_contributions, 'tax', p_tax, 'reason', v_reason));
  end if;
end $$;

create or replace function public.set_payroll_deductions(p_user uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.set_payroll_deductions(p_user, p_on, p_on, null);
end $$;

drop function public.payroll_staff();
create function public.payroll_staff()
returns table (user_id uuid, full_name text, role text, amount numeric, pay_type text, personal boolean,
               updated_at timestamptz, updated_by_name text, deductions_on boolean,
               contributions_on boolean, tax_on boolean, deductions_reason text)
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
         coalesce(not (nd.contributions_off and nd.tax_off), true),
         coalesce(not nd.contributions_off, true), coalesce(not nd.tax_off, true), nd.reason
  from ranked r
  left join public.role_salaries rs on rs.role = r.role
  left join public.profiles rub on rub.id = rs.updated_by
  left join public.payroll_no_deductions nd on nd.user_id = r.id
  where r.role in ('employee', 'coach') or (r.role in ('subadmin', 'admin') and r.amount is not null)
  order by case r.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, r.full_name;
end $$;

-- Categories (U3): 13th month and de minimis are bonuses only; retro both ways -----------------------------------------------
create or replace function public.payroll_add_line(p_period bigint, p_user uuid, p_kind text, p_amount numeric, p_note text,
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
     or p_category is null or p_category not in ('regular_bonus', 'overtime', 'holiday', 'other', 'thirteenth_month', 'de_minimis', 'retro')
     or (p_kind = 'deduction' and p_category in ('thirteenth_month', 'de_minimis')) then
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

-- Reopen and Paid out (U7-U9) ------------------------------------------------------------------------------------------------
create function public.payroll_reopen(p_period bigint, p_reason text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_per public.payroll_periods;
  v_reason text := nullif(btrim(coalesce(p_reason, '')), '');
  v_removed int;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null or v_reason is null or length(v_reason) > 200 then perform public.fail('bad_input', '22023'); end if;
  select * into v_per from public.payroll_periods where period_id = p_period for update;
  if not found then perform public.fail('bad_input', '22023'); end if;
  if v_per.paid_out_at is not null then perform public.fail('paid_out', 'P0002'); end if;
  if v_per.paid_at is null then perform public.fail('not_approved', '22023'); end if;
  -- lock the month's 16th-end too, so an Approve of it can't slip in while this reopens (it waits, then sees this as a draft)
  perform 1 from public.payroll_periods s where s.starts_on = v_per.starts_on + 15 for update;
  if extract(day from v_per.starts_on) = 1 and exists (
       select 1 from public.payroll_periods s where s.starts_on = v_per.starts_on + 15 and s.paid_at is not null) then
    perform public.fail('second_half_approved', '22023');
  end if;
  delete from public.payroll_lines
  where period_id = p_period and (kind = 'salary' or category in ('sss', 'philhealth', 'pagibig', 'tax'));
  get diagnostics v_removed = row_count;
  update public.payroll_periods set paid_at = null, paid_by = null where period_id = p_period;
  perform public.audit('payroll.reopened', 'payroll_periods', p_period::text,
    jsonb_build_object('starts_on', v_per.starts_on, 'ends_on', v_per.ends_on, 'reason', v_reason, 'lines_removed', v_removed));
  return public.payroll_period(v_per.starts_on);
end $$;

create function public.payroll_mark_paid_out(p_period bigint) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_per public.payroll_periods;
begin
  perform public.require_permission('reports:revenue');
  if p_period is null then perform public.fail('bad_input', '22023'); end if;
  select * into v_per from public.payroll_periods where period_id = p_period for update;
  if not found then perform public.fail('bad_input', '22023'); end if;
  if v_per.paid_out_at is not null then perform public.fail('paid_out', 'P0002'); end if;
  if v_per.paid_at is null then perform public.fail('not_approved', '22023'); end if;
  update public.payroll_periods set paid_out_at = now(), paid_out_by = auth.uid() where period_id = p_period;
  perform public.audit('payroll.paid_out', 'payroll_periods', p_period::text,
    jsonb_build_object('starts_on', v_per.starts_on, 'ends_on', v_per.ends_on));
  return public.payroll_period(v_per.starts_on);
end $$;

-- Pay period: status and the new fields --------------------------------------------------------------------------------------
create or replace function public.payroll_period(p_starts_on date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_per public.payroll_periods;
  v_paid_by_name text;
  v_paid_out_by_name text;
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
  if v_per.paid_out_by is not null then
    select coalesce(nullif(btrim(full_name), ''), username) into v_paid_out_by_name from public.profiles where id = v_per.paid_out_by;
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
           'month_gross', b.month_gross, 'month_base', b.month_base, 'taxable', b.taxable, 'msc', b.msc, 'pagibig_base', b.pagibig_base,
           'contributions_on', b.contributions_on, 'tax_on', b.tax_on, 'deductions_reason', b.deductions_reason,
           'nontaxable', b.nontaxable, 'thirteenth_used', b.thirteenth_used)
         order by case b.role when 'employee' then 1 when 'coach' then 2 when 'subadmin' then 3 else 4 end, b.full_name), '[]'::jsonb),
         coalesce(sum(b.net), 0), coalesce(sum(b.gross), 0), coalesce(sum(b.gov_total), 0)
  into v_rows, v_net, v_gross, v_gov
  from public.payroll_basis(v_per.period_id) b;

  return jsonb_build_object('period_id', v_per.period_id, 'starts_on', v_per.starts_on, 'ends_on', v_per.ends_on,
    'paid_at', v_per.paid_at, 'paid_by_name', v_paid_by_name, 'paid_out_at', v_per.paid_out_at, 'paid_out_by_name', v_paid_out_by_name,
    'status', case when v_per.paid_out_at is not null then 'paid_out' when v_per.paid_at is not null then 'approved' else 'draft' end,
    'total_net', v_net, 'total_gross', v_gross, 'total_gov', v_gov, 'rows', v_rows);
end $$;

-- Logs & Archive names for the new rows are already covered (payroll_periods by period, staff_salaries by person) ------------
-- Grants (live default privileges reopen new and recreated functions) --------------------------------------------------------
revoke all on function public.payroll_basis(bigint) from public, anon, authenticated;
revoke all on function
  public.set_payroll_deductions(uuid, boolean, boolean, text), public.set_payroll_deductions(uuid, boolean),
  public.payroll_staff(), public.payroll_add_line(bigint, uuid, text, numeric, text, text),
  public.payroll_reopen(bigint, text), public.payroll_mark_paid_out(bigint), public.payroll_period(date)
  from public, anon, authenticated;
grant execute on function
  public.set_payroll_deductions(uuid, boolean, boolean, text), public.set_payroll_deductions(uuid, boolean),
  public.payroll_staff(), public.payroll_add_line(bigint, uuid, text, numeric, text, text),
  public.payroll_reopen(bigint, text), public.payroll_mark_paid_out(bigint), public.payroll_period(date)
  to authenticated;
