-- Phase 8 (E1, E2, 2026-11-20): remove coach hourly_rate, amenity coach_fee, and With a coach on amenity bookings.
-- book_amenity_for stops checking/storing a coach or adding coach_fee (old callers passing p_coach still work at the amenity price only).

do $$
declare
  v_def text := pg_get_functiondef('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure);
  v_r1_old text := $q$  if p_coach is not null and not public.coach_is_free(p_coach, p_amenity, p_start, v_end) then perform public.fail('coach_not_free', 'P0002'); end if;$q$;
  v_r2_old text := $q$    if p_coach is not null then
      begin
        update public.reservations set coach_id = p_coach, total_price = v_am.coach_fee * (p_minutes / 60), down_payment = 0,
          user_amenity_id = coalesce(user_amenity_id, v_pass)
          where reservation_id = v_res.reservation_id returning * into v_res;
      exception when exclusion_violation then perform public.fail('coach_not_free', 'P0002');
      end;
    end if;$q$;
  v_r3_old text := $q$  v_total := (v_am.price + case when p_coach is not null then v_am.coach_fee else 0 end) * (p_minutes / 60);   -- 0090: per hour$q$;
  v_r4_old text := $q$  values (v_uid, p_amenity, p_coach, p_start, v_end, auth.uid(), v_ua, v_pay.payment_id, 'awaiting_payment', v_due, v_down) returning * into v_res;$q$;
begin
  if array_length(string_to_array(v_def, v_r1_old), 1) <> 2 then raise exception '0118: book_amenity_for lacks coach_is_free check'; end if;
  if array_length(string_to_array(v_def, v_r2_old), 1) <> 2 then raise exception '0118: book_amenity_for lacks pass coach_fee update'; end if;
  if array_length(string_to_array(v_def, v_r3_old), 1) <> 2 then raise exception '0118: book_amenity_for lacks v_total coach_fee'; end if;
  if array_length(string_to_array(v_def, v_r4_old), 1) <> 2 then raise exception '0118: book_amenity_for lacks reservation insert'; end if;

  v_def := replace(v_def, v_r1_old, '');
  v_def := replace(v_def, v_r2_old, '');
  v_def := replace(v_def, v_r3_old, '  v_total := v_am.price * (p_minutes / 60);   -- 0118: amenity price only');
  v_def := replace(v_def, v_r4_old, '  values (v_uid, p_amenity, null, p_start, v_end, auth.uid(), v_ua, v_pay.payment_id, ''awaiting_payment'', v_due, v_down) returning * into v_res;');
  execute v_def;
end $$;

revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;

alter table public.coaches drop constraint if exists coaches_rate_positive;
alter table public.coaches drop column if exists hourly_rate;
alter table public.amenities drop column if exists coach_fee;
