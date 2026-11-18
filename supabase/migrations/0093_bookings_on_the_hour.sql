-- Owner, 2026-11-17: bookings are whole hours (0090) and now also start on the hour; the apps offer only those times (cde4012).
-- The database refuses any other start, so an older app still showing 1:30 can't book it. book_amenity_for is the shared body for
-- member bookings (with a pass or paid) and the desk's member bookings, so the check goes at its top; the walk-in function gets it too.
-- book_slots keeps its 30-minute slots: book_amenity_for's pass path hands it the half hours inside an on-the-hour booking.
do $$
declare v_def text; r record;
begin
  for r in select * from (values
    ('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)',
     $q$  perform public.expire_payments();   -- requests that ran out since the job last ran are cancelled first$q$,
     $q$  if extract(epoch from p_start)::bigint % 3600 <> 0 then perform public.fail('not_on_the_hour', '22023'); end if;   -- 0093
  perform public.expire_payments();   -- requests that ran out since the job last ran are cancelled first$q$),
    ('public.desk_book_walk_in(text,bigint,timestamptz,integer,boolean)',
     $q$if extract(epoch from p_start)::bigint % 1800 <> 0 then perform public.fail('off_grid', '22023'); end if;$q$,
     $q$if extract(epoch from p_start)::bigint % 3600 <> 0 then perform public.fail('not_on_the_hour', '22023'); end if;   -- 0093$q$)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn::regprocedure);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0093: % lacks "%"', r.fn, r.old_text; end if;
    execute replace(v_def, r.old_text, r.new_text);   -- create or replace keeps the owner and grants
  end loop;
end $$;

revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;
revoke all on function public.desk_book_walk_in(text, bigint, timestamptz, integer, boolean) from public, anon;
grant execute on function public.desk_book_walk_in(text, bigint, timestamptz, integer, boolean) to authenticated;
