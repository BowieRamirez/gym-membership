-- Cash plan (docs/superpowers/plans/2026-11-17-cash-payments-verify.md): a member can pick cash at the front desk for a package.
-- The payment is created as before, then switched to cash; it waits on Payments to verify, where verify_payment (unchanged)
-- approves it into the desk's open drawer (0050 trigger) or rejects it with a reason. Online ('qr') stays the default, so
-- older apps that send no p_method keep paying online. A cash payment gets no Xendit checkout (save_online_checkout needs 'qr').

create function public.with_package_method(p_pay public.payments, p_method public.payment_method) returns public.payments
language plpgsql security definer set search_path='' as $$
declare v public.payments;
begin
  if p_method = 'qr' then return p_pay; end if;
  update public.payments set method = 'cash' where payment_id = p_pay.payment_id returning * into v;
  return v;
end $$;

create function public.check_package_method(p_method public.payment_method) returns void
language plpgsql set search_path='' as $$
begin
  if p_method is null or p_method not in ('qr', 'cash') then perform public.fail('bad_input', '22023'); end if;
end $$;

drop function public.extend_membership(bigint, text);
create function public.extend_membership(p_package_id bigint, p_discount_code text default null, p_method public.payment_method default 'qr')
returns public.payments language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  perform public.check_package_method(p_method);
  return public.with_package_method(public.create_membership_extension(auth.uid(), p_package_id, p_discount_code), p_method);
end $$;

drop function public.avail_membership(bigint, text);
create function public.avail_membership(p_package_id bigint, p_discount_code text default null, p_method public.payment_method default 'qr')
returns public.payments language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  perform public.check_package_method(p_method);
  perform 1 from public.members where user_id = auth.uid() for update;
  if exists(select 1 from public.user_membership_packages where member_id = auth.uid() and status = 'pending')
    or exists(select 1 from public.membership_terms where member_id = auth.uid() and cancelled_at is null and ends_at > now()) then
    perform public.fail('already_availed', 'P0002');
  end if;
  return public.with_package_method(public.create_membership_extension(auth.uid(), p_package_id, p_discount_code), p_method);
end $$;

drop function public.renew_membership(bigint, text);
create function public.renew_membership(p_ump_id bigint, p_discount_code text default null, p_method public.payment_method default 'qr')
returns public.payments language plpgsql security definer set search_path='' as $$
declare v_package bigint;
begin
  perform public.require_active_user();
  perform public.check_package_method(p_method);
  select membership_package_id into v_package from public.user_membership_packages
    where user_membership_package_id = p_ump_id and member_id = auth.uid() and status in ('active', 'expired');
  if not found then perform public.fail('not_renewable', '22023'); end if;
  return public.with_package_method(public.create_membership_extension(auth.uid(), public.newest_package_version(v_package), p_discount_code), p_method);
end $$;

revoke all on function public.with_package_method(public.payments, public.payment_method), public.check_package_method(public.payment_method),
  public.extend_membership(bigint, text, public.payment_method), public.avail_membership(bigint, text, public.payment_method),
  public.renew_membership(bigint, text, public.payment_method) from public, anon, authenticated;
grant execute on function public.extend_membership(bigint, text, public.payment_method), public.avail_membership(bigint, text, public.payment_method),
  public.renew_membership(bigint, text, public.payment_method) to authenticated;
