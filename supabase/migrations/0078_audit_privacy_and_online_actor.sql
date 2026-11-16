-- Cashier plan (docs/superpowers/plans/2026-11-16-cashier-walkin-logs.md), Phase 2.
-- Y4: the log keeps no member's network address. Staff addresses stay (D7, cleared after 90 days); a member's action stores none,
--     and the addresses members left before this migration are cleared now.
-- Y5: an online (Xendit) payment is logged as the member who paid, with their role and the method they used, instead of "System".

create or replace function public.audit(p_action text, p_entity text, p_entity_id text default null, p_details jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.audit_log (actor_id, action, entity, entity_id, details, ip_address, role)
	select auth.uid(), p_action, p_entity, p_entity_id, p_details,
		case when r is distinct from 'member' then public.request_ip() end, r
	from (select public.main_role(auth.uid()) as r) x;
$$;

update public.audit_log set ip_address = null where role = 'member' and ip_address is not null;

-- For actions the server takes on a person's behalf with no signed-in caller (the Xendit webhook): that person is the actor; no address.
create function public.audit_as(p_actor uuid, p_action text, p_entity text, p_entity_id text default null, p_details jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.audit_log (actor_id, action, entity, entity_id, details, ip_address, role)
	values (p_actor, p_action, p_entity, p_entity_id, p_details, null, public.main_role(p_actor));
$$;

-- 0077's confirm_online_payment, logging through audit_as with the method (channel) in the details
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
    perform public.audit_as(v.member_id, 'payment.online_refund', 'payments', v.payment_id::text,
      jsonb_build_object('xendit_id', p_xendit_id, 'amount', p_amount, 'channel', p_channel, 'status', v.status));
    return 'refund';
  end if;

  update public.payments
  set status = 'verified', verified_at = now(), receipt_no = public.next_receipt_no(),
      reference_no = p_xendit_id, online_channel = p_channel, online_paid_amount = p_amount
  where payment_id = v.payment_id;
  perform public.activate_payment_target(v.payment_id);
  perform public.notify(v.member_id, 'payment', 'Payment received', v.payment_code, jsonb_build_object('payment_id', v.payment_id));
  perform public.audit_as(v.member_id, 'payment.online', 'payments', v.payment_id::text, jsonb_build_object('xendit_id', p_xendit_id, 'channel', p_channel));
  return 'confirmed';
end $$;

revoke all on function public.audit(text, text, text, jsonb), public.audit_as(uuid, text, text, text, jsonb),
  public.confirm_online_payment(text, numeric, text) from public, anon, authenticated;
grant execute on function public.confirm_online_payment(text, numeric, text) to service_role;
