-- Phase 6 of the 2026-11-12 plan (owner decisions G13, G14, 2026-11-14), and amenity codes (G4, moved from Phase 2).
--
-- G13 (reverses 0052's unpaid hold): a booking is a request until its down payment is verified. The down payment is
-- gym_settings.down_payment_percent (50) of the price, by GCash (proof) or cash at the desk. A request holds no slot: the exclusion
-- constraints now cover confirmed bookings only, so of two paid requests for one slot the first verified wins and the other's down
-- payment is refunded at once through Refunds ("Slot taken"). The rest is paid at the desk (record_booking_balance). The member
-- cancelling 24 hours or more before the start gets the down payment back; later they may still cancel but the gym keeps it. The gym
-- cancelling always refunds it. Nobody cancels once the balance is paid (the desk voids that payment first).
-- G14: Solo or With a coach. A coach is free when they run the amenity (coach_amenities, 0052), have a scheduled shift over the whole
-- time, and have no confirmed booking or scheduled session then. With a coach adds the amenity's coach_fee to the price. A member with a
-- pass still books straight away (book_slots); with a coach the fee is then the balance at the desk.
-- G4: book_amenity takes a discount code; it comes off the price before the down payment (guard_payment_discount_item checks it fits).

alter table public.gym_settings add column down_payment_percent int not null default 50 check (down_payment_percent between 1 and 100);
alter table public.amenities add column coach_fee numeric(12,2) not null default 0 check (coach_fee >= 0);
grant update (coach_fee) on public.amenities to authenticated;
alter table public.reservations
  add column total_price numeric(12,2) not null default 0 check (total_price >= 0),
  add column down_payment numeric(12,2) not null default 0 check (down_payment >= 0 and down_payment <= total_price),
  add column balance_payment_id bigint unique references public.payments;

-- ---------- a request holds nothing: only confirmed bookings take a slot, the member's time and the coach ----------
alter table public.reservations drop constraint reservations_amenity_cooldown, drop constraint reservations_member_busy, drop constraint reservations_coach_busy;
alter table public.reservations
  add constraint reservations_amenity_cooldown exclude using gist (
    amenity_id with =, tstzrange(starts_at, public.plus_cooldown(ends_at)) with &&) where (status = 'confirmed'),
  add constraint reservations_member_busy exclude using gist (
    member_id with =, tstzrange(starts_at, ends_at) with &&) where (status = 'confirmed'),
  add constraint reservations_coach_busy exclude using gist (
    coach_id with =, tstzrange(starts_at, ends_at) with &&) where (status = 'confirmed' and coach_id is not null);

-- ---------- G14: who can coach a session ----------
create function public.coach_is_free(p_coach uuid, p_amenity bigint, p_start timestamptz, p_end timestamptz) returns boolean
language sql stable security definer set search_path='' as $$
  select exists (select 1 from public.coach_amenities ca join public.profiles p on p.id = ca.coach_id
                 where ca.coach_id = p_coach and ca.amenity_id = p_amenity and p.is_active)
     and exists (select 1 from public.shift_posts s where s.coach_id = p_coach and s.status = 'scheduled'
                 and s.starts_at <= p_start and s.ends_at >= p_end)
     and not exists (select 1 from public.reservations r where r.coach_id = p_coach and r.status = 'confirmed'
                     and tstzrange(r.starts_at, r.ends_at) && tstzrange(p_start, p_end))
     and not exists (select 1 from public.training_sessions t where t.coach_id = p_coach and t.status = 'scheduled'
                     and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(p_start, p_end));
$$;
revoke all on function public.coach_is_free(uuid, bigint, timestamptz, timestamptz) from public, anon, authenticated;

create function public.free_coaches(p_amenity bigint, p_start timestamptz, p_end timestamptz)
returns table(coach_id uuid, name text, avatar_path text)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_active_user();
  if p_amenity is null or p_start is null or p_end is null or p_end <= p_start then perform public.fail('bad_input', '22023'); end if;
  return query select ca.coach_id, coalesce(nullif(btrim(p.full_name), ''), p.username), p.avatar_path
    from public.coach_amenities ca join public.profiles p on p.id = ca.coach_id
    where ca.amenity_id = p_amenity and public.coach_is_free(ca.coach_id, p_amenity, p_start, p_end)
    order by 2;
end $$;
revoke all on function public.free_coaches(bigint, timestamptz, timestamptz) from public, anon;
grant execute on function public.free_coaches(bigint, timestamptz, timestamptz) to authenticated;

-- ---------- book: a request with a down payment ----------
drop function public.book_amenity(bigint, timestamptz, integer, public.payment_method);
create function public.book_amenity(p_amenity bigint, p_start timestamptz, p_minutes integer, p_method public.payment_method default 'qr',
  p_coach uuid default null, p_code text default null)
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := auth.uid(); v_end timestamptz; v_day date; v_open time; v_close time; v_hours int; v_percent int;
  v_am public.amenities; v_ua bigint; v_pass bigint; v_pay public.payments; v_res public.reservations; v_con text;
  v_total numeric; v_disc bigint; v_cut numeric; v_due numeric; v_down numeric;
begin
  perform public.require_active_user();
  if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
  if p_amenity is null or p_start is null or p_minutes is null or p_minutes not in (30, 60, 90, 120) or p_method is null or p_method not in ('qr', 'cash') then
    perform public.fail('bad_input', '22023');
  end if;
  perform public.expire_payments();   -- requests that ran out since the job last ran are cancelled first
  v_end := p_start + make_interval(mins => p_minutes);
  select * into v_am from public.amenities where amenity_id = p_amenity and is_active;
  if not found then perform public.fail('not_found', '22023'); end if;
  if p_coach is not null and not public.coach_is_free(p_coach, p_amenity, p_start, v_end) then perform public.fail('coach_not_free', 'P0002'); end if;

  -- a pass or an unused session still books straight away (book_slots checks everything); with a coach, the fee is paid at the desk
  select ua.user_amenity_id into v_pass from public.user_amenities ua where ua.member_id = v_uid and ua.amenity_id = p_amenity and ua.status = 'active'
    and (ua.expires_at is null or ua.expires_at >= v_end) order by ua.is_session, ua.user_amenity_id limit 1;
  if v_pass is not null then
    select * into v_res from public.book_slots((select jsonb_agg(jsonb_build_object('amenity', p_amenity, 'start', t))
      from generate_series(p_start, v_end - interval '30 minutes', interval '30 minutes') t));
    if p_coach is not null then
      begin
        update public.reservations set coach_id = p_coach, total_price = v_am.coach_fee, down_payment = 0,
          user_amenity_id = coalesce(user_amenity_id, v_pass)
          where reservation_id = v_res.reservation_id returning * into v_res;
      exception when exclusion_violation then perform public.fail('coach_not_free', 'P0002');
      end;
    end if;
    return v_res;
  end if;

  if extract(epoch from p_start)::bigint % 1800 <> 0 then perform public.fail('off_grid', '22023'); end if;
  if p_start <= now() then perform public.fail('past_slot', '22023'); end if;
  if p_start - interval '1 hour' <= now() then perform public.fail('too_soon', '22023'); end if;
  v_day := (p_start at time zone 'Asia/Manila')::date;
  if v_day > (now() at time zone 'Asia/Manila')::date + 14 then perform public.fail('too_far_ahead', '22023'); end if;
  select opens_at, closes_at, payment_expiry_hours, down_payment_percent into v_open, v_close, v_hours, v_percent from public.gym_settings where id = 1;
  if p_start < (v_day + v_open) at time zone 'Asia/Manila' or v_end > (v_day + v_close) at time zone 'Asia/Manila' then
    perform public.fail('outside_gym_hours', '22023');
  end if;
  if v_am.status = 'unavailable' and (v_am.back_at is null or v_am.back_at > p_start) then perform public.fail('amenity_unavailable', 'P0002'); end if;
  if exists (select 1 from public.training_sessions t where t.member_id = v_uid and t.status = 'scheduled'
             and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(p_start, v_end)) then
    perform public.fail('member_busy', 'P0002');
  end if;

  v_total := v_am.price + case when p_coach is not null then v_am.coach_fee else 0 end;
  select d.discount_id, d.discount_amount, d.amount into v_disc, v_cut, v_due from public.apply_discount(v_uid, p_code, v_total) d;
  v_down := round(v_due * v_percent / 100.0, 2);
  insert into public.user_amenities (member_id, amenity_id) values (v_uid, p_amenity) returning user_amenity_id into v_ua;
  insert into public.payments (payment_code, kind, method, amount, member_id, user_amenity_id, expires_at, discount_id, discount_amount)
  values (public.generate_payment_code(), 'amenity', p_method, v_down, v_uid, v_ua,
          least(now() + make_interval(hours => v_hours), p_start - interval '1 hour'), v_disc, v_cut)
  returning * into v_pay;
  insert into public.reservations (member_id, amenity_id, coach_id, starts_at, ends_at, created_by, user_amenity_id, payment_id, status, total_price, down_payment)
  values (v_uid, p_amenity, p_coach, p_start, v_end, v_uid, v_ua, v_pay.payment_id, 'awaiting_payment', v_due, v_down) returning * into v_res;
  perform public.audit('reservation.request', 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'method', p_method,
                       'coach', p_coach, 'total', v_due, 'down_payment', v_down, 'discount', v_disc));
  return v_res;
