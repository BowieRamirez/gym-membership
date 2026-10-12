-- 0004: sign-up trigger (plan 4.2)
--
-- Runs for every new Supabase Auth user: self sign-up from the app, or an account the
-- create-user Edge Function made. The role comes from app metadata, which only the
-- service role can set, so a member can't sign up as an admin.

create function public.handle_new_user() returns trigger
language plpgsql security definer
set search_path = ''
as $$
declare
	v_role text := coalesce(new.raw_app_meta_data ->> 'role', 'member');
	v_name text := coalesce(nullif(btrim(new.raw_user_meta_data ->> 'full_name'), ''), split_part(new.email, '@', 1));
begin
	if v_role not in ('member', 'coach', 'employee', 'admin') then
		v_role := 'member';
	end if;

	insert into public.profiles (id, username, full_name, must_change_password)
	values (
		new.id,
		coalesce(nullif(btrim(new.raw_user_meta_data ->> 'username'), ''), lower(new.email)),
		v_name,
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

create trigger on_auth_user_created
	after insert on auth.users
	for each row execute function public.handle_new_user();
