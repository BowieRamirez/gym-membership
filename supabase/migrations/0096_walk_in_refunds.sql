-- Owner, 2026-11-18: the desk asks for a walk-in's refund with a reason, and the System admin approves or rejects it, then repays it as any refund.
--   Day pass: only on the day it was paid for; the check-in stays on record.
--   Walk-in amenity booking: 24 hours or more before the start, or later only when the gym is the one cancelling. Approving cancels it and frees the slot.

alter table public.refund_requests
  drop constraint refund_requests_kind_check,
  drop constraint refund_requests_target;
alter table public.refund_requests
  add constraint refund_requests_kind_check check (kind in ('membership', 'cancel', 'no_show', 'online', 'day_pass', 'walk_in_booking')),
  add constraint refund_requests_target check ((kind = 'membership') = (term_id is not null) and (kind in ('cancel', 'no_show', 'walk_in_booking')) = (reservation_id is not null));

-- the desk sees how the requests it made are going
create policy refund_requests_desk_walk_ins on public.refund_requests for select to authenticated
  using (public.is_active_user() and kind in ('day_pass', 'walk_in_booking') and public.has_permission('payments:record'));

create function public.request_walk_in_refund(p_payment_id bigint, p_reason text, p_gym_cancels boolean default false) returns public.refund_requests
language plpgsql security definer set search_path='' as $$
declare v_pay public.payments; v_res public.reservations; v_kind text; v_reason text := btrim(coalesce(p_reason, '')); v_req public.refund_requests;
begin
  perform public.require_permission('payments:record');
  if length(v_reason) not between 1 and 900 or p_gym_cancels is null then perform public.fail('bad_input', '22023'); end if;
  select * into v_pay from public.payments where payment_id = p_payment_id for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v_pay.status <> 'verified' or v_pay.amount <= 0 or v_pay.member_id is not null then perform public.fail('not_refundable', 'P0002'); end if;

  if v_pay.kind = 'walk_in' then
    v_kind := 'day_pass';
    if (v_pay.verified_at at time zone 'Asia/Manila')::date <> (now() at time zone 'Asia/Manila')::date then perform public.fail('refund_day_passed', '22023'); end if;
  elsif v_pay.kind = 'amenity' then
    v_kind := 'walk_in_booking';
    select * into v_res from public.reservations where payment_id = p_payment_id and member_id is null and status = 'confirmed' for update;
    if not found then perform public.fail('not_refundable', 'P0002'); end if;
    if v_res.starts_at <= now() then perform public.fail('already_started', 'P0002'); end if;
    if not p_gym_cancels and v_res.starts_at - interval '24 hours' < now() then perform public.fail('refund_too_late', '22023'); end if;
  else
    perform public.fail('not_refundable', 'P0002');
  end if;
  if exists (select 1 from public.refund_requests where payment_id = p_payment_id and kind <> 'online') then perform public.fail('already_requested', 'P0002'); end if;

  insert into public.refund_requests (kind, payment_id, reservation_id, member_id, amount, reason)
  values (v_kind, p_payment_id, v_res.reservation_id, null, v_pay.amount, case when p_gym_cancels then 'Gym is cancelling: ' else '' end || v_reason)
  returning * into v_req;
  perform public.notify_staff('payment', 'Walk-in refund to review', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req.refund_request_id));
  perform public.audit('refund.request', 'refund_requests', v_req.refund_request_id::text,
    jsonb_build_object('amount', v_req.amount, 'kind', v_kind, 'payment_id', p_payment_id, 'gym_cancels', p_gym_cancels));
  return v_req;
end $$;

create function public.decide_walk_in_refund(p_request_id bigint, p_approve boolean, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v public.refund_requests; v_pay public.payments;
begin
  perform public.require_permission('payments:void');
  if p_approve is null or length(btrim(coalesce(p_reason, ''))) not between 1 and 1000 then perform public.fail('bad_input', '22023'); end if;
  select * into v from public.refund_requests where refund_request_id = p_request_id and kind in ('day_pass', 'walk_in_booking');
  if not found then perform public.fail('not_found', '22023'); end if;
  select * into v_pay from public.payments where payment_id = v.payment_id for update;
  select * into v from public.refund_requests where refund_request_id = p_request_id for update;
  if v.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;
  if v_pay.status <> 'verified' then perform public.fail('not_refundable', 'P0002'); end if;
  if p_approve and v.reservation_id is not null then   -- asked in time counts: a booking that has started meanwhile is still refunded
    update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = auth.uid(), cancel_reason = 'Refund approved'
      where reservation_id = v.reservation_id and status = 'confirmed';
  end if;
  update public.refund_requests set status = case when p_approve then 'approved' else 'rejected' end,
    decided_by = auth.uid(), decided_at = now(), decision_reason = btrim(p_reason) where refund_request_id = p_request_id;
  perform public.audit('refund.decide', 'refund_requests', p_request_id::text, jsonb_build_object('approved', p_approve, 'reason', btrim(p_reason), 'amount', v.amount));
end $$;

-- the gym cancelling or closing refunds the booking itself: that replaces a walk-in request still open or already declined
do $$
declare v_def text; r record; v_insert text := $q$insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)$q$;
begin
  for r in select * from (values ('public.cancel_reservation(bigint,text)'::regprocedure), ('public.close_gym_day(date,text)'::regprocedure)) as t(fn) loop
    v_def := pg_get_functiondef(r.fn);
    if array_length(string_to_array(v_def, v_insert), 1) <> 2 then raise exception '0096: % lacks the refund insert', r.fn; end if;
    execute replace(v_def, v_insert, $q$delete from public.refund_requests where reservation_id = v.reservation_id and kind = 'walk_in_booking' and status in ('pending', 'rejected');
      $q$ || v_insert);
  end loop;
end $$;

revoke all on function public.request_walk_in_refund(bigint, text, boolean), public.decide_walk_in_refund(bigint, boolean, text),
  public.cancel_reservation(bigint, text), public.close_gym_day(date, text) from public, anon;
grant execute on function public.request_walk_in_refund(bigint, text, boolean), public.decide_walk_in_refund(bigint, boolean, text),
  public.cancel_reservation(bigint, text), public.close_gym_day(date, text) to authenticated;
