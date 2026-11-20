-- Owner fixes 2026-11-19 Q1 (docs/superpowers/plans/2026-11-19-owner-fixes.md): an account alone no longer books an amenity.
-- The desk's booking for a member (desk_book_amenity, then book_amenity_for, 0087) skipped the membership check (plan 2026-11-17 Z1);
-- now both paths need an active membership. A Day pass is a 1-day package (Phase 3), so it counts. Walk-ins with no account
-- (desk_book_walk_in, 0091/0092) are not accounts and are unchanged.
do $$
declare v_def text; v_old text := $q$  if not p_desk and not public.is_active_member(v_uid) then$q$;
begin
  v_def := pg_get_functiondef('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure);
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then raise exception '0097: book_amenity_for lacks "%"', v_old; end if;
  execute replace(v_def, v_old, $q$  if not public.is_active_member(v_uid) then$q$);   -- keeps the owner and grants
end $$;

revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;
