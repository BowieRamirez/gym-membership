-- Owner fixes 2026-11-19 Q3 (docs/superpowers/plans/2026-11-19-owner-fixes.md): a member starts at most 10 package payments in a
-- rolling hour; made, then cancelled or expired, they all count. The member's package paths (avail, extend, renew, 0084) all check
-- the method here first, and only they do, so the front desk's sales aren't limited.
create or replace function public.check_package_method(p_method public.payment_method) returns void
language plpgsql set search_path='' as $$
begin
  if p_method = 'cash' then perform public.fail('online_only', '22023'); end if;   -- 0099
  if p_method is null or p_method <> 'qr' then perform public.fail('bad_input', '22023'); end if;
  if (select count(*) from public.payments where member_id = auth.uid() and kind in ('membership', 'renewal')
      and created_at > now() - interval '1 hour') >= 10 then
    perform public.fail('too_many_tries', 'P0002');
  end if;
end $$;
revoke all on function public.check_package_method(public.payment_method) from public, anon, authenticated;
