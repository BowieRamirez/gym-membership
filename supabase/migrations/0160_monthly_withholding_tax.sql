-- Owner, 2026-11-25: "only have withholding tax when the salary exceeds 20,833 PHP". Withholding tax was taken every pay on that pay
-- (0144 T3, BIR semi-monthly table), so a month under 20,833 was taxed whenever one half went over 10,417. Now it is worked out once a
-- month in the 16th-end pay, on the whole month's taxable pay, with BIR's monthly table (RR 11-2018 Annex E, 2023 on: 0 up to 20,833);
-- the 1st-15th pay has none, like SSS, PhilHealth and Pag-IBIG. Tax a 1st-15th pay already took (approved before this) is taken off.

update public.payroll_deduction_rates set
  tax_brackets = '[{"over":0,"base":0,"rate":0},{"over":20833,"base":0,"rate":0.15},{"over":33333,"base":1875,"rate":0.20},
    {"over":66667,"base":8541.80,"rate":0.25},{"over":166667,"base":33541.80,"rate":0.30},{"over":666667,"base":183541.80,"rate":0.35}]',
  source = 'SSS 15% (employee 5%) MSC 5,000-35,000; PhilHealth 5% (employee 2.5%) 10,000-100,000; Pag-IBIG 2% (1% up to 1,500) on up to 10,000; BIR monthly table RR 11-2018 Annex E (TRAIN, 2023 on), taken in the 16th-end pay on the month. Confirm with the accountant.';
alter table public.payroll_deduction_rates alter column tax_brackets set default
  '[{"over":0,"base":0,"rate":0},{"over":20833,"base":0,"rate":0.15},{"over":33333,"base":1875,"rate":0.20},
    {"over":66667,"base":8541.80,"rate":0.25},{"over":166667,"base":33541.80,"rate":0.30},{"over":666667,"base":183541.80,"rate":0.35}]';

-- the 1st-15th pay's taxable pay and the tax it took, per person (plpgsql, so payroll_basis is never inlined into itself)
create function public.payroll_half_tax(p_period bigint)
returns table (user_id uuid, taxable numeric, tax numeric)
language plpgsql stable set search_path = '' as $$
begin
  if p_period is null then return; end if;
  return query select b.user_id, b.taxable, b.tax from public.payroll_basis(p_period) b;
end $$;

create or replace function public.payroll_basis(p_period bigint)
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
  -- 0160 (owner): withholding tax once a month, in the 16th-end pay, on the month's taxable pay (this half + the 1st-15th), monthly
  -- BIR table, less any tax the 1st-15th pay already took (frozen before 0160); the 1st-15th pay has none, like the contributions
  half as (
    select w.*, greatest(0, w.gross - w.nontax - w.rd - w.w_sss - w.w_ph - w.w_pi)::numeric(12,2) as h_taxable
    from wanted w),
  wanted2 as (
    select h.*,
           case when h.second_half then (h.h_taxable + coalesce(fd.taxable, 0))::numeric(12,2) else h.h_taxable end as w_taxable,
           case when h.t_on and h.second_half then greatest(0, public.payroll_tax(h.h_taxable + coalesce(fd.taxable, 0)) - coalesce(fd.tax, 0)) else 0 end as w_tax,
           greatest(0, h.gross - h.deductions) as room
    from half h left join public.payroll_half_tax((select period_id from half1)) fd on fd.user_id = h.user_id),
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

revoke all on function public.payroll_half_tax(bigint) from public, anon, authenticated;
revoke all on function public.payroll_basis(bigint) from public, anon, authenticated;
