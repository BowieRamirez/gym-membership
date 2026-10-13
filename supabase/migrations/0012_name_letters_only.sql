-- 0012: a name is letters only.
--
-- The app already refuses other names when someone registers, but anyone can call Supabase Auth directly with the public key,
-- and a signed-in user can rename themselves. This puts the same rule in the database.
--
-- The rule: 2 to 60 characters; letters, with single spaces, hyphens or apostrophes between them
-- ("Mary-Ann O'Brien", "Juan dela Cruz"). Digits, symbols and doubled or edge separators are refused.
--
-- NOT VALID: the rule is enforced on every new and changed row, but rows that already exist are not checked, so this
-- cannot fail on old data. Be aware that a row with an old name that breaks the rule will refuse its next update, whatever column
-- changes. To see them before that happens:
--
--   select id, full_name from public.profiles
--   where not (char_length(full_name) between 2 and 60 and full_name ~ '^[[:alpha:]]+([ ''-][[:alpha:]]+)*$');
--
-- Fix those names, then `alter table public.profiles validate constraint profiles_full_name_letters;`.

alter table public.profiles
	add constraint profiles_full_name_letters
	check (char_length(full_name) between 2 and 60 and full_name ~ '^[[:alpha:]]+([ ''-][[:alpha:]]+)*$') not valid;

-- The sign-up trigger (0004) fell back to the part of the email before the @ when no name was given, which can hold digits
-- ("jane99"). It now keeps only the letters ("jane"), and uses "Member" when too little is left. A name that was given
-- is passed through as is, so a bad one fails the sign-up on the constraint above.
create or replace function public.handle_new_user() returns trigger
language plpgsql security definer
set search_path = ''
as $$
declare
	v_role text := coalesce(new.raw_app_meta_data ->> 'role', 'member');
	v_fallback text := btrim(regexp_replace(split_part(new.email, '@', 1), '[^[:alpha:]]+', ' ', 'g'));
	v_name text := coalesce(
		nullif(btrim(new.raw_user_meta_data ->> 'full_name'), ''),
		case when char_length(v_fallback) between 2 and 60 then v_fallback else 'Member' end
	);
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
