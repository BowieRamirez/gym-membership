-- Final review of plan 2026-11-17: a desk booking for a customer who holds a pass went through book_slots, which books for
-- auth.uid() (the desk employee). The desk now always takes the paid path; a member's own booking still uses their pass.
create or replace function public.book_amenity_for(p_member uuid, p_desk boolean, p_amenity bigint, p_start timestamptz, p_minutes integer,
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
  if not p_desk then   -- 0088: book_slots books for auth.uid(), so the desk always takes the paid path
  select ua.user_amenity_id into v_pass from public.user_amenities ua where ua.member_id = v_uid and ua.amenity_id = p_amenity and ua.status = 'active'
    and (ua.expires_at is null or ua.expires_at >= v_end) order by ua.is_session, ua.user_amenity_id limit 1;
  end if;
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
