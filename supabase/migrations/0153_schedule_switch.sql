-- Owner fixes 2026-11-25, Fix 3 Phase 3.3: the Scheduling page turns shift check-in on and off (gym_settings.schedule_enforced, 0151).
-- A function, not a settings write: gym settings are the System admin's, and a sub-admin who runs schedules needs this switch too.
create function public.schedule_set_enforced(p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_permission('reports:revenue');
  if p_on is null then perform public.fail('bad_input', '22023'); end if;
  update public.gym_settings set schedule_enforced = p_on where id = 1;
  perform public.audit('schedule.enforced', 'gym_settings', '1', jsonb_build_object('on', p_on));
end $$;
revoke all on function public.schedule_set_enforced(boolean) from public, anon;
grant execute on function public.schedule_set_enforced(boolean) to authenticated;
