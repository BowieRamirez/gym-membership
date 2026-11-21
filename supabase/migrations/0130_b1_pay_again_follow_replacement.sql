-- B1 (reopened): extend_membership must follow the replaces_id chain server-side (members cannot read old replaced packages under RLS).

create or replace function public.extend_membership(p_package_id bigint, p_discount_code text default null, p_method public.payment_method default 'qr')
returns public.payments language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  perform public.check_package_method(p_method);
  return public.with_package_method(
    public.create_membership_extension(auth.uid(), public.newest_package_version(p_package_id), p_discount_code),
    p_method
  );
end $$;

revoke all on function public.extend_membership(bigint, text, public.payment_method) from public, anon, authenticated;
grant execute on function public.extend_membership(bigint, text, public.payment_method) to authenticated;
