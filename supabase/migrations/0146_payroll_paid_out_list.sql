-- 0146: list all paid-out payroll periods with their frozen totals for the Paid out table on PayrollPage.
create function public.payroll_paid_out_periods()
returns table (
  period_id bigint,
  starts_on date,
  ends_on date,
  paid_at timestamptz,
  paid_by_name text,
  paid_out_at timestamptz,
  paid_out_by_name text,
  people int,
  base_pay numeric,
  bonuses numeric,
  gross numeric,
  deductions numeric,
  gov_total numeric,
  net numeric
)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  return query
  with totals as (
    select pl.period_id,
           count(distinct pl.user_id)::int as people,
           coalesce(sum(pl.amount) filter (where pl.kind = 'salary'), 0)::numeric(12,2) as base_pay,
           coalesce(sum(pl.amount) filter (where pl.kind = 'bonus'), 0)::numeric(12,2) as bonuses,
           coalesce(sum(pl.amount) filter (where pl.kind in ('salary', 'bonus')), 0)::numeric(12,2) as gross,
           coalesce(sum(pl.amount) filter (where pl.kind = 'deduction'), 0)::numeric(12,2) as deductions,
           coalesce(sum(pl.amount) filter (where pl.kind = 'deduction' and pl.category in ('sss', 'philhealth', 'pagibig', 'tax')), 0)::numeric(12,2) as gov_total,
           coalesce(sum(case when pl.kind = 'deduction' then -pl.amount else pl.amount end), 0)::numeric(12,2) as net
    from public.payroll_lines pl
    group by pl.period_id
  )
  select pp.period_id, pp.starts_on, pp.ends_on,
         pp.paid_at, coalesce(nullif(btrim(pb.full_name), ''), pb.username),
         pp.paid_out_at, coalesce(nullif(btrim(po.full_name), ''), po.username),
         coalesce(t.people, 0),
         coalesce(t.base_pay, 0)::numeric(12,2),
         coalesce(t.bonuses, 0)::numeric(12,2),
         coalesce(t.gross, 0)::numeric(12,2),
         coalesce(t.deductions, 0)::numeric(12,2),
         coalesce(t.gov_total, 0)::numeric(12,2),
         coalesce(t.net, 0)::numeric(12,2)
  from public.payroll_periods pp
  left join totals t on t.period_id = pp.period_id
  left join public.profiles pb on pb.id = pp.paid_by
  left join public.profiles po on po.id = pp.paid_out_by
  where pp.paid_out_at is not null
  order by pp.starts_on desc, pp.paid_out_at desc;
end $$;

revoke all on function public.payroll_paid_out_periods() from public, anon, authenticated;
grant execute on function public.payroll_paid_out_periods() to authenticated;
