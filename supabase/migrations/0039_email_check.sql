-- Item 2 (2026-11-05): the register page says right away if an email already has an account.
-- Someone registering is not signed in, so this cannot be a function the app calls: anonymous users reach no security definer
-- function (rules.mjs). The check-email Edge Function calls it with the service role and passes the caller's network address.
-- The limits are here because a debounce in the app only slows a well-behaved client.

-- One row per recent lookup. Network addresses are hashed, and rows last 10 minutes. The email looked up is not stored.
create table public.email_checks (
  ip_hash    text        not null,
  checked_at timestamptz not null default now()
);
create index email_checks_ip_idx on public.email_checks (ip_hash, checked_at);
alter table public.email_checks enable row level security;
revoke all on public.email_checks from public, anon, authenticated;

create function public.check_email_in_use(p_ip text, p_email text) returns boolean
language plpgsql security definer set search_path='' as $$
declare
  v_hash text := pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(coalesce(p_ip,''),'UTF8')),'hex');
begin
  if coalesce(char_length(p_email),0) not between 1 and 100 then perform public.fail('bad_input','22023'); end if;
  perform pg_catalog.pg_advisory_xact_lock(23002,1);   -- so the counts below are exact when lookups arrive together
  delete from public.email_checks where checked_at < now()-interval '10 minutes';
  -- the second limit catches forged or rotating addresses; refused lookups store nothing, so the table stays small
  if (select count(*) from public.email_checks where ip_hash=v_hash) >= 30
    or (select count(*) from public.email_checks) >= 600 then
    perform public.fail('rate_limited','P0002');
  end if;
  insert into public.email_checks(ip_hash) values(v_hash);
  return exists(select 1 from auth.users u where lower(u.email)=lower(btrim(p_email)));
end $$;
revoke all on function public.check_email_in_use(text,text) from public, anon, authenticated;
grant execute on function public.check_email_in_use(text,text) to service_role;
