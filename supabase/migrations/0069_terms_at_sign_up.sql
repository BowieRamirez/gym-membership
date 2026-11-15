-- Item 14 of the 2026-11-12 plan, G24/G25 (owner, 2026-11-15; design docs/superpowers/specs/2026-11-15-terms-at-sign-up-design.md).
-- A member who signs up agrees to the Terms and Policy shipped with the app. The app sends the version it showed (user metadata
-- terms_version); the profile keeps it and the time. A self sign-up (a name but no role in app metadata, as the app's sign-up makes it)
-- without a version is refused, like the last-name check (0059). Accounts an admin makes carry a role (create-user) and aren't asked;
-- neither is one made in the Supabase dashboard (no name). An app older than this sends no version, so it can't sign members up (G15).

alter table public.profiles
  add column terms_version text,
  add column terms_accepted_at timestamptz;
comment on column public.profiles.terms_version is 'Terms and Policy version agreed at sign-up (G25); null for admin-made and older accounts';
comment on column public.profiles.terms_accepted_at is 'When terms_version was agreed (G25)';

create function public.require_sign_up_terms() returns trigger
language plpgsql set search_path = '' as $$
begin
  -- the app's sign-up always sends a name; an account made in the Supabase dashboard has none and stays allowed, as 0059 left it.
  -- create-user (an admin) goes through the Admin API, which inserts first and writes the role after, so at this point it has no role
  -- yet: it is told apart by must_change_password, which create-user always sets and the app's sign-up never does. (Someone calling
  -- Auth directly could set it to skip agreeing; they'd only skip the tick and be made to change their password.)
  if nullif(btrim(coalesce(new.raw_app_meta_data, '{}'::jsonb) ->> 'role'), '') is null
     and coalesce((new.raw_user_meta_data ->> 'must_change_password')::boolean, false) = false
     and coalesce(nullif(btrim(new.raw_user_meta_data ->> 'first_name'), ''), nullif(btrim(new.raw_user_meta_data ->> 'full_name'), '')) is not null
     and nullif(btrim(coalesce(new.raw_user_meta_data, '{}'::jsonb) ->> 'terms_version'), '') is null then
    perform public.fail('terms_required');
  end if;
  return new;
end $$;
revoke all on function public.require_sign_up_terms() from public, anon, authenticated;
create trigger auth_users_require_terms before insert on auth.users for each row execute function public.require_sign_up_terms();

-- after on_auth_user_created (triggers run in name order), so the profile exists
create function public.save_sign_up_terms() returns trigger
language plpgsql security definer set search_path = '' as $$
declare v text := nullif(btrim(coalesce(new.raw_user_meta_data, '{}'::jsonb) ->> 'terms_version'), '');
begin
  if v is not null then
    update public.profiles set terms_version = left(v, 40), terms_accepted_at = now() where id = new.id;
  end if;
  return new;
end $$;
revoke all on function public.save_sign_up_terms() from public, anon, authenticated;
create trigger on_auth_user_terms after insert on auth.users for each row execute function public.save_sign_up_terms();
