-- Owner, 2026-11-17: Amenity status's "Book for a customer" books walk-ins (no account) instead of registering them. The desk takes
-- the full price in cash and reads out a receipt number the customer photographs. A member booked at the desk gets the same receipt
-- as a Front desk chat message, which is their reference.

-- a booking and its payment belong to a member, or to a walk-in's name
alter table public.reservations alter column member_id drop not null;
alter table public.reservations add column walk_in_name text;
alter table public.reservations add constraint reservations_who check (num_nonnulls(member_id, walk_in_name) = 1);
alter table public.refund_requests alter column member_id drop not null;   -- the gym cancelling a walk-in's booking (cancel, closed day)

alter table public.payments drop constraint payments_target_matches_kind;
alter table public.payments add constraint payments_target_matches_kind check (
  case kind
    when 'membership' then user_membership_package_id is not null and renewal_id is null and user_amenity_id is null
    when 'renewal'    then renewal_id is not null and user_membership_package_id is null and user_amenity_id is null
    when 'amenity'    then (user_amenity_id is not null or member_id is null) and user_membership_package_id is null and renewal_id is null
    when 'walk_in'    then num_nonnulls(user_membership_package_id, renewal_id, user_amenity_id) = 0
  end);
alter table public.payments drop constraint payments_payer;
alter table public.payments add constraint payments_payer check (
  (kind in ('walk_in', 'amenity') and member_id is null and walk_in_name is not null)
  or (kind <> 'walk_in' and member_id is not null and walk_in_name is null));

-- a walk-in's block has no member: 'mine' stays false, not null
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
                  'cooldown', public.plus_cooldown(r.ends_at), 'mine', r.member_id is not distinct from auth.uid(),
                  'id', case when r.member_id = auth.uid() then r.reservation_id end,
                  'pending', r.status = 'awaiting_payment') order by r.starts_at), '[]')
               from public.reservations r
               where (r.status = 'confirmed' or (r.status = 'awaiting_payment' and r.member_id = auth.uid()))
                 and tstzrange(r.starts_at, public.plus_cooldown(r.ends_at)) && tstzrange(v_from, v_to)));
end $$;

-- the desk books a walk-in: the same slot rules as book_amenity_for's paid path, paid in full in cash and confirmed at once
-- ponytail: the slot checks are repeated from book_amenity_for (no pass, discount, down payment or member here); change both together
create function public.desk_book_walk_in(p_name text, p_amenity bigint, p_start timestamptz, p_minutes integer)
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := auth.uid(); v_name text := btrim(p_name); v_end timestamptz; v_day date; v_open time; v_close time;
  v_am public.amenities; v_total numeric; v_pay public.payments; v_res public.reservations;
begin
  perform public.require_permission('payments:record');
  if coalesce(v_name, '') = '' or char_length(v_name) > 100 or p_amenity is null or p_start is null or p_minutes is null
     or p_minutes < 60 or p_minutes % 60 <> 0 then
    perform public.fail('bad_input', '22023');
  end if;
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

  v_total := v_am.price * (p_minutes / 60);   -- 0090: per hour
  insert into public.payments (payment_code, kind, method, status, amount, walk_in_name, recorded_by, verified_by, verified_at, receipt_no)
  values (public.generate_payment_code(), 'amenity', 'cash', 'verified', v_total, v_name, v_uid, v_uid, now(), public.next_receipt_no())
  returning * into v_pay;   -- payments_cash_shift (0050) puts it in the open drawer, or refuses
  begin
    insert into public.reservations (walk_in_name, amenity_id, starts_at, ends_at, created_by, payment_id, status, total_price, down_payment)
    values (v_name, p_amenity, p_start, v_end, v_uid, v_pay.payment_id, 'confirmed', v_total, v_total) returning * into v_res;
  exception when exclusion_violation then perform public.fail('slot_taken', 'P0002');
  end;
  perform public.audit('reservation.desk_walk_in', 'reservations', v_res.reservation_id::text,
    jsonb_build_object('amenity', p_amenity, 'starts_at', p_start, 'ends_at', v_end, 'payment_id', v_pay.payment_id, 'total', v_total));
  return v_res;
end $$;
revoke all on function public.desk_book_walk_in(text, bigint, timestamptz, integer) from public, anon, authenticated;
grant execute on function public.desk_book_walk_in(text, bigint, timestamptz, integer) to authenticated;

-- as in 0087, then the receipt goes to the member's Front desk chat (sent by the desk employee; their reference for the booking)
create or replace function public.desk_book_amenity(p_member uuid, p_amenity bigint, p_start timestamptz, p_minutes integer,
  p_coach uuid default null, p_code text default null)
returns public.reservations
language plpgsql security definer set search_path='' as $$
declare v public.reservations; v_receipt text; v_body text;
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
  if v.status <> 'confirmed' then perform public.fail('slot_taken', 'P0002'); end if;

  select receipt_no into v_receipt from public.payments where payment_id = v.payment_id;
  v_body := 'Your booking is confirmed: ' || public.session_words(v.starts_at, v.ends_at, v.amenity_id) || '. Paid PHP '
    || to_char(v.total_price, 'FM999,999,990.00') || ' in cash. Receipt ' || v_receipt || '. Show this message at the front desk.';
  insert into public.support_threads (member_id, queue) values (p_member, 'front_desk') on conflict (member_id, queue) do nothing;
  insert into public.support_messages (member_id, queue, sender_id, body) values (p_member, 'front_desk', auth.uid(), v_body);
  update public.support_threads set last_message_at = now() where member_id = p_member and queue = 'front_desk';
  perform public.notify(p_member, 'message', 'Message from the front desk', left(v_body, 80),
    jsonb_build_object('support_member', p_member::text, 'support_queue', 'front_desk'));
  return v;
end $$;
revoke all on function public.desk_book_amenity(uuid, bigint, timestamptz, integer, uuid, text) from public, anon, authenticated;
grant execute on function public.desk_book_amenity(uuid, bigint, timestamptz, integer, uuid, text) to authenticated;
