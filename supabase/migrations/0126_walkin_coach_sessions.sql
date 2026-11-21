-- Allow front desk to book a 1-hour coach session for a walk-in Day pass customer without a member account.

alter table public.training_sessions alter column member_id drop not null;
alter table public.training_sessions add column if not exists walk_in_name text;

create or replace function public.scan_session(p_code text)
returns table (
  training_session_id bigint,
  member_id uuid,
  member_name text,
  coach_id uuid,
  coach_name text,
  scheduled_start timestamptz,
  scheduled_end timestamptz,
  checked_in_at timestamptz,
  coach_arrived_at timestamptz,
  status text,
  session_code text
)
language plpgsql security definer
set search_path = ''
as $$
declare
  v_s public.training_sessions;
  v_checked timestamptz;
begin
  perform public.require_active_user();
  if not public.is_staff() then perform public.fail('forbidden', '42501'); end if;

  select * into v_s
  from public.training_sessions ts
  where ts.session_code is not null and upper(ts.session_code) = upper(btrim(coalesce(p_code, '')))
  for update;

  if not found then perform public.fail('not_found', 'P0002'); end if;
  if v_s.status = 'cancelled' then perform public.fail('session_cancelled', 'P0002'); end if;
  if v_s.status in ('completed', 'no_show') then perform public.fail('already_processed', 'P0002'); end if;
  if v_s.held_until is not null then perform public.fail('payment_required', 'P0002'); end if;
  if (v_s.scheduled_start at time zone 'Asia/Manila')::date <> (now() at time zone 'Asia/Manila')::date then
    perform public.fail('wrong_day', 'P0002');
  end if;

  if v_s.checked_in_at is null then
    update public.training_sessions ts
    set checked_in_at = now()
    where ts.training_session_id = v_s.training_session_id
    returning ts.checked_in_at into v_checked;
    perform public.audit('coach_session.check_in', 'training_sessions', v_s.training_session_id::text,
      jsonb_build_object('member_id', v_s.member_id, 'coach_id', v_s.coach_id));
  else
    v_checked := v_s.checked_in_at;
  end if;

  return query
  select v_s.training_session_id,
         v_s.member_id,
         coalesce((select p.full_name from public.profiles p where p.id = v_s.member_id), v_s.walk_in_name, 'Walk-in'),
         v_s.coach_id,
         (select p.full_name from public.profiles p where p.id = v_s.coach_id),
         v_s.scheduled_start,
         v_s.scheduled_end,
         v_checked,
         v_s.coach_arrived_at,
         case
           when v_s.coach_arrived_at is not null then 'In session'
           when v_checked is not null then 'Checked in'
           else v_s.status::text
         end,
         v_s.session_code;
end $$;

create or replace function public.today_coach_sessions()
returns table (
  training_session_id bigint,
  session_code text,
  member_id uuid,
  member_name text,
  coach_id uuid,
  coach_name text,
  scheduled_start timestamptz,
  scheduled_end timestamptz,
  status text,
  checked_in_at timestamptz,
  coach_arrived_at timestamptz,
  held_until timestamptz
)
language plpgsql stable security definer
set search_path = ''
as $$
declare v_today date := (now() at time zone 'Asia/Manila')::date;
begin
  perform public.require_active_user();
  if not public.is_staff() then perform public.fail('forbidden', '42501'); end if;

  return query
  select ts.training_session_id,
         ts.session_code,
         ts.member_id,
         coalesce(mp.full_name, ts.walk_in_name, 'Walk-in'),
         ts.coach_id,
         cp.full_name,
         ts.scheduled_start,
         ts.scheduled_end,
         case
           when ts.held_until is not null then 'Waiting for payment'
           when ts.status = 'scheduled' and ts.coach_arrived_at is not null then 'In session'
           when ts.status = 'scheduled' and ts.checked_in_at is not null then 'Checked in'
           else ts.status::text
         end,
         ts.checked_in_at,
         ts.coach_arrived_at,
         ts.held_until
  from public.training_sessions ts
  left join public.profiles mp on mp.id = ts.member_id
  join public.profiles cp on cp.id = ts.coach_id
  where ts.status <> 'cancelled'
    and (ts.held_until is null or ts.held_until > now())
    and (ts.scheduled_start at time zone 'Asia/Manila')::date = v_today
  order by ts.scheduled_start;
end $$;

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
    insert into public.training_sessions(coach_id, member_id, walk_in_name, scheduled_start, scheduled_end, status, payment_id, is_paid_addon)
    values (p_coach, null, v_name, p_start, v_end, 'scheduled', null, false)
    returning training_session_id into v_sid;
  exception when exclusion_violation then
    perform public.fail('coach_taken', 'P0002');
  end;

  perform public.notify(p_coach, 'system', 'New walk-in session', coalesce(v_name, 'Walk-in'));
  perform public.audit('coach_session.desk_walkin', 'training_sessions', v_sid::text,
    jsonb_build_object('coach_id', p_coach, 'hours', p_hours));
  return v_sid;
end $$;

revoke all on function public.desk_book_walkin_coach_session(uuid, timestamptz, int, text) from public, anon;
grant execute on function public.desk_book_walkin_coach_session(uuid, timestamptz, int, text) to authenticated;
