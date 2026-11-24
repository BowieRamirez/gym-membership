-- Owner, 2026-11-25 (after the live test): an unpaid online booking held nothing, so two people could book and pay for the same hour and
-- the second got a "Slot taken" refund. An unpaid booking now holds its slot for 15 minutes: every "is it free" check counts a booking
-- that is paid or still inside its hold. After the hold the slot is free again; a late payer is still refunded ("Slot taken", 0064).

alter table public.reservations add column held_until timestamptz;

-- ponytail: a fixed 15 minutes; move it to gym_settings if the owner wants to change it from the app
create function public.hold_unpaid_booking() returns trigger language plpgsql set search_path = '' as $$
begin
  if new.status = 'awaiting_payment' then new.held_until := now() + interval '15 minutes'; end if;
  return new;
end $$;
create trigger reservations_hold_unpaid before insert on public.reservations
  for each row execute function public.hold_unpaid_booking();

create function public.reservation_holds_slot(r public.reservations) returns boolean
language sql stable set search_path = '' as $$
  select r.status = 'confirmed' or (r.status = 'awaiting_payment' and r.held_until > now())
$$;

do $$
declare v_def text; r record;
begin
  for r in select * from (values
    ('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure,
     $q$r.amenity_id = p_amenity and r.status = 'confirmed'$q$, $q$r.amenity_id = p_amenity and public.reservation_holds_slot(r)$q$),
    ('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure,
     $q$r.member_id = v_uid and r.status = 'confirmed'$q$, $q$r.member_id = v_uid and public.reservation_holds_slot(r)$q$),
    ('public.desk_book_walk_in(text,bigint,timestamptz,integer,boolean)'::regprocedure,
     $q$r.amenity_id = p_amenity and r.status = 'confirmed'$q$, $q$r.amenity_id = p_amenity and public.reservation_holds_slot(r)$q$),
    ('public.amenity_grid(date)'::regprocedure,
     $q$where (r.status = 'confirmed' or$q$, $q$where (public.reservation_holds_slot(r) or$q$),
    ('public.book_slots(jsonb)'::regprocedure,
     $q$    begin
      insert into public.reservations (member_id, amenity_id, starts_at, ends_at, created_by, user_amenity_id)$q$,
     $q$    if exists (select 1 from public.reservations h where h.amenity_id = b.a and h.status = 'awaiting_payment' and h.held_until > now()
               and tstzrange(h.starts_at, public.plus_cooldown(h.ends_at)) && tstzrange(b.s, public.plus_cooldown(b.e))) then
      perform public.fail('slot_taken', 'P0002');   -- 0148: someone is paying for it
    end if;
    begin
      insert into public.reservations (member_id, amenity_id, starts_at, ends_at, created_by, user_amenity_id)$q$)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0148: % lacks "%"', r.fn, r.old_text; end if;
    execute replace(v_def, r.old_text, r.new_text);
  end loop;
end $$;

revoke all on function public.hold_unpaid_booking(), public.reservation_holds_slot(public.reservations) from public, anon, authenticated;
