-- Xendit plan, Phase 2 ruling: Xendit Payment Sessions are short-lived (30 minutes by default), so a pending payment may need a new
-- checkout after its first one expires. xendit-checkout asks Xendit first and replaces a session only once it is EXPIRED or CANCELED,
-- naming the one it replaces: if another tap already swapped it, nothing changes, so a session that could still be paid is never lost.
drop function public.save_online_checkout(bigint, text, text);
create function public.save_online_checkout(p_payment_id bigint, p_xendit_id text, p_url text, p_replaces text default null) returns void
language plpgsql security definer set search_path='' as $$
begin
  update public.payments set xendit_id = p_xendit_id, xendit_url = p_url
  where payment_id = p_payment_id and status = 'pending' and method = 'qr' and xendit_id is not distinct from p_replaces;
end $$;
revoke all on function public.save_online_checkout(bigint, text, text, text) from public, anon, authenticated;
grant execute on function public.save_online_checkout(bigint, text, text, text) to service_role;
