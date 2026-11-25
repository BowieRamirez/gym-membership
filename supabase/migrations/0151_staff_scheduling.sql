-- Owner fixes 2026-11-25, Fix 3 Phase 3.1 (S1-S8): the admin schedules coaches and front desk.
-- Weekly templates (a row per working day, none = rest), assignments from a date (never edited, so history stays), and one-day or
-- date-range overrides. shift_for() says which shift applies on a day. Check-in follows it once gym_settings.schedule_enforced is on:
-- refused with no shift that day, or more than 5 minutes before the start; late is allowed. Coaches no longer post shifts: the
-- schedule writes their shift_posts (which booking, free coaches and closures already read) from tomorrow on, keeping any shift with
-- a booked session (the shift_posts guard, 0028, refuses to cancel it).

alter table public.gym_settings add column schedule_enforced boolean not null default false;
alter table public.coach_visits add column expected_start timestamptz, add column expected_end timestamptz;
alter table public.cash_shifts add column expected_start timestamptz, add column expected_end timestamptz;

create table public.schedule_templates (
  template_id bigint generated always as identity primary key,
  name        text not null unique check (char_length(btrim(name)) between 1 and 60),
  archived_at timestamptz,
  created_at  timestamptz not null default now()
);
create table public.schedule_template_days (
  template_id bigint   not null references public.schedule_templates on delete cascade,
  weekday     smallint not null check (weekday between 1 and 7),   -- ISO: 1 Monday .. 7 Sunday
  starts_at   time     not null,
  ends_at     time     not null check (ends_at > starts_at),        -- no overnight shifts: the gym closes the same day
  primary key (template_id, weekday)
);
create table public.schedule_assignments (
  assignment_id  bigint generated always as identity primary key,
  user_id        uuid   not null references public.profiles (id),
  template_id    bigint not null references public.schedule_templates,
  effective_from date   not null,
  created_by     uuid   not null references public.profiles (id),
  created_at     timestamptz not null default now()
);
create index schedule_assignments_user_idx on public.schedule_assignments (user_id, effective_from desc);
create table public.schedule_overrides (
  override_id bigint generated always as identity primary key,
  user_id     uuid not null references public.profiles (id),
  from_date   date not null,
  to_date     date not null check (to_date >= from_date and to_date - from_date <= 366),
  starts_at   time,
  ends_at     time,
  reason      text not null check (char_length(btrim(reason)) between 1 and 200),
  created_by  uuid not null references public.profiles (id),
  created_at  timestamptz not null default now(),
  check ((starts_at is null) = (ends_at is null)),   -- both empty = rest
  check (ends_at > starts_at)
);
create index schedule_overrides_user_idx on public.schedule_overrides (user_id, from_date);

alter table public.schedule_templates enable row level security;
alter table public.schedule_template_days enable row level security;
alter table public.schedule_assignments enable row level security;
alter table public.schedule_overrides enable row level security;
revoke all on public.schedule_templates, public.schedule_template_days, public.schedule_assignments, public.schedule_overrides from public, anon, authenticated;
grant select on public.schedule_templates, public.schedule_template_days, public.schedule_assignments, public.schedule_overrides to authenticated;
create policy schedule_templates_select on public.schedule_templates for select to authenticated using (public.is_active_user());
create policy schedule_template_days_select on public.schedule_template_days for select to authenticated using (public.is_active_user());
create policy schedule_assignments_select on public.schedule_assignments for select to authenticated
  using (public.is_active_user() and (user_id = auth.uid() or public.has_permission('reports:revenue')));
create policy schedule_overrides_select on public.schedule_overrides for select to authenticated
  using (public.is_active_user() and (user_id = auth.uid() or public.has_permission('reports:revenue')));

