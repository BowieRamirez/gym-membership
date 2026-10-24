-- 0037: super admin accounts are fixed from the app, and the super admin can add a sub-admin directly (Phase 13 follow-up, 2026-10-24).
--
-- Nobody signed in to the app (not even another super admin) can change a super admin's roles, deactivate or delete them. The
-- checks run on the tables, so every path is covered. The database owner (Supabase dashboard, migrations, the service role used by
-- the Edge Functions, where auth.uid() is null) still can: that is how a super admin would be removed.

create function public.is_super_admin(p_user uuid) returns boolean
language sql stable security definer set search_path = '' as $$
	select exists (select 1 from public.user_roles ur join public.roles r using (role_id) where ur.user_id = p_user and r.name = 'admin');
$$;
revoke all on function public.is_super_admin(uuid) from public, anon;

create function public.guard_super_admin_roles() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
	if auth.uid() is not null and public.is_super_admin(case when tg_op = 'DELETE' then old.user_id else new.user_id end) then
		perform public.fail('super_admin_protected', '42501');
	end if;
	return case when tg_op = 'DELETE' then old else new end;
end $$;
revoke all on function public.guard_super_admin_roles() from public, anon, authenticated;
create trigger user_roles_super_admin_guard before insert or delete on public.user_roles
	for each row execute function public.guard_super_admin_roles();

create function public.guard_super_admin_account() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
	if auth.uid() is not null and old.is_active and not new.is_active and public.is_super_admin(old.id) then
		perform public.fail('super_admin_protected', '42501');
	end if;
	return new;
end $$;
revoke all on function public.guard_super_admin_account() from public, anon, authenticated;
create trigger profiles_super_admin_guard before update of is_active on public.profiles
	for each row execute function public.guard_super_admin_account();

-- Same as 0035, plus the subadmin role, so create-user can add a sub-admin in one step.
create or replace function public.handle_new_user() returns trigger
language plpgsql security definer
set search_path = ''
as $$
declare
	v_role text := coalesce(new.raw_app_meta_data ->> 'role', 'member');
	v_fallback text := btrim(regexp_replace(split_part(new.email, '@', 1), '[^[:alpha:]]+', ' ', 'g'));
	v_first text := nullif(btrim(new.raw_user_meta_data ->> 'first_name'), '');
	v_name text := coalesce(
		nullif(btrim(new.raw_user_meta_data ->> 'full_name'), ''),
		case when char_length(v_fallback) between 2 and 60 then v_fallback else 'Member' end
	);
begin
	if v_role not in ('member', 'coach', 'employee', 'subadmin', 'admin') then
		v_role := 'member';
	end if;

	insert into public.profiles (id, username, full_name, first_name, last_name, must_change_password)
	values (
		new.id,
		coalesce(nullif(btrim(new.raw_user_meta_data ->> 'username'), ''), lower(new.email)),
		v_name,
		v_first,
		case when v_first is null then '' else coalesce(btrim(new.raw_user_meta_data ->> 'last_name'), '') end,
		coalesce((new.raw_user_meta_data ->> 'must_change_password')::boolean, false)
	);

	insert into public.user_settings (user_id) values (new.id);

	insert into public.user_roles (user_id, role_id)
	select new.id, role_id from public.roles where name = v_role;

	if v_role = 'member' then
		insert into public.members (user_id) values (new.id);
	elsif v_role = 'coach' then
		insert into public.coaches (user_id) values (new.id);
	end if;

	return new;
end;
$$;
