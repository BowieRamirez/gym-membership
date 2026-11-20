-- Owner fixes 2026-11-19 Q12, Q13 (docs/superpowers/plans/2026-11-19-owner-fixes.md):
-- check_discount_code checks a code as the member types it, without writing to payments.
-- Returns percent, amount_off, you_pay and reason (null when valid; otherwise invalid_code, code_expired,
-- code_already_used, code_limit_reached, or code_not_for_item). Returning reason instead of raising lets the
-- code_checks row commit on wrong guesses too, so the 20-in-10-minutes limit stops guessing.

create table public.code_checks (
  member_id  uuid        not null references public.profiles(id) on delete cascade,
  checked_at timestamptz not null default now()
);
create index code_checks_member_idx on public.code_checks (member_id, checked_at);
alter table public.code_checks enable row level security;
revoke all on public.code_checks from public, anon, authenticated;
comment on table public.code_checks is 'Recent discount code checks per member (Q13, 0107): 20 in 10 minutes';

create function public.check_discount_code(p_code text, p_kind text, p_item bigint)
returns table(percent int, amount_off numeric, you_pay numeric, reason text)
language plpgsql security definer set search_path = '' as $$
declare
  v_uid    uuid := auth.uid();
  v_price  numeric;
  v_disc   record;
  v_pct    int;
  v_reason text;
begin
  perform public.require_active_user();
  if p_code is null or btrim(p_code) = '' or char_length(btrim(p_code)) > 50
     or coalesce(p_kind, '') not in ('package', 'amenity') or p_item is null or p_item < 1 then
    perform public.fail('bad_input', '22023');
  end if;

  if p_kind = 'package' then
    select p.price into v_price from public.membership_packages p
      where p.membership_package_id = p_item and p.is_active and p.published_at is not null;
  else
    select a.price into v_price from public.amenities a
      where a.amenity_id = p_item and a.is_active and a.published_at is not null;
  end if;
  if not found then perform public.fail('not_found', '22023'); end if;

  perform pg_catalog.pg_advisory_xact_lock(23003, pg_catalog.hashtext(v_uid::text));
  delete from public.code_checks where checked_at < now() - interval '10 minutes';
  if (select count(*) from public.code_checks where member_id = v_uid) >= 20 then
    perform public.fail('too_many_checks', 'P0002');
  end if;
  insert into public.code_checks (member_id) values (v_uid);

  begin
    select * into v_disc from public.apply_discount(v_uid, p_code, v_price);
    if not exists (
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

revoke all on function public.check_discount_code(text, text, bigint) from public, anon, authenticated;
grant execute on function public.check_discount_code(text, text, bigint) to authenticated;
