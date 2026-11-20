-- Owner fixes 2026-11-19 Q10 (docs/superpowers/plans/2026-11-19-owner-fixes.md):
-- a Day pass booking must end by the pass's midnight (e.g. 11 pm to 1 am is refused with outside_membership);
-- other memberships keep the start-only rule from 0098.

do $$
declare
  v_def text := pg_get_functiondef('public.book_amenity_for(uuid,boolean,bigint,timestamptz,integer,public.payment_method,uuid,text)'::regprocedure);
  v_old text := $q$  if not exists(select 1 from public.membership_terms where member_id = v_uid and cancelled_at is null and starts_at <= p_start and ends_at > p_start) then
    perform public.fail('outside_membership', '22023');   -- 0098
  end if;$q$;
  v_new text := $q$  if not exists(
    select 1 from public.membership_terms t
    join public.membership_packages p on p.membership_package_id = t.membership_package_id
    where t.member_id = v_uid and t.cancelled_at is null
      and t.starts_at <= p_start and t.ends_at > p_start
      and (not p.is_day_pass or t.ends_at >= v_end)
  ) then
    perform public.fail('outside_membership', '22023');   -- 0106
  end if;$q$;
begin
  if array_length(string_to_array(v_def, v_old), 1) <> 2 then
    raise exception '0106: book_amenity_for lacks 0098 term check';
  end if;
  execute replace(v_def, v_old, v_new);
end $$;

revoke all on function public.book_amenity_for(uuid, boolean, bigint, timestamptz, integer, public.payment_method, uuid, text) from public, anon, authenticated;
