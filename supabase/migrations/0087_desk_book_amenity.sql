-- Plan 2026-11-17 Z1, Z2: the front desk books an amenity for a customer and takes the full price in cash at once.
-- book_amenity's body moves into book_amenity_for (not callable by anyone) so the member's and the desk's bookings share every rule:
-- slot grid, gym hours, clashes, coach, unavailable amenity, discount. The desk skips two rules a person standing at the counter
-- doesn't need: an active membership (Z1: someone just registered may book) and booking an hour ahead.
create function public.book_amenity_for(p_member uuid, p_desk boolean, p_amenity bigint, p_start timestamptz, p_minutes integer,
  p_method public.payment_method, p_coach uuid, p_code text)
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := p_member; v_end timestamptz; v_day date; v_open time; v_close time; v_hours int; v_percent int;
  v_am public.amenities; v_ua bigint; v_pass bigint; v_pay public.payments; v_res public.reservations; v_con text;
  v_total numeric; v_disc bigint; v_cut numeric; v_due numeric; v_down numeric;
begin
  if not p_desk and not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
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
  if not p_desk and p_start - interval '1 hour' <= now() then perform public.fail('too_soon', '22023'); end if;
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
  values (v_uid, p_amenity, p_coach, p_start, v_end, auth.uid(), v_ua, v_pay.payment_id, 'awaiting_payment', v_due, v_down) returning * into v_res;
  perform public.audit('reservation.request', 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'method', p_method,
                       'coach', p_coach, 'total', v_due, 'down_payment', v_down, 'discount', v_disc));
  return v_res;
end $$;
revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;

create or replace function public.book_amenity(p_amenity bigint, p_start timestamptz, p_minutes integer, p_method public.payment_method default 'qr',
  p_coach uuid default null, p_code text default null)
returns public.reservations
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  return public.book_amenity_for(auth.uid(), false, p_amenity, p_start, p_minutes, p_method, p_coach, p_code);
end $$;
revoke all on function public.book_amenity(bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;
grant execute on function public.book_amenity(bigint, timestamptz, integer, public.payment_method, uuid, text) to authenticated;

-- the desk: books for the member, then the whole price is the payment and is verified at once into the open drawer
create function public.desk_book_amenity(p_member uuid, p_amenity bigint, p_start timestamptz, p_minutes integer,
  p_coach uuid default null, p_code text default null)
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare v public.reservations;
begin
  perform public.require_permission('payments:record');
  if p_member is null or p_member = auth.uid() then perform public.fail('bad_input', '22023'); end if;
  if not exists (select 1 from public.members where user_id = p_member) then perform public.fail('not_found', '22023'); end if;
  v := public.book_amenity_for(p_member, true, p_amenity, p_start, p_minutes, 'cash', p_coach, p_code);
  if v.payment_id is null then return v; end if;   -- booked with a pass the member already has: nothing to pay
  update public.payments set amount = v.total_price, recorded_by = auth.uid(), expires_at = now() + interval '10 minutes'
    where payment_id = v.payment_id;
  update public.reservations set down_payment = total_price where reservation_id = v.reservation_id;
  perform public.verify_payment(v.payment_id, true);
  select * into v from public.reservations where reservation_id = v.reservation_id;
  -- verifying lost to a booking already confirmed for that time (settle_amenity_hold cancels it into a refund): refuse the whole thing,
  -- so the desk keeps no cash and no refund is made
  if v.status <> 'confirmed' then perform public.fail('slot_taken', 'P0002'); end if;
  return v;
end $$;
revoke all on function public.desk_book_amenity(uuid, bigint, timestamptz, integer, uuid, text) from public, anon, authenticated;
grant execute on function public.desk_book_amenity(uuid, bigint, timestamptz, integer, uuid, text) to authenticated;
