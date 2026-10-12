-- 0011: apply the role an account was created with.
--
-- 0004 reads app_metadata.role when the auth user is inserted, but Supabase Auth inserts the user first and writes
-- app_metadata a moment later, so accounts made through the Admin API (the owner, and everyone create-user makes) came out
-- as plain members. This applies the role the first time it is written. Only the service role can write app_metadata,
-- so a member still cannot give themselves a role.

create function public.apply_role_from_metadata() returns trigger
language plpgsql security definer
set search_path = ''
as $$
declare
	v_role text := new.raw_app_meta_data ->> 'role';
begin
	-- only the first time a role appears; later role changes go through assign_role / revoke_role
	if v_role is null or old.raw_app_meta_data ->> 'role' is not null then return new; end if;
	if v_role not in ('member', 'coach', 'employee', 'admin') then return new; end if;
	if not exists (select 1 from public.profiles where id = new.id) then return new; end if;

	delete from public.user_roles where user_id = new.id;
	insert into public.user_roles (user_id, role_id) select new.id, role_id from public.roles where name = v_role;

	if v_role = 'member' then
		insert into public.members (user_id) values (new.id) on conflict do nothing;
	else
		-- the sign-up trigger made them a member first; staff and coaches are not members unless they have history
		begin
			delete from public.members where user_id = new.id;
		exception when foreign_key_violation then null;
		end;
		if v_role = 'coach' then insert into public.coaches (user_id) values (new.id) on conflict do nothing; end if;
	end if;
	return new;
end;
$$;

create trigger on_auth_user_role_set
	after update of raw_app_meta_data on auth.users
	for each row
	when (old.raw_app_meta_data is distinct from new.raw_app_meta_data)
	execute function public.apply_role_from_metadata();

revoke all on function public.apply_role_from_metadata() from public, anon, authenticated;

-- repair accounts already created the old way
do $$
declare
	u record;
begin
	for u in
		select a.id, a.raw_app_meta_data ->> 'role' as role
		from auth.users a
		join public.profiles p on p.id = a.id
		where a.raw_app_meta_data ->> 'role' in ('coach', 'employee', 'admin')
		  and not exists (
			select 1 from public.user_roles ur join public.roles r on r.role_id = ur.role_id
			where ur.user_id = a.id and r.name = a.raw_app_meta_data ->> 'role'
		  )
	loop
		delete from public.user_roles where user_id = u.id;
		insert into public.user_roles (user_id, role_id) select u.id, role_id from public.roles where name = u.role;
		begin
			delete from public.members where user_id = u.id;
		exception when foreign_key_violation then null;
		end;
		if u.role = 'coach' then insert into public.coaches (user_id) values (u.id) on conflict do nothing; end if;
	end loop;
end;
$$;
