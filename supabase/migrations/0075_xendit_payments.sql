-- Xendit plan (docs/superpowers/plans/2026-11-16-xendit-payments.md, X1-X15): members pay online through Xendit's hosted checkout
-- instead of sending a GCash proof. The Edge Function xendit-checkout saves the checkout on the pending qr payment; xendit-webhook
-- confirms it, which verifies and activates exactly as the desk's approval does. Both functions here are service_role only.

-- ---------- the checkout on the payment ----------
alter table public.payments
  add column xendit_id text unique,
  add column xendit_url text,
  add column online_channel text,
  add column online_paid_amount numeric(10,2);
comment on column public.payments.xendit_id is 'Xendit checkout id (0075); also the reference_no once paid';

-- an online payment is verified by Xendit, not by a staff member
alter table public.payments drop constraint payments_verified_complete;
alter table public.payments add constraint payments_verified_complete check (
  status not in ('verified', 'voided')
  or ((verified_by is not null or xendit_id is not null) and verified_at is not null and receipt_no is not null));

-- the first checkout saved stays (X8): a double tap can't swap the link the member is already paying
create function public.save_online_checkout(p_payment_id bigint, p_xendit_id text, p_url text) returns void
language plpgsql security definer set search_path='' as $$
begin
  update public.payments set xendit_id = p_xendit_id, xendit_url = p_url
  where payment_id = p_payment_id and status = 'pending' and method = 'qr' and xendit_id is null;
end $$;

-- 'confirmed', 'already' (Xendit retries) or 'refund' (X9: paid but no longer payable, or the wrong amount; staff refund it)
create function public.confirm_online_payment(p_xendit_id text, p_amount numeric, p_channel text default null) returns text
language plpgsql security definer set search_path='' as $$
declare v public.payments;
begin
  select * into v from public.payments where xendit_id = p_xendit_id for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.status = 'verified' and v.reference_no = p_xendit_id then return 'already'; end if;

  if v.status <> 'pending' or v.expires_at <= now() or p_amount <> v.amount then
    update public.payments set online_paid_amount = p_amount, online_channel = p_channel where payment_id = v.payment_id;
    perform public.notify_staff('payment', 'Online payment to refund', v.payment_code || ', PHP ' || p_amount,
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

revoke all on function public.save_online_checkout(bigint, text, text), public.confirm_online_payment(text, numeric, text)
  from public, anon, authenticated;
grant execute on function public.save_online_checkout(bigint, text, text), public.confirm_online_payment(text, numeric, text)
  to service_role;

-- A slot lost to someone else's earlier payment is refunded by settle_amenity_hold (0064) with decided_by = auth.uid(). Paid online,
-- there is no signed-in staff member: the refund is decided by the system, with its time but no person.
alter table public.refund_requests drop constraint refund_requests_check;
alter table public.refund_requests add constraint refund_requests_check check (
  (status = 'pending' and decided_by is null and decided_at is null) or (status <> 'pending' and decided_at is not null));

-- ---------- X11: once a Xendit checkout is open, only Xendit approves it; the desk can still reject ----------
alter function public.verify_payment(bigint, boolean, text) rename to verify_payment_before_xendit;
create function public.verify_payment(p_payment_id bigint, p_approve boolean, p_reason text default null) returns void
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_permission('payments:verify');
  if p_approve and exists (select 1 from public.payments where payment_id = p_payment_id and status = 'pending'
                           and xendit_id is not null and proof_path is null) then
    perform public.fail('pay_online', 'P0002');
  end if;
  perform public.verify_payment_before_xendit(p_payment_id, p_approve, p_reason);
end $$;
revoke all on function public.verify_payment_before_xendit(bigint, boolean, text) from public, anon, authenticated;
revoke all on function public.verify_payment(bigint, boolean, text) from public, anon, authenticated;
grant execute on function public.verify_payment(bigint, boolean, text) to authenticated;

-- ---------- X2: no new proofs; old ones stay approvable and viewable (X13) ----------
revoke all on function public.submit_payment_proof(bigint, text, text) from public, anon, authenticated;
