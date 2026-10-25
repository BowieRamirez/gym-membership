-- 0038: a sub-admin added through create-user came out as a member.
--
-- Supabase Auth saves app_metadata (the role) a moment after it inserts the user; 0011's trigger applies the role at that point. 0036
-- added the subadmin role but not to that trigger's list, so the role was ignored and the account kept the sign-up default (member).
-- Same function as 0011, plus subadmin; then the accounts already created that way are repaired the way 0011 repaired its own.

create or replace function public.apply_role_from_metadata() returns trigger
language plpgsql security definer
set search_path = ''
as $$
declare
	v_role text := new.raw_app_meta_data ->> 'role';
begin
	-- only the first time a role appears; later role changes go through assign_role / revoke_role
	if v_role is null or old.raw_app_meta_data ->> 'role' is not null then return new; end if;
	if v_role not in ('member', 'coach', 'employee', 'subadmin', 'admin') then return new; end if;
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

-- repair: active (not deleted) accounts created as a sub-admin that never got the role
do $$
declare
	u record;
begin
	for u in
		select a.id
		from auth.users a
		join public.profiles p on p.id = a.id and p.deleted_at is null
		where a.raw_app_meta_data ->> 'role' = 'subadmin'
		  and not exists (select 1 from public.user_roles ur join public.roles r using (role_id) where ur.user_id = a.id and r.name = 'subadmin')
	loop
		delete from public.user_roles where user_id = u.id;
		insert into public.user_roles (user_id, role_id) select u.id, role_id from public.roles where name = 'subadmin';
		begin
			delete from public.members where user_id = u.id;
		exception when foreign_key_violation then null;
		end;
	end loop;
end;
$$;