end $$;
revoke all on function public.book_amenity(bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon;
grant execute on function public.book_amenity(bigint, timestamptz, integer, public.payment_method, uuid, text) to authenticated;

-- ---------- verifying the down payment confirms the booking, unless someone else's was verified first ----------
create or replace function public.settle_amenity_hold() returns trigger
language plpgsql security definer set search_path='' as $$
declare v public.reservations; v_pay public.payments; v_con text; v_why text; v_req bigint;
begin
  if new.status = old.status then return new; end if;
  select * into v from public.reservations where user_amenity_id = new.user_amenity_id and status = 'awaiting_payment' for update;
  if not found then return new; end if;
  if new.status = 'active' then
    select * into v_pay from public.payments where payment_id = v.payment_id;
    if v_pay.expires_at <= now() then perform public.fail('payment_expired', 'P0002'); end if;
    begin
      update public.reservations set status = 'confirmed' where reservation_id = v.reservation_id;
    exception when exclusion_violation then
      get stacked diagnostics v_con = constraint_name;
      v_why := case v_con when 'reservations_coach_busy' then 'Coach not free' when 'reservations_member_busy' then 'You have another booking then' else 'Slot taken' end;
      update public.reservations set status = 'cancelled', cancelled_at = now(), cancel_reason = v_why where reservation_id = v.reservation_id;
      new.status := 'cancelled';
      insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)
      values ('cancel', v.reservation_id, v.payment_id, v.member_id, v_pay.amount, v_why, 'approved', auth.uid(), now(),
              v_why || ': another booking''s down payment was verified first. Down payment refunded.')
      returning refund_request_id into v_req;
      perform public.notify(v.member_id, 'amenity', 'Your slot was taken: down payment refunded',
        v_why || ', ' || to_char(v.starts_at at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM'), jsonb_build_object('reservation_id', v.reservation_id));
      perform public.notify_staff('payment', 'Booking refund to repay', v_pay.payment_code, jsonb_build_object('refund_request_id', v_req));
      perform public.audit('refund.request', 'refund_requests', v_req::text, jsonb_build_object('amount', v_pay.amount, 'reservation_id', v.reservation_id, 'kind', 'slot_taken'));
      return new;
    end;
    if new.is_session then new.status := 'expired'; end if;   -- the session is this booking
    perform public.notify(v.member_id, 'amenity', 'Booking confirmed',
      to_char(v.starts_at at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM'), jsonb_build_object('reservation_id', v.reservation_id));
  elsif new.status = 'cancelled' then
    update public.reservations set status = 'cancelled', cancelled_at = now(), cancel_reason = 'Not paid' where reservation_id = v.reservation_id;
  end if;
  return new;
end $$;

-- ---------- the balance, at the desk ----------
create function public.record_booking_balance(p_reservation_id bigint, p_method public.payment_method, p_reference text default null)
returns public.payments
language plpgsql security definer set search_path='' as $$
declare v public.reservations; v_pay public.payments;
begin
  perform public.require_permission('payments:record');
  if p_method is null or p_method not in ('cash', 'gcash') or (p_method = 'gcash' and coalesce(btrim(p_reference), '') = '') then
    perform public.fail('bad_input', '22023');
  end if;
  select * into v from public.reservations where reservation_id = p_reservation_id for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.status <> 'confirmed' then perform public.fail('not_confirmed', 'P0002'); end if;
  if v.balance_payment_id is not null or v.total_price - v.down_payment <= 0 or v.user_amenity_id is null then
    perform public.fail('already_processed', 'P0002');
  end if;
  insert into public.payments (payment_code, kind, method, amount, member_id, user_amenity_id, reference_no, recorded_by, status, verified_by, verified_at, receipt_no)
  values (public.generate_payment_code(), 'amenity', p_method, v.total_price - v.down_payment, v.member_id, v.user_amenity_id,
          nullif(btrim(p_reference), ''), auth.uid(), 'verified', auth.uid(), now(), public.next_receipt_no())
  returning * into v_pay;
  update public.reservations set balance_payment_id = v_pay.payment_id where reservation_id = p_reservation_id;
  perform public.audit('reservation.balance', 'reservations', p_reservation_id::text,
    jsonb_build_object('payment_id', v_pay.payment_id, 'amount', v_pay.amount, 'method', p_method));
  return v_pay;
end $$;
revoke all on function public.record_booking_balance(bigint, public.payment_method, text) from public, anon;
grant execute on function public.record_booking_balance(bigint, public.payment_method, text) to authenticated;

-- ---------- cancel: the down payment back 24 hours or more before; the gym cancelling always refunds ----------
create or replace function public.cancel_reservation(p_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations; v_pay public.payments; v_req bigint; v_mine boolean; v_refund boolean;
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
    v_refund := not v_mine or v.starts_at - interval '24 hours' >= now();
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

-- ---------- the grid: confirmed bookings are taken; your own requests show to you ----------
create or replace function public.amenity_grid(p_day date) returns jsonb
language plpgsql stable security definer set search_path='' as $$
declare v_open time; v_close time; v_from timestamptz; v_to timestamptz;
begin
  perform public.require_active_user();
  if p_day is null then perform public.fail('bad_input', '22023'); end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  v_from := p_day::timestamp at time zone 'Asia/Manila'; v_to := (p_day + 1)::timestamp at time zone 'Asia/Manila';
  return jsonb_build_object(
    'now', now(),
    'opens', (p_day + v_open) at time zone 'Asia/Manila',
    'closes', (p_day + v_close) at time zone 'Asia/Manila',
    'amenities', (select coalesce(jsonb_agg(jsonb_build_object('id', a.amenity_id, 'name', a.name, 'status', a.status, 'back', a.back_at) order by a.name), '[]')
                  from public.amenities a where a.is_active and a.published_at is not null),
    'blocks', (select coalesce(jsonb_agg(jsonb_build_object('amenity', r.amenity_id, 'starts', r.starts_at, 'ends', r.ends_at,
                  'cooldown', public.plus_cooldown(r.ends_at), 'mine', r.member_id = auth.uid(),
                  'id', case when r.member_id = auth.uid() then r.reservation_id end,
                  'pending', r.status = 'awaiting_payment') order by r.starts_at), '[]')
               from public.reservations r
               where (r.status = 'confirmed' or (r.status = 'awaiting_payment' and r.member_id = auth.uid()))
                 and tstzrange(r.starts_at, public.plus_cooldown(r.ends_at)) && tstzrange(v_from, v_to)));
end $$;
