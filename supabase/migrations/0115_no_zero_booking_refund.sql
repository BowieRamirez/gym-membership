-- B5 (2026-11-20): cancelling or closing a gym day on a ₱0 booking (e.g. 100% discount code) must not create a ₱0 "To repay" refund or alert staff.

create or replace function public.cancel_reservation(p_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations; v_pay public.payments; v_req bigint; v_mine boolean; v_refund boolean := false;
begin
  perform public.require_active_user();
  select * into v from public.reservations where reservation_id = p_id for update;
  if not found or (v.member_id <> v_uid and v.coach_id is distinct from v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  v_mine := v.member_id = v_uid;
  if not v_mine and length(btrim(coalesce(p_reason, ''))) not between 1 and 500 then perform public.fail('bad_reason', '22023'); end if;
  if v.status not in ('confirmed', 'awaiting_payment') then perform public.fail('already_processed', 'P0002'); end if;
  if v.starts_at <= now() then perform public.fail('already_started', 'P0002'); end if;
  if v.balance_payment_id is not null then perform public.fail('balance_paid', 'P0002'); end if;
  if v.payment_id is not null then select * into v_pay from public.payments where payment_id = v.payment_id for update; end if;

  update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid, cancel_reason = nullif(btrim(p_reason), '')
    where reservation_id = p_id;
  if v.status = 'awaiting_payment' then
    update public.payments set status = 'rejected', rejected_reason = 'Booking cancelled' where payment_id = v.payment_id and status = 'pending';
    perform public.cancel_payment_target(v.payment_id);
  elsif v_pay.status = 'verified' then
    v_refund := coalesce(v_pay.amount, 0) > 0 and (not v_mine or v.starts_at - interval '24 hours' >= now());
    if v_refund then
      insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
      values ('cancel', p_id, v_pay.payment_id, v.member_id, v_pay.amount, coalesce(nullif(btrim(p_reason), ''), 'Booking cancelled'),
              'approved', v_uid, now(), case when v_mine then 'Cancelled 24 hours or more before the start: down payment refunded'
                                             else 'Cancelled by the gym: down payment refunded' end)
      returning refund_request_id into v_req;
      perform public.notify_staff('payment', 'Booking refund to repay', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req));
      perform public.audit('refund.request', 'refund_requests', v_req::text, jsonb_build_object('amount', v_pay.amount, 'reservation_id', p_id, 'kind', 'cancel'));
    end if;
  elsif v.user_amenity_id is not null then   -- a session used by book_slots comes back; a timed pass was never used up
    update public.user_amenities set status = 'active' where user_amenity_id = v.user_amenity_id and is_session;
  end if;
  if not v_mine then
    perform public.notify(v.member_id, 'amenity', 'Booking cancelled', btrim(p_reason), jsonb_build_object('reservation_id', p_id));
  end if;
  perform public.audit('reservation.cancel', 'reservations', p_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), ''), 'refund', v_refund));
end $$;

revoke all on function public.cancel_reservation(bigint, text) from public, anon, authenticated;
grant execute on function public.cancel_reservation(bigint, text) to authenticated;

create or replace function public.close_gym_day(p_date date, p_reason text) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid(); v_reason text := btrim(coalesce(p_reason, '')); v_why text;
  v public.reservations; v_pay public.payments; v_bal numeric; v_refund numeric; v_req bigint; s record;
