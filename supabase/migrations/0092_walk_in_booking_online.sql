-- Owner, 2026-11-17: a walk-in's desk booking may also be paid online through Xendit, as the Cashier's Day pass can (0080).
-- With p_online the desk records a PENDING online (qr) payment for up to 30 minutes and an awaiting_payment booking; Xendit's checkout
-- opens on the desk PC (xendit-checkout already serves payments the desk recorded); confirm_online_payment then confirms the booking.
-- A walk-in has no user_amenities row, so settle_amenity_hold (0064) never sees it: confirm_walk_in_booking does its part here.

drop function public.desk_book_walk_in(text, bigint, timestamptz, integer);
create function public.desk_book_walk_in(p_name text, p_amenity bigint, p_start timestamptz, p_minutes integer, p_online boolean default false)
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := auth.uid(); v_name text := btrim(p_name); v_end timestamptz; v_day date; v_open time; v_close time;
  v_am public.amenities; v_total numeric; v_pay public.payments; v_res public.reservations;
begin
  perform public.require_permission('payments:record');
  if coalesce(v_name, '') = '' or char_length(v_name) > 100 or p_amenity is null or p_start is null or p_minutes is null
     or p_minutes < 60 or p_minutes % 60 <> 0 or p_online is null then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.expire_payments();   -- online holds that ran out are cancelled first
  v_end := p_start + make_interval(mins => p_minutes);
  select * into v_am from public.amenities where amenity_id = p_amenity and is_active;
  if not found then perform public.fail('not_found', '22023'); end if;
  if extract(epoch from p_start)::bigint % 1800 <> 0 then perform public.fail('off_grid', '22023'); end if;
  if p_start <= now() then perform public.fail('past_slot', '22023'); end if;
  v_day := (p_start at time zone 'Asia/Manila')::date;
  if v_day > (now() at time zone 'Asia/Manila')::date + 14 then perform public.fail('too_far_ahead', '22023'); end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  if p_start < (v_day + v_open) at time zone 'Asia/Manila' or v_end > (v_day + v_close) at time zone 'Asia/Manila' then
    perform public.fail('outside_gym_hours', '22023');
  end if;
  if v_am.status = 'unavailable' and (v_am.back_at is null or v_am.back_at > p_start) then perform public.fail('amenity_unavailable', 'P0002'); end if;
  -- an online payment holds nothing, so a slot already booked is refused before the customer is asked to pay
  if exists (select 1 from public.reservations r where r.amenity_id = p_amenity and r.status = 'confirmed'
             and tstzrange(r.starts_at, public.plus_cooldown(r.ends_at)) && tstzrange(p_start, public.plus_cooldown(v_end))) then
    perform public.fail('slot_taken', 'P0002');
  end if;

  v_total := v_am.price * (p_minutes / 60);   -- 0090: per hour
  if p_online then
    insert into public.payments (payment_code, kind, method, status, amount, walk_in_name, recorded_by, expires_at)
    values (public.generate_payment_code(), 'amenity', 'qr', 'pending', v_total, v_name, v_uid, least(now() + interval '30 minutes', p_start))
    returning * into v_pay;
  else
    insert into public.payments (payment_code, kind, method, status, amount, walk_in_name, recorded_by, verified_by, verified_at, receipt_no)
    values (public.generate_payment_code(), 'amenity', 'cash', 'verified', v_total, v_name, v_uid, v_uid, now(), public.next_receipt_no())
    returning * into v_pay;   -- payments_cash_shift (0050) puts it in the open drawer, or refuses
  end if;
  begin
    insert into public.reservations (walk_in_name, amenity_id, starts_at, ends_at, created_by, payment_id, status, total_price, down_payment)
    values (v_name, p_amenity, p_start, v_end, v_uid, v_pay.payment_id, case when p_online then 'awaiting_payment' else 'confirmed' end, v_total, v_total)
    returning * into v_res;
  exception when exclusion_violation then perform public.fail('slot_taken', 'P0002');
  end;
  perform public.audit(case when p_online then 'reservation.desk_walk_in_online' else 'reservation.desk_walk_in' end, 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'total', v_total));
  return v_res;
end $$;

-- Xendit confirmed a walk-in's online payment: the booking is confirmed, or, if the slot went to someone else meanwhile, cancelled with
-- an approved refund for the desk to repay (as settle_amenity_hold does for members; the desk employee who booked it is the decider)
create function public.confirm_walk_in_booking(p_payment_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v public.reservations; v_pay public.payments; v_req bigint;
begin
  select * into v from public.reservations where payment_id = p_payment_id and member_id is null and status = 'awaiting_payment' for update;
  if not found then return; end if;
  begin
    update public.reservations set status = 'confirmed' where reservation_id = v.reservation_id;
  exception when exclusion_violation then
    select * into v_pay from public.payments where payment_id = p_payment_id;
    update public.reservations set status = 'cancelled', cancelled_at = now(), cancel_reason = 'Slot taken' where reservation_id = v.reservation_id;
    insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
    values ('cancel', v.reservation_id, p_payment_id, null, v_pay.amount, 'Slot taken', 'approved', v.created_by, now(),
            'Slot taken: another booking was paid first. Refund the walk-in ' || v.walk_in_name || '.')
    returning refund_request_id into v_req;
    perform public.notify_staff('payment', 'Booking refund to repay', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req));
  end;
end $$;

-- an online walk-in booking whose payment expired or was cancelled at the desk is cancelled with it
create or replace function public.cancel_payment_target(p_payment_id bigint) returns void
language plpgsql security definer set search_path = '' as $$
declare v_pay public.payments;
begin
  select * into v_pay from public.payments where payment_id = p_payment_id;
  update public.user_membership_packages set status = 'cancelled' where user_membership_package_id = v_pay.user_membership_package_id and status = 'pending';
  update public.membership_renewals set status = 'cancelled' where renewal_id = v_pay.renewal_id and status = 'pending';
  update public.user_amenities set status = 'cancelled' where user_amenity_id = v_pay.user_amenity_id and status = 'pending';
  update public.reservations set status = 'cancelled', cancelled_at = now(), cancel_reason = 'Not paid'
    where payment_id = p_payment_id and member_id is null and status = 'awaiting_payment';   -- 0092
end $$;

do $$
declare v_def text; r record;
begin
  v_def := pg_get_functiondef('public.confirm_online_payment(text,numeric,text)'::regprocedure);
  for r in select * from (values
    ($q$  perform public.activate_payment_target(v.payment_id);$q$,
     $q$  perform public.activate_payment_target(v.payment_id);
  perform public.confirm_walk_in_booking(v.payment_id);   -- 0092$q$)
  ) as t(old_text, new_text) loop
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0092: confirm_online_payment lacks "%"', r.old_text; end if;
    v_def := replace(v_def, r.old_text, r.new_text);
  end loop;
  execute v_def;
end $$;

revoke all on function public.desk_book_walk_in(text, bigint, timestamptz, integer, boolean), public.confirm_walk_in_booking(bigint),
  public.cancel_payment_target(bigint), public.confirm_online_payment(text, numeric, text) from public, anon, authenticated;
grant execute on function public.desk_book_walk_in(text, bigint, timestamptz, integer, boolean) to authenticated;
grant execute on function public.confirm_online_payment(text, numeric, text) to service_role;
