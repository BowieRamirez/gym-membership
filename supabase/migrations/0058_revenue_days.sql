-- Owner, 2026-11-12: the Windows Revenue page shows today, this week, this month, a day-by-day chart and check-ins a day
-- (mockup docs/mockups/revenue.html). One row per Manila day, quiet days included, so any period up to a year stays far under
-- the 1,000-row limit of a read. Counted as revenue_summary counts (0027): verified payments on the day they were verified,
-- approved or paid refunds on the day they were decided, voided payments left out. Visits are every gym check-in, walk-ins included.
create function public.revenue_days(p_from date, p_to date)
returns table(day date, net numeric, payments bigint, refunds numeric, visits bigint)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_from is null or p_to is null or p_from > p_to or p_to - p_from > 366 then perform public.fail('bad_input','22023'); end if;
  return query
  with d as (select g::date as day from generate_series(p_from, p_to, interval '1 day') g),
  pay as (select (p.verified_at at time zone 'Asia/Manila')::date as day, count(*) as n, sum(p.amount) as amt from public.payments p
          where p.status='verified' and (p.verified_at at time zone 'Asia/Manila')::date between p_from and p_to group by 1),
  ref as (select (r.decided_at at time zone 'Asia/Manila')::date as day, sum(r.amount) as amt from public.refund_requests r
          where r.status in ('approved','paid') and (r.decided_at at time zone 'Asia/Manila')::date between p_from and p_to group by 1),
  vis as (select (c.checked_in_at at time zone 'Asia/Manila')::date as day, count(*) as n from public.gym_check_ins c
          where (c.checked_in_at at time zone 'Asia/Manila')::date between p_from and p_to group by 1)
  select d.day, coalesce(pay.amt,0) - coalesce(ref.amt,0), coalesce(pay.n,0)::bigint, coalesce(ref.amt,0), coalesce(vis.n,0)::bigint
  from d left join pay on pay.day = d.day left join ref on ref.day = d.day left join vis on vis.day = d.day
  order by d.day;
end $$;
-- Supabase's default privileges give a new function to anon too (see 0056); only signed-in staff with the permission get past the check
revoke all on function public.revenue_days(date, date) from public, anon, authenticated;
grant execute on function public.revenue_days(date, date) to authenticated;
