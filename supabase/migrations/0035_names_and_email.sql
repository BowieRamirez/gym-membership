-- 0035: first and last name, email confirmation by code, and the username following the sign-in email (Phase 11, D13).
--
-- Names: profiles gets first_name and last_name. full_name stays and is always built from them ("first last"), so every
-- screen, receipt, PDF and chat that reads full_name keeps working. A writer that only sets full_name (an older app, or the
-- create-user function before it was updated) still works: the name is split at the first space, as existing names are here.
-- The original full_name is never lost by the split: joining the two parts gives it back.
--
-- Email: confirmed_email holds the sign-in email the person proved they can read. Only confirm_my_email() writes it, and only
-- from a session opened with an emailed code in the last 15 minutes. "Confirmed" means confirmed_email equals the current
-- sign-in email, so changing the email makes it unconfirmed again. Nothing is blocked by an unconfirmed email (D13(c)).

alter table public.profiles add column first_name text, add column last_name text not null default '', add column confirmed_email text;

-- Split existing names. The 0012 rule is NOT VALID and is checked on every changed row, so an old name that breaks it would stop
-- this update; it is lifted for the backfill and put back unchanged.
alter table public.profiles drop constraint profiles_full_name_letters;
update public.profiles set first_name = substring(btrim(full_name) from '^\S+'),
	last_name = btrim(regexp_replace(btrim(full_name), '^\S+\s*', ''));
update public.profiles set first_name = full_name where first_name is null;   -- a blank full_name (not possible today)
alter table public.profiles alter column first_name set not null;
alter table public.profiles
	add constraint profiles_full_name_letters
	check (char_length(full_name) between 2 and 60 and full_name ~ '^[[:alpha:]]+([ ''-][[:alpha:]]+)*$') not valid,
	-- the same letters rule per part; last_name may be empty only for an old one-word name
	add constraint profiles_first_name_letters check (first_name ~ '^[[:alpha:]]+([ ''-][[:alpha:]]+)*$') not valid,
	add constraint profiles_last_name_letters check (last_name = '' or last_name ~ '^[[:alpha:]]+([ ''-][[:alpha:]]+)*$') not valid;

create function public.sync_name_parts() returns trigger
language plpgsql set search_path = '' as $$
begin
	if (tg_op = 'INSERT' and new.first_name is null)
		or (tg_op = 'UPDATE' and new.full_name is distinct from old.full_name
			and new.first_name is not distinct from old.first_name and new.last_name is not distinct from old.last_name) then
		new.first_name := coalesce(substring(btrim(new.full_name) from '^\S+'), btrim(new.full_name));   -- blank stays blank: the rules refuse it
		new.last_name := btrim(regexp_replace(btrim(new.full_name), '^\S+\s*', ''));
	elsif tg_op = 'INSERT' or new.first_name is distinct from old.first_name or new.last_name is distinct from old.last_name then
		new.first_name := btrim(new.first_name);
		new.last_name := btrim(coalesce(new.last_name, ''));
		new.full_name := btrim(new.first_name || ' ' || new.last_name);
	end if;
	return new;
end $$;
create trigger profiles_name_parts before insert or update on public.profiles for each row execute function public.sync_name_parts();

grant update (first_name, last_name) on public.profiles to authenticated;

-- Renames yourself. Both parts are required; the table rules above check the letters.
create function public.set_my_name(p_first text, p_last text) returns void
language plpgsql set search_path = '' as $$
begin
	if btrim(coalesce(p_first, '')) = '' or btrim(coalesce(p_last, '')) = '' then perform public.fail('bad_name'); end if;
	update public.profiles set first_name = btrim(p_first), last_name = btrim(p_last) where id = auth.uid();
	if not found then perform public.fail('not authenticated', '42501'); end if;
end $$;
revoke all on function public.set_my_name(text, text) from public, anon;
grant execute on function public.set_my_name(text, text) to authenticated;

-- Sign-up passes first_name/last_name when the app sends them; otherwise the trigger above splits full_name. Same as 0012 otherwise.
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
	if v_role not in ('member', 'coach', 'employee', 'admin') then
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

-- Marks your current sign-in email confirmed. Supabase Auth records how a session was opened in the token's amr list; a code
-- or link sent by email (sign-in code, password reset, invite, sign-up or email-change confirmation) proves you read that inbox.
create function public.confirm_my_email() returns text
language plpgsql security definer set search_path = '' as $$
declare v_email text;
begin
	if auth.uid() is null then perform public.fail('not authenticated', '42501'); end if;
	if not exists (
		select 1 from jsonb_array_elements(coalesce(auth.jwt() -> 'amr', '[]'::jsonb)) a
		where a ->> 'method' in ('otp', 'magiclink', 'recovery', 'invite', 'email/signup', 'email_change')
			and (a ->> 'timestamp')::bigint >= extract(epoch from now())::bigint - 15 * 60
	) then
		perform public.fail('code_not_verified');
	end if;
	select lower(email) into v_email from auth.users where id = auth.uid();
	update public.profiles set confirmed_email = v_email where id = auth.uid();
	return v_email;
end $$;
revoke all on function public.confirm_my_email() from public, anon;
grant execute on function public.confirm_my_email() to authenticated;

-- The username is the sign-in email (lowercase) and must stay unique, so it follows an email change; otherwise the old address
-- could never sign up again.
create function public.sync_username() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
	update public.profiles set username = lower(new.email) where id = new.id;
	return new;
end $$;
revoke all on function public.sync_username() from public, anon, authenticated;
create trigger on_auth_user_email_changed
	after update of email on auth.users
	for each row when (new.email is distinct from old.email and new.email is not null)
	execute function public.sync_username();
