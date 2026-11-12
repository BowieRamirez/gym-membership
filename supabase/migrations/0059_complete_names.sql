-- Item 3 / G5 (2026-11-12): accounts with a first name and no last name. Nothing is guessed: old one-word names stay as they are
-- until the person adds the last name themselves (once) or a system admin corrects it (correct_name, 0040).

-- A sign-up that sends a name must send a last name too (the app always does; an older app's one-word full_name is refused).
-- Accounts made with no name at all (the Supabase dashboard) still get handle_new_user's fallback and show as "Name incomplete".
create function public.require_sign_up_last_name() returns trigger
language plpgsql set search_path = '' as $$
declare
	m jsonb := coalesce(new.raw_user_meta_data, '{}'::jsonb);
	v_first text := nullif(btrim(m ->> 'first_name'), '');
	v_full text := nullif(btrim(m ->> 'full_name'), '');
begin
	if (v_first is not null and btrim(coalesce(m ->> 'last_name', '')) = '')
		or (v_first is null and v_full is not null and v_full !~ '\s') then
		perform public.fail('bad_name');
	end if;
	return new;
end $$;
revoke all on function public.require_sign_up_last_name() from public, anon, authenticated;
create trigger auth_users_require_last_name before insert on auth.users for each row execute function public.require_sign_up_last_name();

-- The account owner fills in a blank last name, once. Names stay locked otherwise (0040); the 0035 trigger rebuilds full_name,
-- the table rules check the letters and length, and an archived account is refused as for any profile change.
create function public.add_my_last_name(p_last text) returns void
language plpgsql security definer set search_path = '' as $$
declare
	v_old text;
	v_new text;
begin
	if auth.uid() is null then perform public.fail('forbidden', '42501'); end if;
	if btrim(coalesce(p_last, '')) = '' then perform public.fail('bad_name'); end if;
	select full_name into v_old from public.profiles where id = auth.uid() and last_name = '' for update;
	if not found then perform public.fail('name_complete', 'P0002'); end if;
	update public.profiles set last_name = btrim(p_last) where id = auth.uid() returning full_name into v_new;
	perform public.audit('user.add_last_name', 'profiles', auth.uid()::text, jsonb_build_object('from', v_old, 'to', v_new));
end $$;
revoke all on function public.add_my_last_name(text) from public, anon;
grant execute on function public.add_my_last_name(text) to authenticated;
