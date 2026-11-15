-- G29 (owner, 2026-11-15): members hire any coach at any time; the deal and the payment are between member and coach, so a
-- coach no longer has to be checked in to be hired (0032). An active membership and the coach taking members still apply.
-- Every active user can read coaches' shifts, so the Coaches page (which lists every coach, G27) can show when each is on
-- the floor; before, only that coach's own trainees could.

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

	insert into public.coach_hires (member_id, coach_id) values (v_uid, p_coach_id)
	returning coach_hire_id into v_id;

	perform public.notify(p_coach_id, 'system', 'New trainee', (select full_name from public.profiles where id = v_uid), jsonb_build_object('member_id', v_uid));
	return v_id;
end;
$$;

drop policy shift_posts_select on public.shift_posts;
create policy shift_posts_select on public.shift_posts for select to authenticated
	using (public.is_active_user());
