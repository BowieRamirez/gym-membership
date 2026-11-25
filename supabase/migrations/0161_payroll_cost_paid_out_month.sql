-- 0161 (owner, 2026-11-25): Financial reports count payroll on the Manila day it was paid out, not on the
-- period's last day; an approved period that isn't paid out yet is left out.
create or replace function public.payroll_cost(p_from date, p_to date)
returns table (day date, gross numeric)
language plpgsql stable security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_to < p_from then perform public.fail('bad_input', '22023'); end if;
  return query
  select (pp.paid_out_at at time zone 'Asia/Manila')::date,
         coalesce(sum(pl.amount) filter (where pl.kind in ('salary', 'bonus')), 0)::numeric(12,2)
  from public.payroll_periods pp
  left join public.payroll_lines pl on pl.period_id = pp.period_id
  where pp.paid_out_at is not null and (pp.paid_out_at at time zone 'Asia/Manila')::date between p_from and p_to
  group by pp.period_id, pp.paid_out_at
  order by pp.paid_out_at;
end $$;

revoke all on function public.payroll_cost(date, date) from public, anon;
grant execute on function public.payroll_cost(date, date) to authenticated;
