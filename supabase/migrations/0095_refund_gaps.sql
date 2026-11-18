-- Owner, 2026-11-18: three gaps in refunds.
-- 1. A walk-in's booking has no member, so cancel_reservation's "is it mine" checks were NULL: under 24 hours the gym cancelling made no refund,
--    no reason was asked for, and anyone signed in could cancel it.
-- 2. A closed day refunds the down payment + balance (0065), but repaying compared that with the one payment on the request and refused it.
-- 3. Online money that can't be applied (late, wrong amount, 0075 X9) only notified staff. It is now an approved 'online' refund on the Refunds
--    page. The payment was never revenue, so its refund doesn't reduce revenue; cash repaid still leaves the drawer.

alter table public.refund_requests
  drop constraint refund_requests_kind_check,
  drop constraint refund_requests_target,
  drop constraint refund_requests_payment_id_key;
alter table public.refund_requests
  add constraint refund_requests_kind_check check (kind in ('membership', 'cancel', 'no_show', 'online')),
  add constraint refund_requests_target check ((kind = 'membership') = (term_id is not null) and (kind in ('cancel', 'no_show')) = (reservation_id is not null));
-- stray online money can land on a payment that already has its own refund
create unique index refund_requests_one_per_payment on public.refund_requests (payment_id, (kind = 'online'));

-- no staff member decides this one (decided_by stays empty, as for a slot lost to an online payment, 0075)
create function public.record_online_refund(p public.payments, p_amount numeric, p_channel text) returns void
language plpgsql security definer set search_path='' as $$
declare v_known boolean := coalesce(p_amount > 0, false); v_due numeric; v_why text; v_req bigint;
begin
  v_due := case when v_known then p_amount else p.amount end;
  v_why := case
    when not v_known then 'Xendit did not report a usable amount: check the payment in Xendit before repaying'
    when p.status <> 'pending' or p.expires_at <= now() then 'Paid online after this payment was no longer open'
    else 'Paid online with the wrong amount: PHP ' || to_char(p_amount, 'FM999,999,990.00') || ' instead of PHP ' || to_char(p.amount, 'FM999,999,990.00')
  end;
  insert into public.refund_requests (kind, payment_id, member_id, amount, reason, status, decided_at, decision_reason)
  values ('online', p.payment_id, p.member_id, v_due, 'Online payment not applied', 'approved', now(), v_why)
  returning refund_request_id into v_req;
  perform public.audit_as(coalesce(p.member_id, p.recorded_by), 'refund.request', 'refund_requests', v_req::text,
    jsonb_build_object('amount', v_due, 'payment_id', p.payment_id, 'kind', 'online', 'channel', p_channel));
end $$;

-- the most that can be repaid is what was really received and still stands: the payment, plus the booking's balance (a closed day refunds both)
create or replace function public.record_membership_refund_paid(p_request_id bigint,p_method public.payment_method,p_reference text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_request public.refund_requests; v_pay public.payments; v_received numeric;
begin
  perform public.require_permission('payments:void');
  if p_method is null or p_method not in ('cash','gcash') or (p_method='gcash' and coalesce(btrim(p_reference),'')='') then
    perform public.fail('bad_input','22023');
  end if;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_request.member_id=auth.uid() then perform public.fail('self_decision','42501'); end if;
  select * into v_pay from public.payments where payment_id=v_request.payment_id for update;
  select * into v_request from public.refund_requests where refund_request_id=p_request_id for update;
  if v_request.status<>'approved' then perform public.fail('already_processed','P0002'); end if;
  if v_request.kind<>'online' then   -- online: the money came from Xendit, never from a verified payment
    select coalesce(sum(p.amount),0) into v_received from public.payments p where p.status='verified' and p.payment_id in
      (v_request.payment_id, (select r.balance_payment_id from public.reservations r where r.reservation_id=v_request.reservation_id));
    if v_pay.status<>'verified' or v_request.amount<=0 or v_request.amount>v_received then perform public.fail('not_refundable','P0002'); end if;
  end if;
  update public.refund_requests set status='paid',paid_by=auth.uid(),paid_at=now(),repayment_method=p_method,
    repayment_reference=nullif(btrim(p_reference),'') where refund_request_id=p_request_id;
  perform public.notify(v_request.member_id,'payment','Refund repaid',p_method::text||' · PHP '||to_char(v_request.amount,'FM999,999,990.00'),
    jsonb_build_object('refund_request_id',p_request_id));
  perform public.audit('refund.paid','refund_requests',p_request_id::text,
    jsonb_build_object('amount',v_request.amount,'method',p_method,'reference',nullif(btrim(p_reference),'')));
end $$;

do $$
declare v_def text; r record;
begin
  for r in select * from (values
    ('public.cancel_reservation(bigint,text)'::regprocedure,
     $q$(v.member_id <> v_uid and$q$, $q$(v.member_id is distinct from v_uid and$q$),
    ('public.cancel_reservation(bigint,text)'::regprocedure,
     $q$v_mine := v.member_id = v_uid;$q$, $q$v_mine := v.member_id is not distinct from v_uid;$q$),
    ('public.confirm_online_payment(text,numeric,text)'::regprocedure,
     $q$update public.payments set online_paid_amount = coalesce(p_amount, 0), online_channel = p_channel where payment_id = v.payment_id;$q$,
     $q$update public.payments set online_paid_amount = coalesce(p_amount, 0), online_channel = p_channel where payment_id = v.payment_id;
    perform public.record_online_refund(v, p_amount, p_channel);   -- 0095$q$),
    ('public.revenue_summary(date,date)'::regprocedure,
     $q$using(payment_id) where r.status in ('approved','paid')$q$, $q$using(payment_id) where r.kind <> 'online' and r.status in ('approved','paid')$q$),
    ('public.revenue_lines(date,date,integer,integer)'::regprocedure,
     $q$where r.status in ('approved','paid') and$q$, $q$where r.kind <> 'online' and r.status in ('approved','paid') and$q$),
    ('public.revenue_days(date,date)'::regprocedure,
     $q$where r.status in ('approved','paid') and$q$, $q$where r.kind <> 'online' and r.status in ('approved','paid') and$q$)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0095: % lacks "%"', r.fn, r.old_text; end if;
    execute replace(v_def, r.old_text, r.new_text);
  end loop;
end $$;

revoke all on function public.record_online_refund(public.payments, numeric, text),
  public.confirm_online_payment(text, numeric, text) from public, anon, authenticated;
grant execute on function public.confirm_online_payment(text, numeric, text) to service_role;
revoke all on function public.cancel_reservation(bigint, text), public.record_membership_refund_paid(bigint, public.payment_method, text),
  public.revenue_summary(date, date), public.revenue_lines(date, date, integer, integer), public.revenue_days(date, date) from public, anon;
grant execute on function public.cancel_reservation(bigint, text), public.record_membership_refund_paid(bigint, public.payment_method, text),
  public.revenue_summary(date, date), public.revenue_lines(date, date, integer, integer), public.revenue_days(date, date) to authenticated;
