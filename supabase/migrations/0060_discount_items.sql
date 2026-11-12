-- Item 2 of the 2026-11-12 plan (owner decisions G1 to G4, 2026-11-13).
-- G2 (changes F7 of 0048): a code can't be edited once made. Codes are written only through the functions below, so the table loses
--   its insert and update grants; to change terms, turn the code off and make a new one.
-- G1: a code can be turned off and on (paused; it stays on the Discounts page), archived (as before, 0024), or deleted while no payment
--   of any state has ever named it.
-- G3: each code lists the packages and amenities it fits; every payment that carries a code is checked against what it buys.
-- G4: members see the shown codes they can still use, with what each fits (my_discount_codes). packages_codes stays for older apps.

alter table public.discounts add column paused boolean not null default false;
comment on column public.discounts.paused is 'Turned off by staff for now (G1): not usable, not shown, still on the Discounts page. Archiving is is_active=false (0024)';

create table public.discount_items (
  discount_id bigint not null references public.discounts on delete cascade,
  kind        text   not null check (kind in ('package', 'amenity')),
  item_id     bigint,   -- null: every package (or every amenity)
  check (item_id is null or item_id > 0)
);
create unique index discount_items_key on public.discount_items (discount_id, kind, coalesce(item_id, 0));
alter table public.discount_items enable row level security;
revoke all on public.discount_items from public, anon, authenticated;
grant select on public.discount_items to authenticated;
create policy discount_items_read on public.discount_items for select to authenticated using (public.has_permission('discounts:manage'));
comment on table public.discount_items is 'What each discount code fits (G3); written only by create_discount (0060)';

-- every code made before this fitted everything, and still does
insert into public.discount_items (discount_id, kind, item_id)
  select d.discount_id, k.kind, null from public.discounts d cross join (values ('package'), ('amenity')) k(kind);

revoke insert, update, delete on public.discounts from authenticated;

-- G2: nothing about a code changes after it is made, whoever writes. Only on/off (paused) and archiving remain.
create or replace function public.guard_discount_fixed() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if (upper(new.code), new.percent, new.starts_at, new.expires_at, new.max_uses, new.show_on_packages)
     is distinct from (upper(old.code), old.percent, old.starts_at, old.expires_at, old.max_uses, old.show_on_packages) then
    raise exception 'discount_locked' using errcode='P0002';
  end if;
  return new;
end $$;

-- Same as 0026 apart from: a turned-off code is no code, and a code this member already used says so.
create or replace function public.apply_discount(p_member uuid, p_code text, p_price numeric)
returns table (discount_id bigint, discount_amount numeric, amount numeric)
language plpgsql security definer set search_path = '' as $$
declare
	v_disc public.discounts;
	v_cut numeric;
begin
	if p_code is null or btrim(p_code) = '' then
		return query select null::bigint, 0::numeric, p_price;
		return;
	end if;
	select * into v_disc from public.discounts d
	where upper(d.code) = upper(btrim(p_code)) and d.is_active and not d.paused and d.starts_at <= now() and d.expires_at > now()
	for update;
	if not found then perform public.fail('invalid_code', '22023'); end if;
	if exists (select 1 from public.payments p
	           where p.member_id = p_member and p.discount_id = v_disc.discount_id and p.status in ('pending', 'verified', 'voided')) then
		perform public.fail('code_already_used', 'P0002');
	end if;
	if v_disc.max_uses is not null and public.discount_held(v_disc.discount_id) >= v_disc.max_uses then
		perform public.fail('code_limit_reached', 'P0002');
	end if;
	v_cut := round(p_price * v_disc.percent / 100.0, 2);
	return query select v_disc.discount_id, v_cut, p_price - v_cut;
end $$;

-- G3: one check for every way a code reaches a payment (member purchase, renewal, amenity, Cashier). Named to run after
-- snapshot_membership_payment (0018), which fills membership_package_snapshot_id: the package actually being bought.
create function public.guard_payment_discount_item() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_kind text; v_item bigint;
begin
  if new.discount_id is null or (tg_op = 'UPDATE' and new.discount_id is not distinct from old.discount_id) then return new; end if;
  if new.kind in ('membership', 'renewal') then
    v_kind := 'package'; v_item := new.membership_package_snapshot_id;
  elsif new.user_amenity_id is not null then
    v_kind := 'amenity'; select ua.amenity_id into v_item from public.user_amenities ua where ua.user_amenity_id = new.user_amenity_id;
  end if;
  if v_item is null or not exists (select 1 from public.discount_items i where i.discount_id = new.discount_id and i.kind = v_kind
                                     and (i.item_id is null or i.item_id = v_item)) then
    perform public.fail('code_not_for_item', '22023');
  end if;
  return new;
end $$;
revoke all on function public.guard_payment_discount_item() from public, anon, authenticated;
create trigger zz_payments_discount_fits before insert or update of discount_id on public.payments
  for each row execute function public.guard_payment_discount_item();