begin
  perform public.require_gym_admin();
  perform public.require_open_or_future(p_date);
  if char_length(v_reason) not between 1 and 200 then perform public.fail('bad_reason', '22023'); end if;
  insert into public.gym_closures (closed_on, reason, closed_by) values (p_date, v_reason, v_uid) on conflict do nothing;
  if not found then perform public.fail('already_processed', 'P0002'); end if;
  v_why := 'Gym closed: ' || v_reason;

  for v in select * from public.reservations r where r.status in ('confirmed', 'awaiting_payment')
             and (r.starts_at at time zone 'Asia/Manila')::date = p_date and r.ends_at > now() for update loop
    update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid, cancel_reason = v_why
      where reservation_id = v.reservation_id;
    v_pay := null; v_bal := 0;
    if v.payment_id is not null then select * into v_pay from public.payments where payment_id = v.payment_id for update; end if;
    if v.balance_payment_id is not null then select amount into v_bal from public.payments where payment_id = v.balance_payment_id; end if;
    if v.status = 'awaiting_payment' then
      update public.payments set status = 'rejected', rejected_reason = 'Booking cancelled' where payment_id = v.payment_id and status = 'pending';
      perform public.cancel_payment_target(v.payment_id);
    elsif v_pay.status = 'verified' or v.balance_payment_id is not null then
      v_refund := coalesce(case when v_pay.status = 'verified' then v_pay.amount else 0 end, 0) + coalesce(v_bal, 0);
      if v_refund > 0 then
        insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
        values ('cancel', v.reservation_id, case when v_pay.status = 'verified' then v_pay.payment_id else v.balance_payment_id end, v.member_id,
                v_refund, v_why, 'approved', v_uid, now(), 'Cancelled by the gym: refunded')
        returning refund_request_id into v_req;
        perform public.notify_staff('payment', 'Booking refund to repay', v_why, jsonb_build_object('refund_request_id', v_req));
      end if;
    elsif v.user_amenity_id is not null then
      update public.user_amenities set status = 'active' where user_amenity_id = v.user_amenity_id and is_session;
    end if;
    perform public.notify(v.member_id, 'amenity', 'Booking cancelled', v_why, jsonb_build_object('reservation_id', v.reservation_id));
  end loop;

  for s in select * from public.training_sessions t where t.status = 'scheduled'
             and (t.scheduled_start at time zone 'Asia/Manila')::date = p_date and t.scheduled_end > now() for update loop
    update public.training_sessions set status = 'cancelled', notes = v_why where training_session_id = s.training_session_id;
    update public.time_requests set status = 'cancelled' where training_session_id = s.training_session_id and status = 'pending';
    perform public.notify(s.member_id, 'system', 'A training session was cancelled', v_why, jsonb_build_object('training_session_id', s.training_session_id));
    perform public.notify(s.coach_id, 'system', 'A training session was cancelled', v_why, jsonb_build_object('training_session_id', s.training_session_id));
  end loop;

  for s in select * from public.shift_posts p where p.status = 'scheduled'
             and (p.starts_at at time zone 'Asia/Manila')::date = p_date and p.ends_at > now() for update loop
    update public.shift_posts set status = 'cancelled' where shift_post_id = s.shift_post_id;
    perform public.notify(s.coach_id, 'system', 'Shift cancelled', v_why, jsonb_build_object('shift_post_id', s.shift_post_id));
  end loop;

  for s in select * from public.time_requests q where q.status in ('pending', 'proposed')
             and (case when q.status = 'proposed' then q.proposed_start else q.requested_start end at time zone 'Asia/Manila')::date = p_date for update loop
    update public.time_requests set status = 'rejected', decline_reason = v_why, responded_at = now() where time_request_id = s.time_request_id;
    perform public.notify(s.member_id, 'time_request', 'Session request declined', v_why, jsonb_build_object('time_request_id', s.time_request_id));
  end loop;

  perform public.post_announcement(format('The gym is closed on %s: %s.', to_char(p_date, 'FMDay, FMDD Mon'), rtrim(v_reason, '.')), 'everyone');
  perform public.audit('gym.close', 'gym_closures', p_date::text, jsonb_build_object('reason', v_reason));
end $$;

revoke all on function public.close_gym_day(date, text) from public, anon, authenticated;
grant execute on function public.close_gym_day(date, text) to authenticated;

-- Close any approved ₱0 cancel refunds already created before this fix (e.g. 24, 25, 27) without touching cash drawers.
update public.refund_requests
set status = 'paid',
    paid_by = coalesce(decided_by, (select ur.user_id from public.user_roles ur join public.roles r on r.role_id = ur.role_id where r.name = 'admin' limit 1)),
    paid_at = now(),
    repayment_method = 'gcash',
    repayment_reference = 'Nothing to repay (₱0)'
where kind = 'cancel' and status = 'approved' and amount = 0;
