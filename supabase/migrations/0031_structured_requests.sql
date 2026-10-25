-- Phase 7 (D7/D8, 2026-10-22): structured training requests on the existing time_requests.
-- A member request may name an amenity. The coach approves, declines with a reason, or proposes another time (and
-- amenity) with a reason; the proposal expires 24 hours after it is made or when its time starts, whichever is first.
-- The member accepts that exact proposal (by version), asks for another time (back to the same coach), or declines.
-- Confirming creates the training session and, with an amenity, its reservation in the same transaction, so the
-- reservation constraints (0029) decide; on any clash nothing changes and the request stays open.
-- Every status change is appended to time_request_events. D7: a session stops blocking new requests once it has ended.

alter table public.time_requests
  add column amenity_id          bigint references public.amenities (amenity_id),
  add column proposed_start      timestamptz,
  add column proposed_end        timestamptz,
  add column proposed_amenity_id bigint references public.amenities (amenity_id),
  add column proposal_reason     text,
  add column proposed_at         timestamptz,
  add column proposal_expires_at timestamptz,
  add column decline_reason      text,
  add column version             integer not null default 0,
  add constraint time_requests_proposal_shape check (status <> 'proposed' or (proposed_start is not null and proposed_end > proposed_start and proposal_expires_at is not null));

-- a proposed request is still the member's one open request to that coach (0021)
drop index public.time_requests_one_pending_member_per_coach;
create unique index time_requests_one_pending_member_per_coach
  on public.time_requests (member_id, coach_id)
  where status in ('pending', 'proposed') and requested_by = 'member' and training_session_id is null;

-- ---------- history ----------
create table public.time_request_events (
  event_id        bigint      generated always as identity primary key,
  time_request_id bigint      not null references public.time_requests (time_request_id) on delete cascade,
  actor_id        uuid        references public.profiles (id),
  at              timestamptz not null default now(),
  from_status     text,
  to_status       text        not null,
  version         integer     not null,
  reason          text,
  details         jsonb
);
create index time_request_events_request_idx on public.time_request_events (time_request_id, event_id);
alter table public.time_request_events enable row level security;
revoke all on public.time_request_events from public, anon, authenticated;
grant select on public.time_request_events to authenticated;
create policy time_request_events_select on public.time_request_events for select to authenticated
  using (public.is_active_user() and (public.is_staff() or exists (
    select 1 from public.time_requests t where t.time_request_id = time_request_events.time_request_id
      and (t.coach_id = auth.uid() or t.member_id = auth.uid()))));

create function public.record_time_request_event() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if tg_op = 'INSERT' or old.status is distinct from new.status or old.version <> new.version then
    insert into public.time_request_events (time_request_id, actor_id, from_status, to_status, version, reason, details)
    values (new.time_request_id, auth.uid(), case when tg_op = 'UPDATE' then old.status::text end, new.status::text, new.version,
      case when new.status = 'proposed' then new.proposal_reason when new.status = 'rejected' then new.decline_reason end,
      jsonb_build_object('start', new.requested_start, 'end', new.requested_end, 'amenity', new.amenity_id,
        'proposed_start', new.proposed_start, 'proposed_end', new.proposed_end, 'proposed_amenity', new.proposed_amenity_id));
  end if;
  return null;
end $$;
revoke all on function public.record_time_request_event() from public, anon, authenticated;
create trigger time_requests_history after insert or update on public.time_requests
  for each row execute function public.record_time_request_event();

-- ---------- helpers ----------
-- D7: a scheduled session only blocks new requests for the pair until its booked end has passed.
create or replace function public.assert_no_active_coaching_session(p_coach uuid, p_member uuid) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
    perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('coaching-session/' || p_coach::text || '/' || p_member::text, 0));
    if exists (select 1 from public.training_sessions
               where coach_id = p_coach and member_id = p_member and status = 'scheduled' and scheduled_end > now()) then
        perform public.fail('active_coaching_session', 'P0002');
    end if;
end;
$$;

