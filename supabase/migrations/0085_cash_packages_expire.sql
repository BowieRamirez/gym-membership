-- Cash plan 2026-11-17, review fix: a cash package payment (0084) keeps the same deadline as an online one.
-- Before this, verify_payment refused only an expired 'qr' payment and expire_payments skipped cash that isn't a booking hold,
-- so a member's unpaid cash choice stayed pending for ever (blocking their next purchase and holding a discount use)
-- and the desk could still approve it after "Pay by …". Pending cash only ever comes from a member's choice in the app:
-- counter cash is verified the moment it is recorded.

-- the approve guard, as in 0018 (renamed verify_payment_before_xendit by 0075), now for cash too
create or replace function public.verify_payment_before_xendit(p_payment_id bigint, p_approve boolean, p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments;
begin
  perform public.require_permission('payments:verify');
  select * into v_pay from public.payments where payment_id = p_payment_id for update;
  if p_approve and v_pay.status = 'pending' and v_pay.method in ('qr', 'cash') and v_pay.expires_at <= now() then
    perform public.fail('payment_expired', 'P0002');
  end if;
  perform public.verify_payment_before_terms(p_payment_id, p_approve, p_reason);
end $$;

-- as in 0052, with every pending cash payment, not only booking holds
create or replace function public.expire_payments() returns int
language plpgsql security definer set search_path='' as $$
declare v_pay record; v_count int := 0;
begin
  for v_pay in
    select p.payment_id, p.member_id, p.payment_code from public.payments p
    where p.status = 'pending' and p.expires_at <= now() and p.method in ('qr', 'cash')
    for update
  loop
    update public.payments set status = 'expired' where payment_id = v_pay.payment_id;
    perform public.cancel_payment_target(v_pay.payment_id);
    perform public.notify(v_pay.member_id, 'payment', 'Payment expired', v_pay.payment_code, jsonb_build_object('payment_id', v_pay.payment_id));
    v_count := v_count + 1;
  end loop;
  return v_count;
end $$;

revoke all on function public.verify_payment_before_xendit(bigint, boolean, text), public.expire_payments() from public, anon, authenticated;
