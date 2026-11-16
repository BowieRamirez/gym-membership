-- Owner, 2026-11-16: the log shows no IP address for anyone, admins included (replaces D7's staff addresses and 0078's
-- members-only rule). audit() stores none, every address already stored is cleared, and the log page no longer returns one.
-- The column stays (empty) so older app builds that read it keep working.
create or replace function public.audit(p_action text, p_entity text, p_entity_id text default null, p_details jsonb default null)
returns void
language sql security definer
set search_path = ''
as $$
	insert into public.audit_log (actor_id, action, entity, entity_id, details, ip_address, role)
	values (auth.uid(), p_action, p_entity, p_entity_id, p_details, null, public.main_role(auth.uid()));
$$;
revoke all on function public.audit(text, text, text, jsonb) from public, anon, authenticated;

update public.audit_log set ip_address = null where ip_address is not null;
