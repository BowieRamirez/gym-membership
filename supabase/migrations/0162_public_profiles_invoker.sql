-- 0162 (owner, 2026-11-25): Supabase lint 0010 (security_definer_view) on public.public_profiles. The view ran as its owner so
-- it could read profiles past the narrowed policy (0140). Now the view runs as the caller (security_invoker) and the owner-run
-- part is a closed security definer function with the same row rule and columns, so who sees what does not change.
-- ponytail: the function returns every visible row before PostgREST filters; fine at gym size, add parameters if profiles grow large.
create function public.public_profile_rows()
returns table (id uuid, full_name text, first_name text, last_name text, avatar_path text, is_active boolean)
language sql stable security definer set search_path = '' as $$
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
$$;
revoke all on function public.public_profile_rows() from public, anon;
grant execute on function public.public_profile_rows() to authenticated;

create or replace view public.public_profiles with (security_invoker = true, security_barrier = true) as
select id, full_name, first_name, last_name, avatar_path, is_active from public.public_profile_rows();
revoke all on public.public_profiles from public, anon, authenticated;
grant select on public.public_profiles to authenticated;
