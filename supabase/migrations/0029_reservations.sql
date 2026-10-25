-- Phase 6 (D6-D8, 2026-10-22): amenity reservations.
-- One unit per amenity. Each confirmed booking holds its amenity for [start, end + 30 minutes): the cooldown follows a use,
-- never precedes it, and a booking may start exactly when the previous cooldown ends. Exclusion constraints make double
-- booking, booking into a cooldown, and a member or coach being in two places impossible, even under concurrent requests.
-- In use / ended is read from the clock (starts_at <= now() < ends_at), so nothing depends on a job or an open app.
-- Solo bookings need an active pass for the amenity; coach-linked bookings (coach_id) are created by the Phase 7 request flow.

-- adding minutes to a timestamptz does not depend on the session time zone, so this is genuinely immutable
create function public.plus_cooldown(p_end timestamptz) returns timestamptz
language sql immutable parallel safe set search_path='' as $$ select p_end + interval '30 minutes' $$;

create table public.reservations (
  reservation_id      bigint      generated always as identity primary key,
  member_id           uuid        not null references public.members (user_id),
  amenity_id          bigint      not null references public.amenities (amenity_id),
  coach_id            uuid        references public.coaches (user_id),
  training_session_id bigint      references public.training_sessions (training_session_id),
  starts_at           timestamptz not null,
  ends_at             timestamptz not null,
  status              text        not null default 'confirmed' check (status in ('confirmed', 'cancelled')),
  created_by          uuid        references public.profiles (id),
  created_at          timestamptz not null default now(),
  cancelled_at        timestamptz,
  cancelled_by        uuid        references public.profiles (id),
  cancel_reason       text,
  extended_at         timestamptz,
  extended_by         uuid        references public.profiles (id),
  check (ends_at > starts_at),
  constraint reservations_amenity_cooldown exclude using gist (
    amenity_id with =, tstzrange(starts_at, public.plus_cooldown(ends_at)) with &&) where (status = 'confirmed'),
  constraint reservations_member_busy exclude using gist (
    member_id with =, tstzrange(starts_at, ends_at) with &&) where (status = 'confirmed'),
  constraint reservations_coach_busy exclude using gist (
    coach_id with =, tstzrange(starts_at, ends_at) with &&) where (status = 'confirmed' and coach_id is not null)
);
create index reservations_member_idx on public.reservations (member_id, starts_at);
create index reservations_coach_idx on public.reservations (coach_id, starts_at) where coach_id is not null;

alter table public.reservations enable row level security;
revoke all on public.reservations from public, anon, authenticated;
grant select on public.reservations to authenticated;
create policy reservations_select on public.reservations for select to authenticated
  using (public.is_active_user() and (member_id = auth.uid() or coach_id = auth.uid() or public.is_staff()));

-- Turns an exclusion-constraint clash into the error the app shows.
create function public.reservation_clash(p_constraint text) returns void
language plpgsql set search_path='' as $$
begin
  perform public.fail(case p_constraint when 'reservations_member_busy' then 'member_busy'
    when 'reservations_coach_busy' then 'coach_busy' else 'slot_taken' end, 'P0002');
end $$;

-- p_slots: [{"amenity": 1, "start": "2026-10-25T02:00:00Z"}, ...] in 30-minute slots. Touching slots of one amenity become
-- one booking (max 2 hours); all bookings are made, or none.
create function public.book_slots(p_slots jsonb) returns setof public.reservations
language plpgsql security definer set search_path='' as $$
declare
  v_uid uuid := auth.uid(); v_open time; v_close time; v_day date; v_today date := (now() at time zone 'Asia/Manila')::date;
  b record; v_am public.amenities; v_res public.reservations; v_con text;
