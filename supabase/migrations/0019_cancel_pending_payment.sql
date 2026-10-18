-- A member can withdraw their own membership or extension payment while nobody has verified it yet.
-- Verified payments are not touched here: those use the 24-hour cancel-and-refund request.
create function public.cancel_pending_payment(p_payment_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments;
begin
  perform public.require_active_user();
  select * into v_pay from public.payments
    where payment_id=p_payment_id and member_id=auth.uid() and kind in ('membership','renewal') for update;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_pay.status<>'pending' then perform public.fail('already_processed','P0002'); end if;
  update public.payments set status='rejected',rejected_reason='Cancelled by member' where payment_id=p_payment_id;
  perform public.cancel_payment_target(p_payment_id);
  perform public.audit('payment.cancel','payments',p_payment_id::text);
end $$;

revoke all on function public.cancel_pending_payment(bigint) from public,anon,authenticated;
grant execute on function public.cancel_pending_payment(bigint) to authenticated;