-- the shift that applies to someone on a Manila day: an override (latest wins; empty times = rest), else the latest assignment's
-- weekday (no row = rest), else none. A gym closure day has no shift.
create function public.shift_for(p_user uuid, p_day date) returns table (starts_at timestamptz, ends_at timestamptz)
language plpgsql stable security definer set search_path = '' as $$
declare v_s time; v_e time; v_tpl bigint;
begin
  if exists (select 1 from public.gym_closures where closed_on = p_day) then return; end if;
  select o.starts_at, o.ends_at into v_s, v_e from public.schedule_overrides o
  where o.user_id = p_user and p_day between o.from_date and o.to_date order by o.override_id desc limit 1;
  if not found then
    select a.template_id into v_tpl from public.schedule_assignments a
    where a.user_id = p_user and a.effective_from <= p_day order by a.effective_from desc, a.assignment_id desc limit 1;
    select d.starts_at, d.ends_at into v_s, v_e from public.schedule_template_days d
    where d.template_id = v_tpl and d.weekday = extract(isodow from p_day);
  end if;
  if v_s is null then return; end if;
  return query select (p_day + v_s) at time zone 'Asia/Manila', (p_day + v_e) at time zone 'Asia/Manila';
end $$;

create function public.schedule_check_times(p_start time, p_end time) returns void
language plpgsql stable security definer set search_path = '' as $$
declare v_open time; v_close time;
begin
  if p_start is null or p_end is null or p_end <= p_start then perform public.fail('bad_input', '22023'); end if;
  if extract(second from p_start) <> 0 or extract(second from p_end) <> 0
     or extract(minute from p_start) not in (0, 30) or extract(minute from p_end) not in (0, 30) then
    perform public.fail('off_grid', '22023');   -- shift_posts sit on the half hour (0028)
  end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  if p_start < v_open or p_end > v_close then perform public.fail('outside_gym_hours', '22023'); end if;
end $$;

create function public.schedule_require_person(p_user uuid) returns void
language plpgsql stable security definer set search_path = '' as $$
begin
  if not exists (select 1 from public.profiles p join public.user_roles ur on ur.user_id = p.id join public.roles r on r.role_id = ur.role_id
                 where p.id = p_user and p.is_active and r.name in ('coach', 'employee')) then
    perform public.fail('not_schedulable', '22023');   -- S1: coaches and front desk only
  end if;
end $$;

