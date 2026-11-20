-- B4 / Q15 (2026-11-20): refuse an already-booked slot (or the member's own confirmed booking) before creating a payment.

do $$
declare
  v_def text := pg_get_functiondef('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure);
  v_old text := $q$  if exists (select 1 from public.training_sessions t where t.member_id = v_uid and t.status = 'scheduled'
             and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(p_start, v_end)) then
    perform public.fail('member_busy', 'P0002');
  end if;$q$;
  v_new text := $q$  if exists (select 1 from public.training_sessions t where t.member_id = v_uid and t.status = 'scheduled'
             and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(p_start, v_end)) then
    perform public.fail('member_busy', 'P0002');
  end if;
  if exists (select 1 from public.reservations r where r.amenity_id = p_amenity and r.status = 'confirmed'
             and tstzrange(r.starts_at, public.plus_cooldown(r.ends_at)) && tstzrange(p_start, public.plus_cooldown(v_end))) then
    perform public.fail('slot_taken', 'P0002');
  end if;
  if exists (select 1 from public.reservations r where r.member_id = v_uid and r.status = 'confirmed'
             and tstzrange(r.starts_at, r.ends_at) && tstzrange(p_start, v_end)) then
    perform public.fail('member_busy', 'P0002');
  end if;$q$;
begin
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then
    raise exception '0112: book_amenity_for lacks member_busy check';
  end if;
  execute replace(v_def, v_old, v_new);
end $$;

revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;
