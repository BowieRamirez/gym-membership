-- Owner fixes 2026-11-19 Q12 (the Cashier too): the desk sees whether a code works for the customer it is typed for, before
-- Complete payment. Same answer shape as check_discount_code (0107), but for a chosen member, and only for staff who record payments.
-- A renewal's price comes from the desk's card (p_price, shown only: the server charges its own price) and its fit isn't checked here.
-- Counts against the caller's 20 checks in 10 minutes (code_checks).
create function public.check_discount_code_for(p_member uuid, p_code text, p_kind text, p_item bigint, p_price numeric default null)
returns table(percent int, amount_off numeric, you_pay numeric, reason text)
language plpgsql security definer set search_path = '' as $$
declare
  v_uid    uuid := auth.uid();
  v_price  numeric;
  v_disc   record;
  v_pct    int;
  v_reason text;
begin
  perform public.require_permission('payments:record');
  if p_member is null or p_code is null or btrim(p_code) = '' or char_length(btrim(p_code)) > 50
     or coalesce(p_kind, '') not in ('package', 'amenity', 'renewal') or p_item is null or p_item < 1 then
    perform public.fail('bad_input', '22023');
  end if;
  if not exists (select 1 from public.members m where m.user_id = p_member) then perform public.fail('not_found', '22023'); end if;

  if p_kind = 'package' then
    select p.price into v_price from public.membership_packages p
      where p.membership_package_id = p_item and p.is_active and p.published_at is not null;
  elsif p_kind = 'amenity' then
    select a.price into v_price from public.amenities a
      where a.amenity_id = p_item and a.is_active and a.published_at is not null;
  else
    v_price := p_price;
  end if;
  if v_price is null or v_price <= 0 then perform public.fail('not_found', '22023'); end if;

  perform pg_catalog.pg_advisory_xact_lock(23003, pg_catalog.hashtext(v_uid::text));
  delete from public.code_checks where checked_at < now() - interval '10 minutes';
  if (select count(*) from public.code_checks where member_id = v_uid) >= 20 then
    perform public.fail('too_many_checks', 'P0002');
  end if;
  insert into public.code_checks (member_id) values (v_uid);

  begin
    select * into v_disc from public.apply_discount(p_member, p_code, v_price);
    if p_kind <> 'renewal' and not exists (
      select 1 from public.discount_items i
      where i.discount_id = v_disc.discount_id and i.kind = p_kind
        and (i.item_id is null or i.item_id = p_item)
    ) then
      perform public.fail('code_not_for_item', '22023');
    end if;
    select d.percent into v_pct from public.discounts d where d.discount_id = v_disc.discount_id;
    return query select v_pct, v_disc.discount_amount, v_disc.amount, null::text;
  exception when others then
    v_reason := sqlerrm;
    if v_reason = 'invalid_code' and exists (
      select 1 from public.discounts d
      where upper(d.code) = upper(btrim(p_code)) and d.is_active and not d.paused and d.expires_at <= now()
    ) then
      v_reason := 'code_expired';
    end if;
    return query select null::int, null::numeric, null::numeric, v_reason;
  end;
end $$;

revoke all on function public.check_discount_code_for(uuid, text, text, bigint, numeric) from public, anon, authenticated;
grant execute on function public.check_discount_code_for(uuid, text, text, bigint, numeric) to authenticated;