begin
  perform public.require_active_user();
  if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
  if p_slots is null or jsonb_typeof(p_slots) <> 'array' or jsonb_array_length(p_slots) not between 1 and 48
     or exists (select 1 from jsonb_array_elements(p_slots) e where e->>'amenity' is null or e->>'start' is null) then
    perform public.fail('bad_input', '22023');
  end if;
  if exists (select 1 from jsonb_array_elements(p_slots) e where extract(epoch from (e->>'start')::timestamptz)::bigint % 1800 <> 0) then
    perform public.fail('off_grid', '22023');
  end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;

  for b in
    with s as (select distinct (e->>'amenity')::bigint a, (e->>'start')::timestamptz t from jsonb_array_elements(p_slots) e),
    g as (select a, t, t - (row_number() over (partition by a order by t)) * interval '30 minutes' grp from s)
    select a, min(t) s, max(t) + interval '30 minutes' e, count(*) n from g group by a, grp order by min(t), a
  loop
    if b.n > 4 then perform public.fail('too_long', '22023'); end if;
    if b.s <= now() then perform public.fail('past_slot', '22023'); end if;
    v_day := (b.s at time zone 'Asia/Manila')::date;
    if v_day > v_today + 14 then perform public.fail('too_far_ahead', '22023'); end if;
    if b.s < (v_day + v_open) at time zone 'Asia/Manila' or b.e > (v_day + v_close) at time zone 'Asia/Manila' then
      perform public.fail('outside_gym_hours', '22023');
    end if;
    select * into v_am from public.amenities where amenity_id = b.a and is_active;
    if not found then perform public.fail('not_found', '22023'); end if;
    if v_am.status = 'unavailable' and (v_am.back_at is null or v_am.back_at > b.s) then perform public.fail('amenity_unavailable', 'P0002'); end if;
    if not exists (select 1 from public.user_amenities ua where ua.member_id = v_uid and ua.amenity_id = b.a and ua.status = 'active'
                   and (ua.expires_at is null or ua.expires_at >= b.e)) then
      perform public.fail('no_pass', '22023');
    end if;
    if exists (select 1 from public.training_sessions t where t.member_id = v_uid and t.status = 'scheduled'
               and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(b.s, b.e)) then
      perform public.fail('member_busy', 'P0002');
    end if;
    begin
      insert into public.reservations (member_id, amenity_id, starts_at, ends_at, created_by)
      values (v_uid, b.a, b.s, b.e, v_uid) returning * into v_res;
    exception when exclusion_violation then
      get stacked diagnostics v_con = constraint_name;
      perform public.reservation_clash(v_con);
    end;
    perform public.audit('reservation.book', 'reservations', v_res.reservation_id::text,
      jsonb_build_object('amenity', b.a, 'starts_at', b.s, 'ends_at', b.e));
    return next v_res;
  end loop;
end $$;

