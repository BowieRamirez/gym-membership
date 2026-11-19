-- Owner fixes 2026-11-19 Q6 (docs/superpowers/plans/2026-11-19-owner-fixes.md): members pay online only. Cash is taken by the
-- front desk, recorded against the member (record_counter_payment, desk_book_amenity), and its audit row names the staff member.
-- The member functions refuse 'cash', so an older app that still offers it can't make a cash payment. Cash payments members
-- already chose stay for the desk to verify or expire (0085).

-- avail_membership, extend_membership and renew_membership (0084) are the member's package paths, and only they call this
create or replace function public.check_package_method(p_method public.payment_method) returns void
language plpgsql set search_path='' as $$
begin
  if p_method = 'cash' then perform public.fail('online_only', '22023'); end if;
  if p_method is null or p_method <> 'qr' then perform public.fail('bad_input', '22023'); end if;
end $$;
revoke all on function public.check_package_method(public.payment_method) from public, anon, authenticated;

-- the member's booking (the desk books through desk_book_amenity, which passes 'cash' to book_amenity_for itself)
do $$
declare v_def text; v_old text := $q$  return public.book_amenity_for(auth.uid(), false,$q$;
begin
  v_def := pg_get_functiondef('public.book_amenity(bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure);
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then raise exception '0099: book_amenity lacks "%"', v_old; end if;
  execute replace(v_def, v_old, $q$  if p_method = 'cash' then perform public.fail('online_only', '22023'); end if;
  return public.book_amenity_for(auth.uid(), false,$q$);
end $$;
revoke all on function public.book_amenity(bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon;
grant execute on function public.book_amenity(bigint, timestamptz, integer, public.payment_method, uuid, text) to authenticated;
