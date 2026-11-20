-- B2 (2026-11-20): a coach cannot mark attendance for a session that has not started yet.
create or replace function public.record_session_attendance(p_session_id bigint, p_status public.attendance_status, p_notes text default null)
returns void
language plpgsql security definer
set search_path = ''
as $$
declare
	v_uid uuid := auth.uid();
	v_s public.training_sessions;
begin
	perform public.require_active_user();

	select * into v_s from public.training_sessions where training_session_id = p_session_id and coach_id = v_uid for update;
	if not found then perform public.fail('forbidden', '42501'); end if;
	if v_s.status = 'cancelled' then perform public.fail('already_processed', 'P0002'); end if;
	if v_s.scheduled_start > now() then perform public.fail('too_soon', 'P0002'); end if;

	insert into public.session_attendance (training_session_id, status, checked_in_at, recorded_by, notes)
	values (p_session_id, p_status, case when p_status in ('present', 'late') then now() end, v_uid, nullif(btrim(p_notes), ''))
	on conflict (training_session_id) do update
	set status = excluded.status, checked_in_at = excluded.checked_in_at, recorded_by = excluded.recorded_by, notes = excluded.notes;

	update public.training_sessions
	set status = case when p_status = 'absent' then 'no_show'::public.session_status else 'completed'::public.session_status end
	where training_session_id = p_session_id;

	perform public.notify(v_s.member_id, 'attendance', 'Attendance recorded', p_status::text, jsonb_build_object('training_session_id', p_session_id));
end;
$$;

revoke all on function public.record_session_attendance(bigint, public.attendance_status, text) from public, anon, authenticated;
grant execute on function public.record_session_attendance(bigint, public.attendance_status, text) to authenticated;
