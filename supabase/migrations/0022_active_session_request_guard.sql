-- Serialize booking creation for a coach/member pair, including older pending requests.
-- Existing sessions and requests are preserved; only new bookings are blocked.
create function public.assert_no_active_coaching_session(p_coach uuid, p_member uuid) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
    perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('coaching-session/' || p_coach::text || '/' || p_member::text, 0));
    if exists (select 1 from public.training_sessions
               where coach_id = p_coach and member_id = p_member and status = 'scheduled') then
        perform public.fail('active_coaching_session', 'P0002');
    end if;
end;
$$;
revoke all on function public.assert_no_active_coaching_session(uuid, uuid) from public, anon, authenticated;

create or replace function public.request_time(p_coach_id uuid, p_start timestamptz, p_end timestamptz, p_message text default null)
returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_id bigint;
begin
	perform public.require_active_user();
	if p_end <= p_start then perform public.fail('end must be after start', '22023'); end if;

	if v_uid = p_coach_id then
		if not exists (select 1 from public.coaches where user_id = v_uid) then perform public.fail('forbidden', '42501'); end if;
		insert into public.time_requests (coach_id, requested_by, requested_start, requested_end, message)
		values (v_uid, 'coach', p_start, p_end, nullif(btrim(p_message), ''))
		returning time_request_id into v_id;

		insert into public.user_notifications (user_id, type, title, reference)
		select member_id, 'time_request', 'Your coach offered an open slot', jsonb_build_object('time_request_id', v_id)
		from public.coach_hires where coach_id = v_uid and status = 'active';
	else
		if not public.is_active_member(v_uid) then perform public.fail('no_active_membership', '22023'); end if;
		if not public.coaches_member(p_coach_id, v_uid) then perform public.fail('no_active_hire', '22023'); end if;
		perform public.assert_no_active_coaching_session(p_coach_id, v_uid);
		insert into public.time_requests (coach_id, member_id, requested_by, requested_start, requested_end, message)
		values (p_coach_id, v_uid, 'member', p_start, p_end, nullif(btrim(p_message), ''))
		returning time_request_id into v_id;

		perform public.notify(p_coach_id, 'time_request', 'New time request', null, jsonb_build_object('time_request_id', v_id));
	end if;

	return v_id;
end;
$$;

create or replace function public.request_member_time(p_member_id uuid, p_start timestamptz, p_end timestamptz)
returns bigint
language plpgsql security definer
set search_path = ''
as $$
declare
    v_uid uuid := auth.uid();
    v_id bigint;
begin
    perform public.require_active_user();
    if not exists (select 1 from public.coaches where user_id = v_uid) then
        perform public.fail('forbidden', '42501');
    end if;
    if p_member_id is null or p_start is null or p_end is null or p_end <= p_start then
        perform public.fail('bad_input', '22023');
    end if;
    -- Lock the active relationship so ending the hire cannot leave a newly-created invitation behind.
    perform 1 from public.coach_hires
    where coach_id = v_uid and member_id = p_member_id and status = 'active' for update;
    if not found then perform public.fail('no_active_hire', '22023'); end if;
    if not public.is_active_member(p_member_id) then
        perform public.fail('invite_member_inactive', '22023');
    end if;
    perform public.assert_no_active_coaching_session(v_uid, p_member_id);
    insert into public.time_requests (coach_id, member_id, requested_by, requested_start, requested_end)
    values (v_uid, p_member_id, 'coach', p_start, p_end)
    returning time_request_id into v_id;
    perform public.notify(p_member_id, 'time_request', 'Your coach invited you to a session', null,
        jsonb_build_object('time_request_id', v_id));
    return v_id;
end;
$$;

create or replace function public.respond_time_request(p_request_id bigint, p_approve boolean) returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_r public.time_requests;
	v_member uuid;
begin
	perform public.require_active_user();

	select * into v_r from public.time_requests where time_request_id = p_request_id;
	if not found then perform public.fail('not_found', '22023'); end if;
	if v_r.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;

	if v_r.member_id is null then
		-- an open slot: a trainee of that coach claims it, which counts as approving
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
	if v_r.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;

	if p_approve then
		if v_r.training_session_id is null and not public.is_active_member(v_member) then
			perform public.fail('no_active_membership', '22023');
		end if;

		if v_r.training_session_id is not null then
			update public.training_sessions
			set scheduled_start = v_r.requested_start, scheduled_end = v_r.requested_end
			where training_session_id = v_r.training_session_id and status = 'scheduled';
			if not found then perform public.fail('already_processed', 'P0002'); end if;
		else
			insert into public.training_sessions (coach_id, member_id, title, scheduled_start, scheduled_end)
			values (v_r.coach_id, v_member, 'Training session', v_r.requested_start, v_r.requested_end);
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