-- A time that names an amenity follows the booking rules: future, 30-minute grid, inside gym hours on one Manila day, at most 2 hours.
create function public.check_session_time(p_start timestamptz, p_end timestamptz, p_amenity_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_open time; v_close time; v_day date;
begin
  if p_start is null or p_end is null or p_end <= p_start then perform public.fail('bad_input', '22023'); end if;
  if p_start <= now() then perform public.fail('past_slot', '22023'); end if;
  if p_amenity_id is null then return; end if;
  if not exists (select 1 from public.amenities where amenity_id = p_amenity_id and is_active) then perform public.fail('not_found', '22023'); end if;
  if extract(epoch from p_start)::bigint % 1800 <> 0 or extract(epoch from p_end)::bigint % 1800 <> 0 then perform public.fail('off_grid', '22023'); end if;
  if p_end - p_start > interval '2 hours' then perform public.fail('too_long', '22023'); end if;
  select opens_at, closes_at into v_open, v_close from public.gym_settings where id = 1;
  v_day := (p_start at time zone 'Asia/Manila')::date;
  if p_start < (v_day + v_open) at time zone 'Asia/Manila' or p_end > (v_day + v_close) at time zone 'Asia/Manila' then
    perform public.fail('outside_gym_hours', '22023');
  end if;
end $$;

-- Reserves the amenity for a coach session (D6: the coach session covers it, no member pass needed), or moves the
-- session's existing reservation. The coach must have opened that time with a posted shift (0028).
create function public.place_session_amenity(p_session bigint, p_amenity_id bigint, p_start timestamptz, p_end timestamptz) returns void
language plpgsql security definer set search_path='' as $$
declare v_s public.training_sessions; v_am public.amenities; v_con text;
begin
  select * into v_s from public.training_sessions where training_session_id = p_session;
  if not exists (select 1 from public.shift_posts where coach_id = v_s.coach_id and status = 'scheduled' and starts_at <= p_start and ends_at >= p_end) then
    perform public.fail('coach_unavailable', 'P0002');
  end if;
  select * into v_am from public.amenities where amenity_id = p_amenity_id and is_active;
  if not found then perform public.fail('not_found', '22023'); end if;
  if v_am.status = 'unavailable' and (v_am.back_at is null or v_am.back_at > p_start) then perform public.fail('amenity_unavailable', 'P0002'); end if;
  begin
    update public.reservations set starts_at = p_start, ends_at = p_end, amenity_id = p_amenity_id
      where training_session_id = p_session and status = 'confirmed';
    if not found then
      insert into public.reservations (member_id, amenity_id, coach_id, training_session_id, starts_at, ends_at, created_by)
      values (v_s.member_id, p_amenity_id, v_s.coach_id, p_session, p_start, p_end, auth.uid());
    end if;
  exception when exclusion_violation then
    get stacked diagnostics v_con = constraint_name;
    perform public.reservation_clash(v_con);
  end;
end $$;
revoke all on function public.check_session_time(timestamptz, timestamptz, bigint), public.place_session_amenity(bigint, bigint, timestamptz, timestamptz)
  from public, anon, authenticated;

-- ---------- member asks ----------
create function public.request_session(p_coach_id uuid, p_start timestamptz, p_end timestamptz, p_amenity_id bigint default null, p_message text default null)
returns bigint
language plpgsql security definer set search_path='' as $$
declare v_id bigint;
begin
  perform public.require_active_user();
  if auth.uid() = p_coach_id then perform public.fail('forbidden', '42501'); end if;
  perform public.check_session_time(p_start, p_end, p_amenity_id);
  v_id := public.request_time(p_coach_id, p_start, p_end, p_message);
  update public.time_requests set amenity_id = p_amenity_id where time_request_id = v_id;
  return v_id;
end $$;

-- ---------- coach proposes ----------
create function public.propose_time(p_request_id bigint, p_start timestamptz, p_end timestamptz, p_amenity_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_r public.time_requests;
begin
  perform public.require_active_user();
  select * into v_r from public.time_requests where time_request_id = p_request_id for update;
  if not found or v_r.coach_id <> auth.uid() then perform public.fail('not_found', '22023'); end if;
  if v_r.requested_by <> 'member' or v_r.training_session_id is not null
     or not (v_r.status = 'pending' or (v_r.status = 'proposed' and v_r.proposal_expires_at <= now())) then
    perform public.fail('already_processed', 'P0002');
  end if;
  if length(btrim(coalesce(p_reason, ''))) not between 1 and 500 then perform public.fail('bad_reason', '22023'); end if;
  perform public.check_session_time(p_start, p_end, p_amenity_id);
  update public.time_requests set status = 'proposed', proposed_start = p_start, proposed_end = p_end, proposed_amenity_id = p_amenity_id,
    proposal_reason = btrim(p_reason), proposed_at = now(), proposal_expires_at = least(now() + interval '24 hours', p_start), version = version + 1
    where time_request_id = p_request_id;
  perform public.notify(v_r.member_id, 'time_request', 'Your coach proposed another time', btrim(p_reason), jsonb_build_object('time_request_id', p_request_id));
end $$;

-- ---------- member answers a proposal ----------
-- Returns 'confirmed', or 'expired' when the proposal ran out (the request then goes back to the coach).
create function public.accept_proposal(p_request_id bigint, p_version integer) returns text
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v_r public.time_requests; v_session bigint;
begin
  perform public.require_active_user();
  select * into v_r from public.time_requests where time_request_id = p_request_id;
  if not found or v_r.member_id is distinct from v_uid or v_r.requested_by <> 'member' then perform public.fail('not_found', '22023'); end if;
  if v_r.status <> 'proposed' then perform public.fail('already_processed', 'P0002'); end if;
  perform public.assert_no_active_coaching_session(v_r.coach_id, v_uid);   -- pair lock before the request lock, as in 0022
  select * into v_r from public.time_requests where time_request_id = p_request_id for update;
  if v_r.status <> 'proposed' then perform public.fail('already_processed', 'P0002'); end if;
  if v_r.version <> p_version then perform public.fail('stale_proposal', 'P0002'); end if;
  if v_r.proposal_expires_at <= now() then
    update public.time_requests set status = 'pending' where time_request_id = p_request_id;
    return 'expired';
  end if;
  if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
  insert into public.training_sessions (coach_id, member_id, title, scheduled_start, scheduled_end)
  values (v_r.coach_id, v_uid, 'Training session', v_r.proposed_start, v_r.proposed_end) returning training_session_id into v_session;
  if v_r.proposed_amenity_id is not null then
    perform public.place_session_amenity(v_session, v_r.proposed_amenity_id, v_r.proposed_start, v_r.proposed_end);
  end if;
  update public.time_requests set status = 'approved', requested_start = v_r.proposed_start, requested_end = v_r.proposed_end,
    amenity_id = v_r.proposed_amenity_id, responded_at = now() where time_request_id = p_request_id;
  perform public.notify(v_r.coach_id, 'time_request', 'Proposal accepted', null, jsonb_build_object('time_request_id', p_request_id));
  return 'confirmed';
end $$;

create function public.request_another_time(p_request_id bigint, p_start timestamptz, p_end timestamptz, p_amenity_id bigint default null, p_message text default null) returns void
language plpgsql security definer set search_path='' as $$
declare v_r public.time_requests;
begin
  perform public.require_active_user();
  select * into v_r from public.time_requests where time_request_id = p_request_id for update;
  if not found or v_r.member_id is distinct from auth.uid() or v_r.requested_by <> 'member' then perform public.fail('not_found', '22023'); end if;
  if v_r.status <> 'proposed' then perform public.fail('already_processed', 'P0002'); end if;
  perform public.check_session_time(p_start, p_end, p_amenity_id);
  update public.time_requests set status = 'pending', requested_start = p_start, requested_end = p_end, amenity_id = p_amenity_id,
    message = coalesce(nullif(btrim(p_message), ''), message), proposed_start = null, proposed_end = null, proposed_amenity_id = null,
    proposal_reason = null, proposed_at = null, proposal_expires_at = null, version = version + 1
    where time_request_id = p_request_id;
  perform public.notify(v_r.coach_id, 'time_request', 'Another time requested', null, jsonb_build_object('time_request_id', p_request_id));
end $$;

-- Coach declines a member request; member declines a proposal or a coach invitation. Always with a reason.
create function public.decline_time_request(p_request_id bigint, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_uid uuid := auth.uid(); v_r public.time_requests; v_coach_turn boolean;
begin
  perform public.require_active_user();
  select * into v_r from public.time_requests where time_request_id = p_request_id for update;
  if not found or v_uid not in (v_r.coach_id, coalesce(v_r.member_id, v_r.coach_id)) then perform public.fail('not_found', '22023'); end if;
  if length(btrim(coalesce(p_reason, ''))) not between 1 and 500 then perform public.fail('bad_reason', '22023'); end if;
  v_coach_turn := v_r.status = 'pending' or (v_r.status = 'proposed' and v_r.proposal_expires_at <= now());
  if not ((v_uid = v_r.coach_id and v_r.requested_by = 'member' and v_coach_turn)
       or (v_uid = v_r.member_id and v_r.status = 'proposed')
       or (v_uid = v_r.member_id and v_r.requested_by = 'coach' and v_r.status = 'pending')) then
    perform public.fail('already_processed', 'P0002');
  end if;
  update public.time_requests set status = 'rejected', decline_reason = btrim(p_reason), responded_at = now() where time_request_id = p_request_id;
  perform public.notify(case when v_uid = v_r.coach_id then v_r.member_id else v_r.coach_id end, 'time_request', 'Request declined', btrim(p_reason),
    jsonb_build_object('time_request_id', p_request_id));
end $$;

-- ---------- existing functions, extended ----------
-- 0010 plus: a member can also withdraw a request while a proposal is waiting.
create or replace function public.cancel_time_request(p_request_id bigint) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_r public.time_requests;
begin
	perform public.require_active_user();

	select * into v_r from public.time_requests where time_request_id = p_request_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if not ((v_r.requested_by = 'member' and v_r.member_id = v_uid) or (v_r.requested_by = 'coach' and v_r.coach_id = v_uid)) then
		perform public.fail('forbidden', '42501');
	end if;
	if v_r.status not in ('pending', 'proposed') then perform public.fail('already_processed', 'P0002'); end if;

	update public.time_requests set status = 'cancelled' where time_request_id = p_request_id;
end;
$$;

-- 0022 plus: an expired proposal is the coach's turn again; approving a request that names an amenity reserves it inside
-- the coach's shift; approving a reschedule moves the session's reservation too. Any clash rolls the whole approval back.
create or replace function public.respond_time_request(p_request_id bigint, p_approve boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_r public.time_requests;
	v_member uuid;
	v_session bigint;
	v_amenity bigint;
begin
	perform public.require_active_user();

	select * into v_r from public.time_requests where time_request_id = p_request_id;
	if not found then perform public.fail('not_found', '22023'); end if;
	if not (v_r.status = 'pending' or (v_r.status = 'proposed' and v_r.proposal_expires_at <= now())) then perform public.fail('already_processed', 'P0002'); end if;

	if v_r.member_id is null then
		if not (public.is_active_member(v_uid) and public.coaches_member(v_r.coach_id, v_uid)) then
			perform public.fail('forbidden', '42501');
		end if;
		if not p_approve then perform public.fail('bad_input', '22023'); end if;
		v_member := v_uid;
	elsif v_r.requested_by = 'member' then
		if v_uid <> v_r.coach_id then perform public.fail('forbidden', '42501'); end if;
		v_member := v_r.member_id;
	else
		if v_uid <> v_r.member_id then perform public.fail('forbidden', '42501'); end if;
		v_member := v_r.member_id;
	end if;

	if p_approve and v_r.training_session_id is null then
		perform public.assert_no_active_coaching_session(v_r.coach_id, v_member);
	end if;

	select * into v_r from public.time_requests where time_request_id = p_request_id for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if not (v_r.status = 'pending' or (v_r.status = 'proposed' and v_r.proposal_expires_at <= now())) then perform public.fail('already_processed', 'P0002'); end if;

	if p_approve then
		if v_r.training_session_id is null and not public.is_active_member(v_member) then
			perform public.fail('no_active_membership', '22023');
		end if;

		if v_r.training_session_id is not null then
			update public.training_sessions
			set scheduled_start = v_r.requested_start, scheduled_end = v_r.requested_end
			where training_session_id = v_r.training_session_id and status = 'scheduled';
			if not found then perform public.fail('already_processed', 'P0002'); end if;
			select amenity_id into v_amenity from public.reservations where training_session_id = v_r.training_session_id and status = 'confirmed';
			if v_amenity is not null then
				perform public.check_session_time(v_r.requested_start, v_r.requested_end, v_amenity);
				perform public.place_session_amenity(v_r.training_session_id, v_amenity, v_r.requested_start, v_r.requested_end);
			end if;
		else
			insert into public.training_sessions (coach_id, member_id, title, scheduled_start, scheduled_end)
			values (v_r.coach_id, v_member, 'Training session', v_r.requested_start, v_r.requested_end)
			returning training_session_id into v_session;
			if v_r.amenity_id is not null then
				perform public.place_session_amenity(v_session, v_r.amenity_id, v_r.requested_start, v_r.requested_end);
			end if;
		end if;

		update public.time_requests set status = 'approved', member_id = v_member, responded_at = now() where time_request_id = p_request_id;
	else
		update public.time_requests set status = 'rejected', responded_at = now() where time_request_id = p_request_id;
	end if;

	perform public.notify(case when v_uid = v_r.coach_id then v_member else v_r.coach_id end,
	                      'time_request', case when p_approve then 'Time request approved' else 'Time request declined' end,
	                      null, jsonb_build_object('time_request_id', p_request_id));
end;
$$;

-- 0005 plus: cancelling a session frees its amenity reservation.
create or replace function public.cancel_session(p_session_id bigint, p_reason text default null) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_s public.training_sessions;
begin
	perform public.require_active_user();

	select * into v_s from public.training_sessions
	where training_session_id = p_session_id and (coach_id = v_uid or member_id = v_uid) for update;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_s.status <> 'scheduled' then perform public.fail('already_processed', 'P0002'); end if;

	update public.training_sessions
	set status = 'cancelled', notes = coalesce(nullif(btrim(p_reason), ''), notes)
	where training_session_id = p_session_id;

	update public.time_requests set status = 'cancelled' where training_session_id = p_session_id and status = 'pending';
	update public.reservations set status = 'cancelled', cancelled_at = now(), cancelled_by = v_uid,
	  cancel_reason = coalesce(nullif(btrim(p_reason), ''), 'Session cancelled')
	  where training_session_id = p_session_id and status = 'confirmed';

	perform public.notify(case when v_uid = v_s.coach_id then v_s.member_id else v_s.coach_id end,
	                      'system', 'A training session was cancelled', nullif(btrim(p_reason), ''), jsonb_build_object('training_session_id', p_session_id));
end;
$$;

-- 0029 plus: clashes on a session's own times read as "busy" too.
create or replace function public.reservation_clash(p_constraint text) returns void
language plpgsql set search_path='' as $$
begin
  perform public.fail(case when p_constraint in ('reservations_member_busy', 'training_sessions_no_member_overlap') then 'member_busy'
    when p_constraint in ('reservations_coach_busy', 'training_sessions_no_coach_overlap') then 'coach_busy' else 'slot_taken' end, 'P0002');
end $$;

-- 0029 plus: extending a coach session moves the training session's end with its reservation, in the same statement block.
create or replace function public.extend_reservation(p_id bigint, p_minutes integer) returns void
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
    update public.training_sessions set scheduled_end = v_end where training_session_id = v.training_session_id and status = 'scheduled';
  exception when exclusion_violation then
    get stacked diagnostics v_con = constraint_name;
    perform public.reservation_clash(v_con);
  end;
  perform public.notify(v.member_id, 'amenity', 'Session extended',
    to_char(v_end at time zone 'Asia/Manila', 'HH24:MI'), jsonb_build_object('reservation_id', p_id));
  perform public.audit('reservation.extend', 'reservations', p_id::text, jsonb_build_object('minutes', p_minutes, 'ends_at', v_end));
end $$;

revoke all on function public.request_session(uuid, timestamptz, timestamptz, bigint, text), public.propose_time(bigint, timestamptz, timestamptz, bigint, text),
  public.accept_proposal(bigint, integer), public.request_another_time(bigint, timestamptz, timestamptz, bigint, text),
  public.decline_time_request(bigint, text) from public, anon;
grant execute on function public.request_session(uuid, timestamptz, timestamptz, bigint, text), public.propose_time(bigint, timestamptz, timestamptz, bigint, text),
  public.accept_proposal(bigint, integer), public.request_another_time(bigint, timestamptz, timestamptz, bigint, text),
  public.decline_time_request(bigint, text) to authenticated;
