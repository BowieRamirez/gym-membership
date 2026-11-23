-- 0140: fixes from the 2026-11-23 live system test (owner: fix 1-5 on desk-customer-shifts-2026-11-20, do not merge yet).
--
-- 1. Members and coaches could read every column of the profiles they may see (coaches' sign-in email, confirmed email, phone,
--    terms). Full rows are now only the caller's own and staff's; everyone else reads public_profiles (name, photo, active),
--    which keeps the 0121 row rule exactly.
-- 2. A coach could still check themselves in or out through the API after the coach Settings card was removed (2026-11-22):
--    checking a coach in or out now always needs check_ins:record (front desk, Admin, System admin).
-- 3. report_damage took any of the coach's own sessions, even a cancelled one or one that had not started.
-- 4. A member could not cancel a pending coach access payment (cancel_pending_payment took membership and renewal only).
-- 5. Walk-in names had no length limit (a 5,000-character name was saved). New rows are capped at 100 characters; older rows
--    are left as they are (not valid), so the one long test name does not block the migration.

-- 1 ---------------------------------------------------------------------------------------------------------------------
drop policy if exists profiles_select on public.profiles;
create policy profiles_select on public.profiles for select to authenticated
  using (public.is_active_user() and public.is_staff());
-- profiles_select_self (0006) still gives everyone their own row

-- runs as its owner (like a security definer function), so it reads profiles past the narrowed policy; the where clause is the
-- 0121 row rule, so who is listed does not change, only which columns
create or replace view public.public_profiles with (security_barrier = true) as
select p.id, p.full_name, p.first_name, p.last_name, p.avatar_path, p.is_active
from public.profiles p
where p.id = auth.uid()
   or (public.is_active_user() and (
        public.is_staff()
        or exists (select 1 from public.coaches c where c.user_id = p.id)
        or exists (select 1 from public.coach_hires h
                   where (h.coach_id = auth.uid() and h.member_id = p.id) or (h.member_id = auth.uid() and h.coach_id = p.id))
        or exists (select 1 from public.training_sessions ts
                   where (ts.coach_id = auth.uid() and ts.member_id = p.id) or (ts.member_id = auth.uid() and ts.coach_id = p.id))));
revoke all on public.public_profiles from public, anon, authenticated;
grant select on public.public_profiles to authenticated;

-- 2 ---------------------------------------------------------------------------------------------------------------------
create or replace function public.coach_visit_actor(p_coach uuid) returns uuid
language plpgsql security definer set search_path='' as $$
declare v_coach uuid := coalesce(p_coach, auth.uid());
begin
  perform public.require_permission('check_ins:record');   -- 0140: only the front desk and admins check coaches in and out
  if not exists (select 1 from public.coaches c join public.profiles p on p.id = c.user_id where c.user_id = v_coach and p.is_active) then
    perform public.fail('not_found', '22023');
  end if;
  perform 1 from public.coaches where user_id = v_coach for update;   -- one check-in/out per coach at a time
  return v_coach;
end $$;
revoke all on function public.coach_visit_actor(uuid) from public, anon, authenticated;

-- 3 ---------------------------------------------------------------------------------------------------------------------
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.report_damage(bigint, bigint, text, text)'::regprocedure);
  v_def := replace(v_def,
    $q$    v_coach := v_s.coach_id;$q$,
    $q$    if v_s.status = 'cancelled' then perform public.fail('session_cancelled', '22023'); end if;
    if v_s.scheduled_start > now() then perform public.fail('session_not_started', '22023'); end if;   -- 0140
    v_coach := v_s.coach_id;$q$);
  if v_def not like '%session_not_started%' then raise exception '0140: report_damage did not change'; end if;
  execute v_def;
end $$;
revoke all on function public.report_damage(bigint, bigint, text, text) from public, anon;
grant execute on function public.report_damage(bigint, bigint, text, text) to authenticated;

-- 4 ---------------------------------------------------------------------------------------------------------------------
do $$
declare v_def text;
begin
  v_def := pg_get_functiondef('public.cancel_pending_payment(bigint)'::regprocedure);
  v_def := replace(v_def, $q$kind in ('membership','renewal')$q$, $q$kind in ('membership','renewal','coach_addon')$q$);
  if v_def not like '%coach_addon%' then raise exception '0140: cancel_pending_payment did not change'; end if;
  execute v_def;
end $$;
revoke all on function public.cancel_pending_payment(bigint) from public, anon;
grant execute on function public.cancel_pending_payment(bigint) to authenticated;

-- 5 ---------------------------------------------------------------------------------------------------------------------
alter table public.payments add constraint payments_walk_in_name_length check (char_length(walk_in_name) <= 100) not valid;
alter table public.reservations add constraint reservations_walk_in_name_length check (char_length(walk_in_name) <= 100) not valid;
alter table public.training_sessions add constraint training_sessions_walk_in_name_length check (char_length(walk_in_name) <= 100) not valid;
