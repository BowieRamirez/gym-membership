-- Phase 8 (D9, 2026-10-23): physical coach visits. A coach is "in the gym" while they have an open visit.
-- The coach checks themselves in and out, or the front desk does it for them (check_ins:record); each row keeps who did what.
-- A visit still open at the gym's closing time is closed by a job every 5 minutes and marked auto_closed, at that closing time.
-- Check-in is refused while the gym is closed, so a visit left open for those few minutes after closing does no harm.
-- Presence only hides absent coaches from the "find a coach" list and blocks a new hire of an absent coach.
-- Existing hires, chats, sessions, bookings and booking a coach for later (inside their posted shifts) do not depend on it.

create table public.coach_visits (
  coach_visit_id bigint      generated always as identity primary key,
  coach_id       uuid        not null references public.coaches (user_id),
  checked_in_at  timestamptz not null default now(),
  checked_in_by  uuid        not null references public.profiles (id),
  checked_out_at timestamptz,
  checked_out_by uuid        references public.profiles (id),   -- null on an auto checkout
  auto_closed    boolean     not null default false,
  check (checked_out_at is null or checked_out_at >= checked_in_at)
);
create unique index coach_visits_one_open on public.coach_visits (coach_id) where checked_out_at is null;
create index coach_visits_coach_idx on public.coach_visits (coach_id, checked_in_at);

alter table public.coach_visits enable row level security;
revoke all on public.coach_visits from public, anon, authenticated;
grant select on public.coach_visits to authenticated;
-- everyone sees who is in right now; past visits only to the coach and staff
create policy coach_visits_select on public.coach_visits for select to authenticated
  using (public.is_active_user() and (checked_out_at is null or coach_id = auth.uid() or public.is_staff()));

-- Closes every visit whose check-in day has reached closing time (Manila wall clock), at that closing time.
create function public.close_coach_visits() returns int
language plpgsql security definer set search_path='' as $$
declare v_count int;
begin
  update public.coach_visits v
  set checked_out_at = greatest(v.checked_in_at, ((v.checked_in_at at time zone 'Asia/Manila')::date + g.closes_at) at time zone 'Asia/Manila'),
      auto_closed = true
  from public.gym_settings g
  where g.id = 1 and v.checked_out_at is null
    and now() >= ((v.checked_in_at at time zone 'Asia/Manila')::date + g.closes_at) at time zone 'Asia/Manila';
  get diagnostics v_count = row_count;
  return v_count;
end $$;

-- the coach themselves (p_coach null or their own id), or staff with check_ins:record for any coach
create function public.coach_visit_actor(p_coach uuid) returns uuid
language plpgsql security definer set search_path='' as $$
declare v_coach uuid := coalesce(p_coach, auth.uid());
begin
  if v_coach = auth.uid() then perform public.require_active_user(); else perform public.require_permission('check_ins:record'); end if;
  if not exists (select 1 from public.coaches c join public.profiles p on p.id = c.user_id where c.user_id = v_coach and p.is_active) then
    perform public.fail('not_found', '22023');
  end if;
  perform 1 from public.coaches where user_id = v_coach for update;   -- one check-in/out per coach at a time
  return v_coach;
end $$;

-- Checking in twice is harmless: it returns the visit already open.
create function public.coach_check_in(p_coach uuid default null) returns bigint
language plpgsql security definer set search_path='' as $$
declare v_coach uuid := public.coach_visit_actor(p_coach); v_id bigint; v_open time; v_close time;
  v_now time := (now() at time zone 'Asia/Manila')::time;
begin
  select coach_visit_id into v_id from public.coach_visits where coach_id = v_coach and checked_out_at is null;
  if found then return v_id; end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  if v_now < v_open or v_now >= v_close then perform public.fail('gym_closed', '22023'); end if;
  insert into public.coach_visits (coach_id, checked_in_by) values (v_coach, auth.uid()) returning coach_visit_id into v_id;
  return v_id;
end $$;

-- Checking out when not in is harmless: returns false.
create function public.coach_check_out(p_coach uuid default null) returns boolean
language plpgsql security definer set search_path='' as $$
declare v_coach uuid := public.coach_visit_actor(p_coach);
begin
  update public.coach_visits set checked_out_at = now(), checked_out_by = auth.uid()
  where coach_id = v_coach and checked_out_at is null;
  return found;
end $$;

revoke all on function public.close_coach_visits(), public.coach_visit_actor(uuid) from public, anon, authenticated;
revoke all on function public.coach_check_in(uuid), public.coach_check_out(uuid) from public, anon;
grant execute on function public.coach_check_in(uuid), public.coach_check_out(uuid) to authenticated;

-- A new hire needs the coach in the gym, so a stale "find a coach" list cannot hire someone who left. Otherwise as in 0005.
create or replace function public.hire_coach(p_coach_id uuid) returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_id bigint;
begin
	perform public.require_active_user();
	if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;

	if not exists (
		select 1 from public.coaches c join public.profiles p on p.id = c.user_id
		where c.user_id = p_coach_id and c.is_available and p.is_active
	) then
		perform public.fail('not_found', '22023');
	end if;
	if not exists (select 1 from public.coach_visits where coach_id = p_coach_id and checked_out_at is null) then perform public.fail('coach_not_in_gym', '22023'); end if;

	insert into public.coach_hires (member_id, coach_id) values (v_uid, p_coach_id)
	returning coach_hire_id into v_id;

	perform public.notify(p_coach_id, 'system', 'New trainee', (select full_name from public.profiles where id = v_uid), jsonb_build_object('member_id', v_uid));
	return v_id;
end;
$$;

select cron.schedule('close-coach-visits', '*/5 * * * *', $$select public.close_coach_visits()$$);