-- Before it starts: the member (no reason needed), the coach on it, or staff (reason required) can cancel; that frees the time
-- and its cooldown. Once started, the member ends it early instead (end_reservation), so the cooldown follows the real end.
create function public.cancel_reservation(p_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations;
begin
  perform public.require_active_user();
  select * into v from public.reservations where reservation_id = p_id for update;
  if not found or (v.member_id <> v_uid and v.coach_id is distinct from v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v.member_id <> v_uid and length(btrim(coalesce(p_reason, ''))) not between 1 and 500 then perform public.fail('bad_reason', '22023'); end if;
  if v.status <> 'confirmed' then perform public.fail('already_processed', 'P0002'); end if;
  if v.starts_at <= now() then perform public.fail('already_started', 'P0002'); end if;
  update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid, cancel_reason = nullif(btrim(p_reason), '')
    where reservation_id = p_id;
  if v.member_id <> v_uid then
    perform public.notify(v.member_id, 'amenity', 'Booking cancelled', btrim(p_reason), jsonb_build_object('reservation_id', p_id));
  end if;
  perform public.audit('reservation.cancel', 'reservations', p_id::text, jsonb_build_object('reason', nullif(btrim(p_reason), '')));
end $$;

create function public.end_reservation(p_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations;
begin
  perform public.require_active_user();
  select * into v from public.reservations where reservation_id = p_id for update;
  if not found or (v.member_id <> v_uid and v.coach_id is distinct from v_uid and not public.is_staff()) then
    perform public.fail('not_found', '22023');
  end if;
  if v.status <> 'confirmed' or not (v.starts_at < now() and now() < v.ends_at) then perform public.fail('not_in_use', 'P0002'); end if;
  update public.reservations set ends_at = now() where reservation_id = p_id;
  perform public.audit('reservation.end', 'reservations', p_id::text, jsonb_build_object('planned_end', v.ends_at));
end $$;

-- The coach running a session adds time in 30-minute steps. The exclusion constraints reject any clash with the next
-- booking's time or cooldown in the same statement, so a booking made at the same moment cannot be overwritten.
create function public.extend_reservation(p_id bigint, p_minutes integer) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v public.reservations; v_end timestamptz; v_day date; v_close time; v_con text;
begin
  perform public.require_active_user();
  select * into v from public.reservations where reservation_id = p_id for update;
  if not found or v.coach_id is distinct from v_uid then perform public.fail('not_found', '22023'); end if;
  if p_minutes is null or p_minutes not in (30, 60, 90, 120) then perform public.fail('bad_input', '22023'); end if;
  if v.status <> 'confirmed' or v.ends_at <= now() then perform public.fail('already_processed', 'P0002'); end if;
  v_end := v.ends_at + make_interval(mins => p_minutes);
  v_day := (v.starts_at at time zone 'Asia/Manila')::date;
  select closes_at into v_close from public.gym_settings where id = 1;
  if v_end > (v_day + v_close) at time zone 'Asia/Manila' then perform public.fail('outside_gym_hours', '22023'); end if;
  if exists (select 1 from public.training_sessions t where t.status = 'scheduled' and t.training_session_id is distinct from v.training_session_id
             and (t.member_id = v.member_id or t.coach_id = v.coach_id) and tstzrange(t.scheduled_start, t.scheduled_end) && tstzrange(v.ends_at, v_end)) then
    perform public.fail('member_busy', 'P0002');
  end if;
  begin
    update public.reservations set ends_at = v_end, extended_at = now(), extended_by = v_uid where reservation_id = p_id;
  exception when exclusion_violation then
    get stacked diagnostics v_con = constraint_name;
    perform public.reservation_clash(v_con);
  end;
  perform public.notify(v.member_id, 'amenity', 'Session extended',
    to_char(v_end at time zone 'Asia/Manila', 'HH24:MI'), jsonb_build_object('reservation_id', p_id));
  perform public.audit('reservation.extend', 'reservations', p_id::text, jsonb_build_object('minutes', p_minutes, 'ends_at', v_end));
end $$;

-- One Manila day of the grid: gym hours, active amenities, and every booking's time and cooldown. Other members' bookings
-- carry no id or name, only the time they hold.
create function public.amenity_grid(p_day date) returns jsonb
language plpgsql stable security definer set search_path='' as $$
declare v_open time; v_close time; v_from timestamptz; v_to timestamptz;
begin
  perform public.require_active_user();
  if p_day is null then perform public.fail('bad_input', '22023'); end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  v_from := p_day::timestamp at time zone 'Asia/Manila'; v_to := (p_day + 1)::timestamp at time zone 'Asia/Manila';
  return jsonb_build_object(
    'now', now(),
    'opens', (p_day + v_open) at time zone 'Asia/Manila',
    'closes', (p_day + v_close) at time zone 'Asia/Manila',
    'amenities', (select coalesce(jsonb_agg(jsonb_build_object('id', a.amenity_id, 'name', a.name, 'status', a.status, 'back', a.back_at) order by a.name), '[]')
                  from public.amenities a where a.is_active),
    'blocks', (select coalesce(jsonb_agg(jsonb_build_object('amenity', r.amenity_id, 'starts', r.starts_at, 'ends', r.ends_at,
                  'cooldown', public.plus_cooldown(r.ends_at), 'mine', r.member_id = auth.uid(),
                  'id', case when r.member_id = auth.uid() then r.reservation_id end) order by r.starts_at), '[]')
               from public.reservations r
               where r.status = 'confirmed' and tstzrange(r.starts_at, public.plus_cooldown(r.ends_at)) && tstzrange(v_from, v_to)));
end $$;

revoke all on function public.reservation_clash(text) from public, anon, authenticated;
revoke all on function public.book_slots(jsonb), public.cancel_reservation(bigint, text), public.end_reservation(bigint),
  public.extend_reservation(bigint, integer), public.amenity_grid(date) from public, anon;
grant execute on function public.book_slots(jsonb), public.cancel_reservation(bigint, text), public.end_reservation(bigint),
  public.extend_reservation(bigint, integer), public.amenity_grid(date) to authenticated;
