-- Fix to F8 (2026-11-09 plan, 0049): "a member with no PIN can only be checked in manually by Admin or System admin, with a reason".
-- record_check_in let an Admin or System admin check in ANY member with a reason and a blank PIN, so a member who had set a desk PIN
-- could be checked in without it. Now a reason is refused with has_desk_pin when the member has a PIN (a desk_pins row with a hash;
-- a row holding only wrong-password tries (0062) is no PIN). Online and queued offline check-ins alike.
-- One line added through pg_get_functiondef, as 0061 did; create or replace keeps the owner and grants.
do $$
declare
  v_fn regprocedure := 'public.record_check_in(uuid,text,timestamptz,uuid,text,text,uuid)';
  v_old text := 'then perform public.fail(''pin_required'',''22023''); end if;';
  v_def text := pg_get_functiondef(v_fn);
begin
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then
    raise exception '0063: % does not contain "%" exactly once', v_fn, v_old;
  end if;
  execute replace(v_def, v_old, v_old || '
    if exists (select 1 from public.desk_pins where user_id=p_member_id and pin_hash is not null) then perform public.fail(''has_desk_pin'',''22023''); end if;');
end $$;
