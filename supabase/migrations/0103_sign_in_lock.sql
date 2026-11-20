-- Owner fixes 2026-11-19 Q2 (docs/superpowers/plans/2026-11-19-owner-fixes.md): 10 wrong passwords for one account lock its sign-in
-- for 5 minutes; a right password resets the count. Supabase's Password Verification hook would do this inside Auth, but it needs
-- the Team plan, so the app signs in through the sign-in Edge Function, which asks sign_in_check first and reports sign_in_result
-- after. Keyed by the lower-cased email (an unknown email gets counted the same way, so the lock reveals nothing). The table and
-- functions are the service role's only: no app user can read or reset a count.
create table public.sign_in_attempts (
  email        text primary key,
  wrong        integer not null default 0,
  locked_until timestamptz
);
alter table public.sign_in_attempts enable row level security;   -- no policies: nobody but the service role
revoke all on table public.sign_in_attempts from public, anon, authenticated;
grant all on table public.sign_in_attempts to service_role;

-- minutes left on a lock, 0 when it may try
create function public.sign_in_check(p_email text) returns integer
language sql stable security definer set search_path = '' as $$
  select coalesce((select ceil(extract(epoch from locked_until - now()) / 60)::int from public.sign_in_attempts
    where email = lower(btrim(p_email)) and locked_until > now()), 0);
$$;

-- after a try: a right password clears the count; the 10th wrong one locks for 5 minutes and returns 5, otherwise 0
create function public.sign_in_result(p_email text, p_ok boolean) returns integer
language plpgsql security definer set search_path = '' as $$
declare v_email text := lower(btrim(p_email)); v_wrong integer;
begin
  if p_ok then delete from public.sign_in_attempts where email = v_email; return 0; end if;
  insert into public.sign_in_attempts as a (email, wrong) values (v_email, 1)
    on conflict (email) do update set
      wrong = case when a.locked_until is not null and a.locked_until <= now() then 1 else a.wrong + 1 end,
      locked_until = case when a.locked_until is not null and a.locked_until <= now() then null else a.locked_until end
    returning wrong into v_wrong;
  if v_wrong >= 10 then
    update public.sign_in_attempts set wrong = 0, locked_until = now() + interval '5 minutes' where email = v_email;
    return 5;
  end if;
  return 0;
end $$;

revoke all on function public.sign_in_check(text), public.sign_in_result(text, boolean) from public, anon, authenticated;
grant execute on function public.sign_in_check(text), public.sign_in_result(text, boolean) to service_role;
