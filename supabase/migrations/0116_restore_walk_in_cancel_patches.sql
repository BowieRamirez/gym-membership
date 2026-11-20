-- 0115 rewrote cancel_reservation and close_gym_day from their 0064/0065 text, losing the text patches 0095 and 0096 had applied:
-- a walk-in's booking (no member) could be cancelled by anyone signed in, with no reason and no refund, and an open or declined walk-in
-- refund request made the refund insert fail. Apply the same patches again.
do $$
declare v_def text; r record; v_insert text := $q$insert into public.refund_requests (kind, reservation_id, payment_id, member_id, amount, reason, status, decided_by, decided_at, decision_reason)$q$;
begin
  for r in select * from (values
    ('public.cancel_reservation(bigint,text)'::regprocedure, $q$(v.member_id <> v_uid and$q$, $q$(v.member_id is distinct from v_uid and$q$),
    ('public.cancel_reservation(bigint,text)'::regprocedure, $q$v_mine := v.member_id = v_uid;$q$, $q$v_mine := v.member_id is not distinct from v_uid;$q$),
    ('public.cancel_reservation(bigint,text)'::regprocedure, v_insert, $q$delete from public.refund_requests where reservation_id = v.reservation_id and kind = 'walk_in_booking' and status in ('pending', 'rejected');
      $q$ || v_insert),
    ('public.close_gym_day(date,text)'::regprocedure, v_insert, $q$delete from public.refund_requests where reservation_id = v.reservation_id and kind = 'walk_in_booking' and status in ('pending', 'rejected');
        $q$ || v_insert)
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then raise exception '0116: % lacks "%"', r.fn, r.old_text; end if;
    execute replace(v_def, r.old_text, r.new_text);
  end loop;
end $$;

revoke all on function public.cancel_reservation(bigint, text), public.close_gym_day(date, text) from public, anon;
grant execute on function public.cancel_reservation(bigint, text), public.close_gym_day(date, text) to authenticated;
