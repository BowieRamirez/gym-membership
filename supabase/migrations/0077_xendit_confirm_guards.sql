-- Xendit plan, final review fixes: confirm_online_payment (0075) now
--  * treats a missing amount as a mismatch (`<>` with NULL was NULL, so a payment with no amount fell through to confirmed);
--  * reports a refund to staff once: the app re-checks every 20 seconds and Xendit retries, and each call notified again.
create or replace function public.confirm_online_payment(p_xendit_id text, p_amount numeric, p_channel text default null) returns text
language plpgsql security definer set search_path='' as $$
declare v public.payments;
begin
  select * into v from public.payments where xendit_id = p_xendit_id for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.status = 'verified' and v.reference_no = p_xendit_id then return 'already'; end if;

  if v.status <> 'pending' or v.expires_at <= now() or p_amount is distinct from v.amount then
    if v.online_paid_amount is not null then return 'refund'; end if;   -- already recorded and reported
    update public.payments set online_paid_amount = coalesce(p_amount, 0), online_channel = p_channel where payment_id = v.payment_id;
    perform public.notify_staff('payment', 'Online payment to refund', v.payment_code || ', PHP ' || coalesce(p_amount::text, 'unknown amount'),
      jsonb_build_object('payment_id', v.payment_id));
    perform public.audit('payment.online_refund', 'payments', v.payment_id::text,
      jsonb_build_object('xendit_id', p_xendit_id, 'amount', p_amount, 'status', v.status));
    return 'refund';
  end if;

  update public.payments
  set status = 'verified', verified_at = now(), receipt_no = public.next_receipt_no(),
      reference_no = p_xendit_id, online_channel = p_channel, online_paid_amount = p_amount
  where payment_id = v.payment_id;
  perform public.activate_payment_target(v.payment_id);
  perform public.notify(v.member_id, 'payment', 'Payment received', v.payment_code, jsonb_build_object('payment_id', v.payment_id));
  perform public.audit('payment.online', 'payments', v.payment_id::text, jsonb_build_object('xendit_id', p_xendit_id, 'channel', p_channel));
  return 'confirmed';
end $$;
revoke all on function public.confirm_online_payment(text, numeric, text) from public, anon, authenticated;
grant execute on function public.confirm_online_payment(text, numeric, text) to service_role;
