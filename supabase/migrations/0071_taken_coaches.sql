-- G28 (owner, 2026-11-15): the members' Coaches page tags every coach who has an active hire. Members can't read other
-- members' hires (coach_hires policy), so this returns only which coaches are hired, never by whom. Hiring is unchanged:
-- a coach can take as many members as they like.

create function public.taken_coaches() returns setof uuid
language sql stable security definer set search_path = '' as $$
	select distinct coach_id from public.coach_hires where status = 'active';
$$;
revoke all on function public.taken_coaches() from public, anon;
grant execute on function public.taken_coaches() to authenticated;
