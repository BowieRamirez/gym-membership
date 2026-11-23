-- Set 'Walk-in session' title on walk-in coach sessions (0126 omitted title on insert)
update public.training_sessions
set title = 'Walk-in session'
where member_id is null
  and (title is null or btrim(title) = '');

create or replace function public.desk_book_walkin_coach_session(
  p_coach uuid,
  p_start timestamptz,
  p_hours int default 1,
  p_walk_in_name text default null
)
returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
  v_end timestamptz;
  v_day date;
  v_open time;
  v_close time;
  v_max_h int;
  v_booked_hrs numeric;
  v_sid bigint;
  v_name text := nullif(btrim(coalesce(p_walk_in_name, '')), '');
begin
  perform public.require_permission('payments:record');
  if p_coach is null or p_start is null or p_hours not in (1, 2) then
    perform public.fail('bad_input', '22023');
  end if;
  if extract(minute from p_start at time zone 'Asia/Manila') <> 0
     or extract(second from p_start at time zone 'Asia/Manila') <> 0 then
    perform public.fail('off_grid', '22023');
  end if;
  if p_start <= now() then perform public.fail('past_slot', '22023'); end if;
  v_end := p_start + make_interval(hours => p_hours);
  v_day := (p_start at time zone 'Asia/Manila')::date;

  if exists (select 1 from public.gym_closures where closed_on = v_day) then
    perform public.fail('gym_closed', 'P0002');
  end if;
  select opens_at, closes_at, max_coach_hours_per_day
    into v_open, v_close, v_max_h from public.gym_settings where id = 1;
  if p_start < (v_day + v_open) at time zone 'Asia/Manila' or v_end > (v_day + v_close) at time zone 'Asia/Manila' then
    perform public.fail('outside_gym_hours', '22023');
  end if;

  if not exists (select 1 from public.coaches c join public.profiles pr on pr.id = c.user_id and pr.is_active where c.user_id = p_coach) then
    perform public.fail('not_found', '22023');
  end if;
  if not exists (select 1 from public.shift_posts sp where sp.coach_id = p_coach and sp.status = 'scheduled' and sp.starts_at <= p_start and sp.ends_at >= v_end) then
    perform public.fail('coach_not_free', 'P0002');
  end if;

  perform 1 from public.coaches where user_id = p_coach for update;
  select coalesce(sum(extract(epoch from (ts.scheduled_end - ts.scheduled_start)) / 3600.0), 0) into v_booked_hrs
    from public.training_sessions ts
    where ts.coach_id = p_coach and ts.status in ('scheduled', 'completed')
      and (ts.scheduled_start at time zone 'Asia/Manila')::date = v_day;
  if v_max_h is not null and v_booked_hrs + p_hours > v_max_h then
    perform public.fail('coach_day_full', 'P0002');
  end if;

  begin
    insert into public.training_sessions(coach_id, member_id, walk_in_name, title, scheduled_start, scheduled_end, status, payment_id, is_paid_addon)
    values (p_coach, null, v_name, 'Walk-in session', p_start, v_end, 'scheduled', null, false)
    returning training_session_id into v_sid;
  exception when exclusion_violation then
    perform public.fail('coach_taken', 'P0002');
  end;

  perform public.notify(p_coach, 'system', 'New walk-in session', coalesce(v_name, 'Walk-in'));
  perform public.audit('coach_session.desk_walkin', 'training_sessions', v_sid::text,
    jsonb_build_object('coach_id', p_coach, 'hours', p_hours));
  return v_sid;
end $$;