-- S8: rewrite a coach's scheduled shifts on [from, to] (never before tomorrow) to match shift_for. A shift with a booked session
-- inside it cannot be cancelled (shift_has_sessions, 0028), so it is kept and its day returned.
create function public.schedule_sync_coach(p_user uuid, p_from date, p_to date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_day date; v_s timestamptz; v_e timestamptz; v_post record; v_kept jsonb := '[]'; v_blocked boolean;
  v_tomorrow date := (now() at time zone 'Asia/Manila')::date + 1;
begin
  if not exists (select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id where ur.user_id = p_user and r.name = 'coach') then
    return v_kept;
  end if;
  for v_day in select generate_series(greatest(p_from, v_tomorrow), p_to, interval '1 day')::date loop
    v_s := null; v_e := null;
    select f.starts_at, f.ends_at into v_s, v_e from public.shift_for(p_user, v_day) f;
    if (select count(*) from public.shift_posts sp where sp.coach_id = p_user and sp.status = 'scheduled'
        and (sp.starts_at at time zone 'Asia/Manila')::date = v_day) = 1
       and exists (select 1 from public.shift_posts sp where sp.coach_id = p_user and sp.status = 'scheduled'
                   and sp.starts_at = v_s and sp.ends_at = v_e) then
      continue;   -- already right
    end if;
    v_blocked := false;
    for v_post in select shift_post_id from public.shift_posts sp where sp.coach_id = p_user and sp.status = 'scheduled'
                  and (sp.starts_at at time zone 'Asia/Manila')::date = v_day loop
      begin
        update public.shift_posts set status = 'cancelled' where shift_post_id = v_post.shift_post_id;
      exception when sqlstate 'P0002' then v_blocked := true;   -- shift_has_sessions
      end;
    end loop;
    if v_blocked then v_kept := v_kept || to_jsonb(v_day::text);
    elsif v_s is not null then insert into public.shift_posts (coach_id, starts_at, ends_at) values (p_user, v_s, v_e);
    end if;
  end loop;
  return v_kept;
end $$;

-- coaches whose shifts a change to these people (or this template) affects, synced for the next 8 weeks
create function public.schedule_sync_many(p_users uuid[], p_from date default null, p_to date default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_u uuid; v_kept jsonb := '[]'; v_today date := (now() at time zone 'Asia/Manila')::date;
begin
  foreach v_u in array coalesce(p_users, '{}') loop
    v_kept := v_kept || public.schedule_sync_coach(v_u, coalesce(p_from, v_today + 1), least(coalesce(p_to, v_today + 56), v_today + 56));
  end loop;
  return v_kept;
end $$;

-- daily: keep 8 weeks of shifts filled for every coach who has a schedule (a coach without one keeps their own shifts, S8)
create function public.fill_coach_shifts() returns int
language plpgsql security definer set search_path = '' as $$
declare v_u uuid; v_n int := 0;
begin
  for v_u in select distinct a.user_id from public.schedule_assignments a loop
    begin
      perform public.schedule_sync_many(array[v_u]);
      v_n := v_n + 1;
    exception when others then null;   -- ponytail: one coach's bad day (e.g. gym hours changed under a template) must not stop the rest
    end;
  end loop;
  return v_n;
end $$;

create function public.schedule_template_save(p_id bigint, p_name text, p_days jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_id bigint := p_id; v_day jsonb; v_users uuid[];
begin
  perform public.require_permission('reports:revenue');
  if char_length(btrim(coalesce(p_name, ''))) not between 1 and 60 or jsonb_typeof(coalesce(p_days, 'null')) <> 'array' then
    perform public.fail('bad_input', '22023');
  end if;
  for v_day in select * from jsonb_array_elements(p_days) loop
    if (v_day->>'weekday')::int not between 1 and 7 then perform public.fail('bad_input', '22023'); end if;
    perform public.schedule_check_times((v_day->>'starts')::time, (v_day->>'ends')::time);
  end loop;
  begin
    if v_id is null then
      insert into public.schedule_templates (name) values (btrim(p_name)) returning template_id into v_id;
    else
      update public.schedule_templates set name = btrim(p_name) where template_id = v_id and archived_at is null;
      if not found then perform public.fail('not_found', '22023'); end if;
      delete from public.schedule_template_days where template_id = v_id;
    end if;
    insert into public.schedule_template_days (template_id, weekday, starts_at, ends_at)
    select v_id, (d->>'weekday')::smallint, (d->>'starts')::time, (d->>'ends')::time from jsonb_array_elements(p_days) d;
  exception when unique_violation then perform public.fail(case when sqlerrm like '%name%' then 'name_taken' else 'bad_input' end, '22023');
  end;
  perform public.audit('schedule.template_saved', 'schedule_templates', v_id::text, jsonb_build_object('name', btrim(p_name), 'days', p_days));
  select array_agg(distinct user_id) into v_users from public.schedule_assignments where template_id = v_id;
  return jsonb_build_object('template_id', v_id, 'kept', public.schedule_sync_many(v_users));
end $$;

create function public.schedule_template_duplicate(p_id bigint, p_name text) returns bigint
language plpgsql security definer set search_path = '' as $$
declare v_days jsonb;
begin
  perform public.require_permission('reports:revenue');
  if not exists (select 1 from public.schedule_templates where template_id = p_id) then perform public.fail('not_found', '22023'); end if;
  select coalesce(jsonb_agg(jsonb_build_object('weekday', weekday, 'starts', starts_at, 'ends', ends_at)), '[]') into v_days
  from public.schedule_template_days where template_id = p_id;
  return (public.schedule_template_save(null, p_name, v_days)->>'template_id')::bigint;
end $$;

-- in use = someone's current template today, or a template that starts later
create function public.schedule_template_archive(p_id bigint) returns void
language plpgsql security definer set search_path = '' as $$
declare v_today date := (now() at time zone 'Asia/Manila')::date;
begin
  perform public.require_permission('reports:revenue');
  if exists (select 1 from public.schedule_assignments a where a.template_id = p_id and (a.effective_from > v_today
       or a.assignment_id = (select b.assignment_id from public.schedule_assignments b where b.user_id = a.user_id and b.effective_from <= v_today
                             order by b.effective_from desc, b.assignment_id desc limit 1))) then
    perform public.fail('template_in_use', '22023');
  end if;
  update public.schedule_templates set archived_at = now() where template_id = p_id and archived_at is null;
  if not found then perform public.fail('not_found', '22023'); end if;
  perform public.audit('schedule.template_archived', 'schedule_templates', p_id::text, null);
end $$;

create function public.schedule_assign(p_user uuid, p_template bigint, p_from date) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_id bigint;
begin
  perform public.require_permission('reports:revenue');
  perform public.schedule_require_person(p_user);
  if p_from is null or p_from < (now() at time zone 'Asia/Manila')::date then perform public.fail('past_date', '22023'); end if;
  if not exists (select 1 from public.schedule_templates where template_id = p_template) then perform public.fail('not_found', '22023'); end if;
  if exists (select 1 from public.schedule_templates where template_id = p_template and archived_at is not null) then
    perform public.fail('template_archived', '22023');
  end if;
  insert into public.schedule_assignments (user_id, template_id, effective_from, created_by)
  values (p_user, p_template, p_from, auth.uid()) returning assignment_id into v_id;
  perform public.audit('schedule.assigned', 'schedule_assignments', v_id::text,
    jsonb_build_object('user_id', p_user, 'template_id', p_template, 'effective_from', p_from));
  return public.schedule_sync_many(array[p_user], p_from);
end $$;

create function public.schedule_override_add(p_user uuid, p_from date, p_to date, p_start time, p_end time, p_reason text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_id bigint;
begin
  perform public.require_permission('reports:revenue');
  perform public.schedule_require_person(p_user);
  if p_from is null or p_to is null or p_to < p_from or p_to - p_from > 366 or char_length(btrim(coalesce(p_reason, ''))) not between 1 and 200 then
    perform public.fail('bad_input', '22023');
  end if;
  if p_from < (now() at time zone 'Asia/Manila')::date then perform public.fail('past_date', '22023'); end if;
  if p_start is not null or p_end is not null then perform public.schedule_check_times(p_start, p_end); end if;
  insert into public.schedule_overrides (user_id, from_date, to_date, starts_at, ends_at, reason, created_by)
  values (p_user, p_from, p_to, p_start, p_end, btrim(p_reason), auth.uid()) returning override_id into v_id;
  perform public.audit('schedule.override_added', 'schedule_overrides', v_id::text,
    jsonb_build_object('user_id', p_user, 'from', p_from, 'to', p_to, 'starts', p_start, 'ends', p_end, 'reason', btrim(p_reason)));
  return public.schedule_sync_many(array[p_user], p_from, p_to);
end $$;

-- only an override that has not started yet can be removed: a past day's shift stays explainable
create function public.schedule_override_remove(p_id bigint) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v public.schedule_overrides;
begin
  perform public.require_permission('reports:revenue');
  delete from public.schedule_overrides where override_id = p_id and from_date > (now() at time zone 'Asia/Manila')::date returning * into v;
  if not found then perform public.fail('not_found', '22023'); end if;
  perform public.audit('schedule.override_removed', 'schedule_overrides', p_id::text,
    jsonb_build_object('user_id', v.user_id, 'from', v.from_date, 'to', v.to_date, 'reason', v.reason));
  return public.schedule_sync_many(array[v.user_id], v.from_date, v.to_date);
end $$;

-- S3/S4: the clock-in rule. Refused with no shift today or more than 5 minutes before the start (server time); late is allowed.
-- Off until the admin has set everyone's schedule (gym_settings.schedule_enforced).
create function public.schedule_clock_in(p_user uuid, out o_start timestamptz, out o_end timestamptz)
language plpgsql stable security definer set search_path = '' as $$
begin
  select f.starts_at, f.ends_at into o_start, o_end from public.shift_for(p_user, (now() at time zone 'Asia/Manila')::date) f;
  if not (select schedule_enforced from public.gym_settings where id = 1) then return; end if;
  if o_start is null then perform public.fail('no_shift_today', '22023'); end if;
  if now() < o_start - interval '5 minutes' then
    perform public.fail('too_early:' || to_char(o_start at time zone 'Asia/Manila', 'FMHH12:MI AM'), '22023');
  end if;
end $$;

-- 0032 as it was, plus the shift rule and the shift it was checked in against
create or replace function public.coach_check_in(p_coach uuid default null) returns bigint
language plpgsql security definer set search_path='' as $$
declare v_coach uuid := public.coach_visit_actor(p_coach); v_id bigint; v_open time; v_close time;
  v_now time := (now() at time zone 'Asia/Manila')::time; v_shift record;
begin
  select coach_visit_id into v_id from public.coach_visits where coach_id = v_coach and checked_out_at is null;
  if found then return v_id; end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  if v_now < v_open or v_now >= v_close then perform public.fail('gym_closed', '22023'); end if;
  select * into v_shift from public.schedule_clock_in(v_coach);
  insert into public.coach_visits (coach_id, checked_in_by, expected_start, expected_end)
  values (v_coach, auth.uid(), v_shift.o_start, v_shift.o_end) returning coach_visit_id into v_id;
  return v_id;
end $$;

-- 0050 as it was, plus the shift rule for front desk (S1: admins and sub-admins are not scheduled)
create or replace function public.open_cash_shift(p_float numeric, p_note text default null) returns public.cash_shifts
language plpgsql security definer set search_path='' as $$
declare v_shift public.cash_shifts; v_note text := nullif(btrim(coalesce(p_note, '')), ''); v_s timestamptz; v_e timestamptz;
begin
  perform public.require_permission('payments:record');
  if p_float is null or p_float < 0 or p_float > 10000000 or length(coalesce(v_note, '')) > 500 then perform public.fail('bad_input', '22023'); end if;
  if p_float <> (select opening_float from public.gym_settings where id = 1) and v_note is null then perform public.fail('note_required', '22023'); end if;
  if public.has_role('employee') and not (public.has_role('admin') or public.has_role('subadmin')) then
    select o_start, o_end into v_s, v_e from public.schedule_clock_in(auth.uid());
  end if;
  begin
    insert into public.cash_shifts(cashier_id, opening_float, open_note, expected_start, expected_end)
    values (auth.uid(), round(p_float, 2), v_note, v_s, v_e) returning * into v_shift;
  exception when unique_violation then perform public.fail('shift_open', 'P0002');
  end;
  perform public.audit('cash_shift.open', 'cash_shifts', v_shift.shift_id::text, jsonb_build_object('float', v_shift.opening_float, 'note', v_note));
  return v_shift;
end $$;

-- S2: coaches no longer post or change shifts; the schedule writes them. "Your coach posted a shift" went with hiring (0148).
revoke insert, update on public.shift_posts from authenticated;
drop policy shift_posts_insert on public.shift_posts;
drop policy shift_posts_update on public.shift_posts;
drop trigger shift_posts_notify on public.shift_posts;

select cron.schedule('fill-coach-shifts', '10 16 * * *', $$select public.fill_coach_shifts()$$);   -- 00:10 Manila

revoke all on function public.shift_for(uuid, date), public.schedule_check_times(time, time), public.schedule_require_person(uuid),
  public.schedule_sync_coach(uuid, date, date), public.schedule_sync_many(uuid[], date, date), public.fill_coach_shifts(),
  public.schedule_clock_in(uuid) from public, anon, authenticated;
revoke all on function public.schedule_template_save(bigint, text, jsonb), public.schedule_template_duplicate(bigint, text),
  public.schedule_template_archive(bigint), public.schedule_assign(uuid, bigint, date),
  public.schedule_override_add(uuid, date, date, time, time, text), public.schedule_override_remove(bigint) from public, anon;
grant execute on function public.schedule_template_save(bigint, text, jsonb), public.schedule_template_duplicate(bigint, text),
  public.schedule_template_archive(bigint), public.schedule_assign(uuid, bigint, date),
  public.schedule_override_add(uuid, date, date, time, time, text), public.schedule_override_remove(bigint) to authenticated;
revoke all on function public.coach_check_in(uuid), public.open_cash_shift(numeric, text) from public, anon;
grant execute on function public.coach_check_in(uuid), public.open_cash_shift(numeric, text) to authenticated;
