-- 0010: a member or coach withdraws a request they made that is still waiting.
-- The plan lets time_requests be changed by functions only, and had none for this.

create function public.cancel_time_request(p_request_id bigint) returns void
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
	if v_r.status <> 'pending' then perform public.fail('already_processed', 'P0002'); end if;

	update public.time_requests set status = 'cancelled' where time_request_id = p_request_id;
end;
$$;

revoke all on function public.cancel_time_request(bigint) from public, anon, authenticated;
grant execute on function public.cancel_time_request(bigint) to authenticated;