-- Makes a code and what it fits in one step. p_items: [{"kind": "package" | "amenity", "id": <id> | null for all of that kind}, ...]
create function public.create_discount(p_code text, p_percent int, p_expires_at timestamptz, p_max_uses int, p_show boolean, p_items jsonb)
returns bigint
language plpgsql security definer set search_path='' as $$
declare v_code text := upper(btrim(coalesce(p_code, ''))); v_id bigint; v_item jsonb; v_kind text; v_ref bigint;
begin
  perform public.require_permission('discounts:manage');
  if v_code !~ '^[A-Z0-9][A-Z0-9_-]{2,29}$' or p_percent is null or p_percent not between 1 and 100
     or p_expires_at is null or p_expires_at <= now() then
    perform public.fail('bad_discount');
  end if;
  if p_max_uses is null or p_max_uses < 1 then perform public.fail('limit_required'); end if;
  if p_items is null or jsonb_typeof(p_items) <> 'array' or jsonb_array_length(p_items) = 0 then perform public.fail('no_items'); end if;
  if exists (select 1 from public.discounts d where upper(d.code) = v_code) then perform public.fail('code_taken', 'P0002'); end if;
  insert into public.discounts (code, percent, expires_at, max_uses, show_on_packages)
    values (v_code, p_percent, p_expires_at, p_max_uses, coalesce(p_show, true)) returning discount_id into v_id;
  for v_item in select * from jsonb_array_elements(p_items) loop
    v_kind := v_item ->> 'kind';
    if jsonb_typeof(v_item) <> 'object' or coalesce(v_kind, '') not in ('package', 'amenity')
       or coalesce(jsonb_typeof(v_item -> 'id'), 'null') not in ('number', 'null') then
      perform public.fail('bad_input');
    end if;
    v_ref := (v_item ->> 'id')::bigint;
    if v_ref is not null and not (
         (v_kind = 'package' and exists (select 1 from public.membership_packages p where p.membership_package_id = v_ref and p.is_active))
      or (v_kind = 'amenity' and exists (select 1 from public.amenities a where a.amenity_id = v_ref and a.is_active))) then
      perform public.fail('item_not_found');
    end if;
    insert into public.discount_items (discount_id, kind, item_id) values (v_id, v_kind, v_ref) on conflict do nothing;
  end loop;
  perform public.audit('discount.create', 'discounts', v_id::text, jsonb_build_object('code', v_code, 'percent', p_percent, 'items', p_items));
  return v_id;
end $$;

-- G1: turn a live code off for now, or back on. An archived or expired code is the archive's business (0024).
create function public.set_discount_paused(p_id bigint, p_paused boolean) returns void
language plpgsql security definer set search_path='' as $$
declare v public.discounts;
begin
  perform public.require_permission('discounts:manage');
  select * into v from public.discounts where discount_id = p_id for update;
  if not found then perform public.fail('not_found'); end if;
  if not v.is_active or v.expires_at <= now() then perform public.fail('record_archived', 'P0002'); end if;
  update public.discounts set paused = coalesce(p_paused, false) where discount_id = p_id;
  perform public.audit(case when coalesce(p_paused, false) then 'discount.turn_off' else 'discount.turn_on' end, 'discounts', p_id::text,
                       jsonb_build_object('code', v.code));
end $$;

-- G1: a code no payment has ever named (any state) can be deleted. A System admin only, as for archiving (D15).
create function public.delete_discount(p_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_code text;
begin
  perform public.require_archive_admin();
  select code into v_code from public.discounts where discount_id = p_id for update;
  if not found then perform public.fail('not_found'); end if;
  if exists (select 1 from public.payments where discount_id = p_id) then perform public.fail('discount_used', 'P0002'); end if;
  delete from public.discounts where discount_id = p_id;
  perform public.audit('discount.delete', 'discounts', p_id::text, jsonb_build_object('code', v_code));
end $$;

-- G4: the shown codes this person can still use, with what each fits. Empty id lists with a false "all" mean none of that kind.
create function public.my_discount_codes()
returns table(code text, percent int, expires_at timestamptz, created_at timestamptz,
              all_packages boolean, package_ids bigint[], all_amenities boolean, amenity_ids bigint[])
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_active_user();
  return query select d.code, d.percent, d.expires_at, d.created_at,
      coalesce(bool_or(i.kind = 'package' and i.item_id is null), false),
      coalesce(array_agg(i.item_id order by i.item_id) filter (where i.kind = 'package' and i.item_id is not null), '{}'::bigint[]),
      coalesce(bool_or(i.kind = 'amenity' and i.item_id is null), false),
      coalesce(array_agg(i.item_id order by i.item_id) filter (where i.kind = 'amenity' and i.item_id is not null), '{}'::bigint[])
    from public.discounts d join public.discount_items i on i.discount_id = d.discount_id
    where d.show_on_packages and d.is_active and not d.paused and d.starts_at <= now() and d.expires_at > now()
      and (d.max_uses is null or public.discount_held(d.discount_id) < d.max_uses)
      and not exists (select 1 from public.payments p where p.member_id = auth.uid() and p.discount_id = d.discount_id
                      and p.status in ('pending', 'verified', 'voided'))
    group by d.discount_id
    order by d.created_at desc, d.code;
end $$;

-- Same as 0048 apart from: a turned-off code isn't listed. Kept for apps from before 0060.
create or replace function public.packages_codes() returns table(code text, percent int, expires_at timestamptz)
language plpgsql stable security definer set search_path='' as $$
begin
  perform public.require_active_user();
  return query select d.code, d.percent, d.expires_at from public.discounts d
    where d.show_on_packages and d.is_active and not d.paused and d.starts_at <= now() and d.expires_at > now()
      and (d.max_uses is null or public.discount_held(d.discount_id) < d.max_uses)
      and not exists (select 1 from public.payments p where p.member_id = auth.uid() and p.discount_id = d.discount_id
                      and p.status in ('pending', 'verified', 'voided'))
    order by d.expires_at, d.code;
end $$;

revoke all on function public.create_discount(text, int, timestamptz, int, boolean, jsonb), public.set_discount_paused(bigint, boolean),
  public.delete_discount(bigint), public.my_discount_codes() from public, anon;
grant execute on function public.create_discount(text, int, timestamptz, int, boolean, jsonb), public.set_discount_paused(bigint, boolean),
  public.delete_discount(bigint), public.my_discount_codes() to authenticated;
