-- Phase 16 follow-up: old accounts on an earlier version of a plain package still see the Coach access add-on
-- if the newest published active version of that package sets coach_addon_price.

do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.coach_access_offer()'::regprocedure);
  if position('v_pkg.coach_addon_price is null or v_pkg.coach_addon_price <= 0' in v_def) = 0 then
    raise exception '0133: coach_access_offer lacks expected coach_addon_price guard';
  end if;
  v_def := replace(
    v_def,
    '  v_price     numeric;',
    '  v_price     numeric; v_newest public.membership_packages;'
  );
  v_def := replace(
    v_def,
    '  if v_pkg.coach_addon_price is null or v_pkg.coach_addon_price <= 0 then',
    $q$  if v_pkg.coach_addon_price is null or v_pkg.coach_addon_price <= 0 then
    select * into v_newest
    from public.membership_packages mp
    where mp.membership_package_id = public.newest_package_version(v_term.membership_package_id);
    if found and v_newest.is_active and v_newest.published_at is not null
       and not v_newest.includes_coach and v_newest.coach_addon_price is not null and v_newest.coach_addon_price > 0 then
      v_pkg.coach_addon_price := v_newest.coach_addon_price;
    end if;
  end if;

  if v_pkg.coach_addon_price is null or v_pkg.coach_addon_price <= 0 then$q$
  );
  execute v_def;
end $$;

revoke all on function public.coach_access_offer() from public, anon, authenticated;
grant execute on function public.coach_access_offer() to authenticated;
