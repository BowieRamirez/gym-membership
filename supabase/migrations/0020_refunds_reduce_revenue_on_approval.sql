-- Revenue drops on the day an administrator approves a refund (the money is owed from then on).
-- The cash drawer (daily_cash_summary) still drops only when the repayment is recorded, because that is when cash leaves.
-- The refund is netted against the method the member originally paid with, so it does not move again when it is repaid.
create or replace function public.revenue_summary(p_from date,p_to date)
returns table(day date,kind public.payment_kind,method public.payment_method,payments bigint,total numeric)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_permission('reports:revenue');
  return query select q.d,q.k,q.m,sum(q.n)::bigint,sum(q.amount) from (
    select (p.verified_at at time zone 'Asia/Manila')::date d,p.kind k,p.method m,1::bigint n,p.amount
      from public.payments p where p.status='verified'
    union all
    select (r.decided_at at time zone 'Asia/Manila')::date,p.kind,p.method,0::bigint,-r.amount
      from public.refund_requests r join public.payments p using(payment_id) where r.status in ('approved','paid')
  ) q where q.d between p_from and p_to group by q.d,q.k,q.m order by q.d,q.k,q.m;
end $$;
