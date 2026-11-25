-- Owner fixes 2026-11-25, Fix 3 Phase 3.5 (S9): booking lists stay shift-based; if a coach doesn't come, the front desk or an admin
-- moves the booked session to another coach who is free then. Same checks as booking (0120): a scheduled shift covering it, under the
-- day's hour limit, and no overlapping session (the table's exclusion). Only the coach changes: time, price and payment stay.
-- Per-session pay follows coach_id, so the coach who runs it is the one paid.
create function public.reassign_session(p_session bigint, p_coach uuid, p_reason text) returns void
language plpgsql security definer set search_path = '' as $$
declare v public.training_sessions; v_day date; v_hours numeric; v_max int; v_who text;
begin
  perform public.require_permission('payments:record');
  if char_length(btrim(coalesce(p_reason, ''))) not between 1 and 200 then perform public.fail('bad_reason', '22023'); end if;
  select * into v from public.training_sessions where training_session_id = p_session for update;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v.status <> 'scheduled' or v.scheduled_start <= now() then perform public.fail('session_started', 'P0002'); end if;
  if p_coach is null or p_coach = v.coach_id then perform public.fail('bad_input', '22023'); end if;
  if not exists (select 1 from public.coaches c join public.profiles p on p.id = c.user_id and p.is_active where c.user_id = p_coach) then
    perform public.fail('not_found', '22023');
  end if;
  perform 1 from public.coaches where user_id = p_coach for update;   -- one booking or move per coach at a time
  if not exists (select 1 from public.shift_posts sp where sp.coach_id = p_coach and sp.status = 'scheduled'
                 and sp.starts_at <= v.scheduled_start and sp.ends_at >= v.scheduled_end) then
    perform public.fail('coach_not_free', 'P0002');
  end if;
  v_day := (v.scheduled_start at time zone 'Asia/Manila')::date;
  select max_coach_hours_per_day into v_max from public.gym_settings where id = 1;
  select coalesce(sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0), 0) into v_hours
    from public.training_sessions ts
    where ts.coach_id = p_coach and ts.status in ('scheduled', 'completed') and (ts.scheduled_start at time zone 'Asia/Manila')::date = v_day;
  if v_max is not null and v_hours + extract(epoch from (v.scheduled_end - v.scheduled_start)) / 3600.0 > v_max then
    perform public.fail('coach_day_full', 'P0002');
  end if;
  begin
    update public.training_sessions set coach_id = p_coach where training_session_id = p_session;
  exception when exclusion_violation then perform public.fail('coach_not_free', 'P0002');
  end;
  v_who := to_char(v.scheduled_start at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM') || ' · now with '
    || (select coalesce(nullif(btrim(full_name), ''), username) from public.profiles where id = p_coach);
  if v.member_id is not null then
    perform public.notify(v.member_id, 'system', 'Your coach session has a new coach', v_who, jsonb_build_object('training_session_id', p_session));
  end if;
  perform public.notify(v.coach_id, 'system', 'A session moved to another coach', v_who, jsonb_build_object('training_session_id', p_session));
  perform public.notify(p_coach, 'system', 'A session was moved to you',
    to_char(v.scheduled_start at time zone 'Asia/Manila', 'Dy DD Mon, HH12:MI AM'), jsonb_build_object('training_session_id', p_session));
  perform public.audit('session.reassigned', 'training_sessions', p_session::text,
    jsonb_build_object('from_coach', v.coach_id, 'to_coach', p_coach, 'reason', btrim(p_reason)));
end $$;
revoke all on function public.reassign_session(bigint, uuid, text) from public, anon;
grant execute on function public.reassign_session(bigint, uuid, text) to authenticated;
