-- 0165 (owner, 2026-11-25): coaches report damage any time from their own Report damage page, not only on a session that has
-- started. Without a session a coach files it as themselves (coach_id = the coach), with an amenity or none ("Something else").
-- A report on a session no longer waits for the session to start (0140's session_not_started is gone); a cancelled session
-- still can't carry one. Everything else is 0140's report_damage as it was.
create or replace function public.report_damage(p_session_id bigint default null, p_amenity_id bigint default null,
  p_description text default null, p_photo_path text default null) returns bigint
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := auth.uid();
  v_s public.training_sessions;
  v_coach uuid;
  v_amenity bigint := p_amenity_id;
  v_id bigint;
  v_adm record;
begin
  perform public.require_active_user();
  if nullif(btrim(p_description), '') is null or length(btrim(p_description)) > 1000 then perform public.fail('bad_input', '22023'); end if;
  if v_amenity is not null and not exists (select 1 from public.amenities where amenity_id = v_amenity) then
    perform public.fail('not_found', 'P0002');
  end if;

  if p_session_id is not null then
    select * into v_s from public.training_sessions where training_session_id = p_session_id;
    if not found then perform public.fail('not_found', 'P0002'); end if;
    if not public.is_staff() and v_s.coach_id <> v_uid then perform public.fail('forbidden', '42501'); end if;
    if v_s.status = 'cancelled' then perform public.fail('session_cancelled', '22023'); end if;
    v_coach := v_s.coach_id;
    if v_amenity is null then
      select r.amenity_id into v_amenity from public.reservations r
      where r.training_session_id = p_session_id limit 1;
    end if;
  elsif public.has_role('coach') and not public.is_staff() then
    v_coach := v_uid;
  elsif not public.is_staff() then
    perform public.fail('forbidden', '42501');
  end if;

  insert into public.damage_reports (training_session_id, amenity_id, coach_id, reported_by, description, photo_path)
  values (p_session_id, v_amenity, v_coach, v_uid, btrim(p_description), nullif(btrim(p_photo_path), ''))
  returning damage_report_id into v_id;

  for v_adm in
    select distinct ur.user_id
    from public.user_roles ur
    join public.roles r on r.role_id = ur.role_id
    join public.profiles p on p.id = ur.user_id
    where r.name in ('admin', 'subadmin') and p.is_active
  loop
    perform public.notify(v_adm.user_id, 'system', 'Damage report filed', left(btrim(p_description), 120),
      jsonb_build_object('damage_report_id', v_id));
  end loop;

  perform public.audit('damage_report.create', 'damage_reports', v_id::text,
    jsonb_build_object('session_id', p_session_id, 'amenity_id', v_amenity));
  return v_id;
end $$;
revoke all on function public.report_damage(bigint, bigint, text, text) from public, anon;
grant execute on function public.report_damage(bigint, bigint, text, text) to authenticated;
