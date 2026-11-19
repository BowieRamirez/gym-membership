-- Owner fixes 2026-11-19, final review: 0103 checked the lock, tried the password, then counted it, so many tries sent at once all
-- passed the check before any was counted. Now each try is counted before Auth sees it, in one atomic step (sign_in_begin): once 10
-- are counted the account locks for 5 minutes and later tries are refused. A right password clears the count (sign_in_result).
drop function public.sign_in_check(text);

-- counts this try; minutes left on a lock (this try refused), or 0 when it may go to Auth
create function public.sign_in_begin(p_email text) returns integer
language plpgsql security definer set search_path = '' as $$
declare v_email text := lower(btrim(p_email)); v public.sign_in_attempts;
begin
  insert into public.sign_in_attempts as a (email, wrong) values (v_email, 1)
    on conflict (email) do update set
      wrong = case when a.locked_until > now() then a.wrong when a.locked_until is not null then 1 else a.wrong + 1 end,
      locked_until = case when a.locked_until > now() then a.locked_until end   -- an expired lock starts a fresh count
    returning * into v;
  if v.locked_until > now() then return ceil(extract(epoch from v.locked_until - now()) / 60)::int; end if;
  if v.wrong > 10 then   -- 10 tries already counted and none was right
    update public.sign_in_attempts set wrong = 0, locked_until = now() + interval '5 minutes' where email = v_email;
    return 5;
  end if;
  return 0;
end $$;

-- after the try: a right password clears the count; a wrong one was counted already, and the 10th locks at once (returns 5)
create or replace function public.sign_in_result(p_email text, p_ok boolean) returns integer
language plpgsql security definer set search_path = '' as $$
declare v_email text := lower(btrim(p_email));
begin
  if p_ok then delete from public.sign_in_attempts where email = v_email; return 0; end if;
  update public.sign_in_attempts set wrong = 0, locked_until = now() + interval '5 minutes'
    where email = v_email and wrong >= 10 and (locked_until is null or locked_until <= now());
  return case when found then 5 else 0 end;
end $$;

revoke all on function public.sign_in_begin(text), public.sign_in_result(text, boolean) from public, anon, authenticated;
grant execute on function public.sign_in_begin(text), public.sign_in_result(text, boolean) to service_role;
