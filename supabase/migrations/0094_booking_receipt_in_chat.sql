-- Owner, 2026-11-18: a member's own amenity booking also gets its receipt in their Front desk chat, as a desk booking has since 0091.
-- The message goes out where every paid member booking is confirmed: settle_amenity_hold (0064), when its payment is verified
-- (Xendit, or the desk taking the cash). desk_book_amenity reaches the same place, so its own message (0091) is removed here.
-- Sender: the staff member who confirmed it, or, when Xendit confirms with nobody signed in, the oldest active admin. A member never
-- sees which staff member wrote (chat_messages, 0051); the text says it is automatic. A sender is needed: unread counts compare it.

create function public.send_booking_receipt(p_reservation_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v public.reservations; p public.payments; v_from uuid; v_body text;
begin
  select * into v from public.reservations where reservation_id = p_reservation_id;
  select * into p from public.payments where payment_id = v.payment_id;
  if v.member_id is null or p.receipt_no is null then return; end if;   -- walk-ins get theirs on screen; no paid payment, no receipt
  v_from := coalesce(auth.uid(), (select ur.user_id from public.user_roles ur join public.roles r on r.role_id = ur.role_id
                                  join public.profiles pr on pr.id = ur.user_id
                                  where r.name = 'admin' and pr.is_active order by pr.created_at limit 1));
  if v_from is null or v_from = v.member_id then return; end if;
  v_body := 'Receipt (automatic). Your booking is confirmed: ' || public.session_words(v.starts_at, v.ends_at, v.amenity_id)
    || '. Paid PHP ' || to_char(p.amount, 'FM999,999,990.00')
    || case p.method when 'cash' then ' in cash' when 'gcash' then ' by GCash' else ' online' end
    || '. Receipt ' || p.receipt_no
    || case when v.total_price > p.amount then '. Balance PHP ' || to_char(v.total_price - p.amount, 'FM999,999,990.00') || ' at the front desk' else '' end
    || '. Show this message at the front desk.';
  insert into public.support_threads (member_id, queue) values (v.member_id, 'front_desk') on conflict (member_id, queue) do nothing;
  insert into public.support_messages (member_id, queue, sender_id, body) values (v.member_id, 'front_desk', v_from, v_body);
  update public.support_threads set last_message_at = now() where member_id = v.member_id and queue = 'front_desk';
  perform public.notify(v.member_id, 'message', 'Message from the front desk', left(v_body, 80),
    jsonb_build_object('support_member', v.member_id::text, 'support_queue', 'front_desk'));
exception when others then
  raise warning 'booking receipt not sent for reservation %: %', p_reservation_id, sqlerrm;   -- never blocks confirming the booking
end $$;

-- settle_amenity_hold: once the booking is confirmed, its receipt goes to the chat
do $$
declare v_def text; v_old text := $q$    if new.is_session then new.status := 'expired'; end if;   -- the session is this booking$q$;
begin
  v_def := pg_get_functiondef('public.settle_amenity_hold()'::regprocedure);
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then raise exception '0094: settle_amenity_hold lacks "%"', v_old; end if;
  execute replace(v_def, v_old, v_old || $q$
    perform public.send_booking_receipt(v.reservation_id);   -- 0094$q$);
end $$;

-- desk_book_amenity as in 0091, without its own message (settle_amenity_hold sends it now)
create or replace function public.desk_book_amenity(p_member uuid, p_amenity bigint, p_start timestamptz, p_minutes integer,
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
  perform public.verify_payment(v.payment_id, true);   -- confirms the booking and sends the receipt to the chat (0094)
  select * into v from public.reservations where reservation_id = v.reservation_id;
  if v.status <> 'confirmed' then perform public.fail('slot_taken', 'P0002'); end if;
  return v;
end $$;

revoke all on function public.send_booking_receipt(bigint), public.settle_amenity_hold() from public, anon, authenticated;
revoke all on function public.desk_book_amenity(uuid, bigint, timestamptz, integer, uuid, text) from public, anon, authenticated;
grant execute on function public.desk_book_amenity(uuid, bigint, timestamptz, integer, uuid, text) to authenticated;
