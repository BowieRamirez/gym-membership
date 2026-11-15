-- Items 7, 8 and 9 of the 2026-11-12 plan (owner decisions G6, G7, G9, 2026-11-13).
-- G6: packages and amenities are Draft (published_at null), Published or Archived (is_active false, 0024). Anything added from the app
--   starts as a draft: editable, deletable, never shown to members, never sold. Publishing checks the required fields. Delete is only for
--   a draft; anything else archives. An archived package never comes back on sale (make a new version instead).
-- G7: a published amenity stays editable; new prices apply to new purchases only (paid ones keep what was paid). No amenity versions.
-- G9: a published package is locked. "New version" copies it into a draft linked by replaces_id (one at a time); publishing that draft
--   archives the old one in the same step. Members keep the terms they bought (membership_terms, 0018) and renew into the newest version
--   at its price. One published package per name, so a version can share its name. benefits: one line each, shown on Packages.

-- Rows already on sale are published (the default fills them; a raw insert by the database owner is published too, as before).
-- App inserts are forced to drafts by guard_catalog_lifecycle below.
alter table public.membership_packages
  add column published_at timestamptz default now(),
  add column replaces_id bigint references public.membership_packages (membership_package_id),
  add column benefits text[] not null default '{}';
alter table public.amenities add column published_at timestamptz default now();
comment on column public.membership_packages.published_at is 'Null: a draft (G6). Set only by publish_catalog_item (0061)';
comment on column public.membership_packages.replaces_id is 'The package this version replaces when published (G9)';
comment on column public.amenities.published_at is 'Null: a draft (G6/G7). Set only by publish_catalog_item (0061)';

-- G9: one published package per name; archived versions and drafts may share it.
alter table public.membership_packages drop constraint membership_packages_name_key;
create unique index membership_packages_published_name on public.membership_packages (name) where published_at is not null and is_active;

create function public.guard_catalog_lifecycle() returns trigger
language plpgsql set search_path='' as $$
begin
  -- G6: an archived package never comes back on sale, whoever writes (restore_archived_record included). An archived draft can.
  if tg_op = 'UPDATE' and tg_table_name = 'membership_packages' and not old.is_active and new.is_active and old.published_at is not null then
    raise exception 'package_archived' using errcode = 'P0002';
  end if;
  if current_user not in ('authenticated', 'anon') then return new; end if;   -- the functions below and the database owner
  if tg_op = 'INSERT' then
    new.published_at := null;
    if tg_table_name = 'membership_packages' then new.replaces_id := null; end if;
    return new;
  end if;
  if tg_table_name = 'membership_packages' then
    -- G9: a published package changes only by being archived
    if old.published_at is not null and (new.name, new.description, new.price, new.duration_days, new.benefits, new.image_path, new.published_at, new.replaces_id)
       is distinct from (old.name, old.description, old.price, old.duration_days, old.benefits, old.image_path, old.published_at, old.replaces_id) then
      raise exception 'package_published' using errcode = 'P0002';
    end if;
    new.replaces_id := old.replaces_id;
  end if;
  new.published_at := old.published_at;   -- publishing goes through publish_catalog_item
  return new;
end $$;
revoke all on function public.guard_catalog_lifecycle() from public, anon, authenticated;
create trigger packages_lifecycle_guard before insert or update on public.membership_packages for each row execute function public.guard_catalog_lifecycle();
create trigger amenities_lifecycle_guard before insert or update on public.amenities for each row execute function public.guard_catalog_lifecycle();

-- Members see only what is on sale; catalog managers see drafts too.
drop policy packages_select on public.membership_packages;
create policy packages_select on public.membership_packages for select to authenticated
  using (public.is_active_user() and ((is_active and published_at is not null) or public.has_permission('catalog:manage')));
drop policy amenities_select on public.amenities;
create policy amenities_select on public.amenities for select to authenticated
  using (public.is_active_user() and ((is_active and published_at is not null) or public.has_permission('catalog:manage')));

-- G9: the package a renewal is sold as: the newest published version in the line that started at p_id (p_id itself if none).
create function public.newest_package_version(p_id bigint) returns bigint
language sql stable security definer set search_path='' as $$
  with recursive line(id, depth) as (
    select p_id, 0
    union all
    select m.membership_package_id, l.depth + 1 from public.membership_packages m join line l on m.replaces_id = l.id where m.published_at is not null
  )
  select id from line order by depth desc limit 1
$$;
revoke all on function public.newest_package_version(bigint) from public, anon, authenticated;

-- G6: one check for every way something is sold. Packages: the payment's package (filled by snapshot_membership_payment, 0018; named
-- to run after it). Amenities: the purchase row and the booking.
create function public.guard_catalog_for_sale() returns trigger
language plpgsql security definer set search_path='' as $$
begin
  if tg_table_name = 'payments' then
    if new.kind in ('membership', 'renewal') and not exists (select 1 from public.membership_packages p
        where p.membership_package_id = new.membership_package_snapshot_id and p.is_active and p.published_at is not null) then
      perform public.fail('item_not_for_sale', 'P0002');
    end if;
  elsif not exists (select 1 from public.amenities a where a.amenity_id = new.amenity_id and a.is_active and a.published_at is not null) then
    perform public.fail('item_not_for_sale', 'P0002');
  end if;
  return new;
end $$;
revoke all on function public.guard_catalog_for_sale() from public, anon, authenticated;
create trigger zz_payments_for_sale before insert on public.payments for each row execute function public.guard_catalog_for_sale();
create trigger user_amenities_for_sale before insert on public.user_amenities for each row execute function public.guard_catalog_for_sale();
create trigger reservations_for_sale before insert on public.reservations for each row execute function public.guard_catalog_for_sale();

-- Same as 0018 apart from: a renewal is sold as the newest version (G9).
create or replace function public.snapshot_membership_payment() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_package bigint; v_pkg public.membership_packages;
begin
  if new.kind='membership' then
    select membership_package_id into v_package from public.user_membership_packages
      where user_membership_package_id=new.user_membership_package_id;
  elsif new.kind='renewal' then
    select public.newest_package_version(u.membership_package_id) into v_package from public.membership_renewals r
      join public.user_membership_packages u using(user_membership_package_id) where r.renewal_id=new.renewal_id;
  else return new;
  end if;
  select * into v_pkg from public.membership_packages
    where membership_package_id=coalesce(new.membership_package_snapshot_id,v_package);
  if not found then perform public.fail('not_found','22023'); end if;
  new.membership_package_snapshot_id := v_pkg.membership_package_id;
  new.membership_package_name := v_pkg.name;
  new.membership_duration_days := v_pkg.duration_days;
  new.membership_snapshot_source := 'purchase';
  return new;
end $$;

-- Same as 0018 apart from: renews into the newest version (G9).
create or replace function public.renew_membership(p_ump_id bigint,p_discount_code text default null) returns public.payments
language plpgsql security definer set search_path='' as $$
declare v_package bigint;
begin
  perform public.require_active_user();
  select membership_package_id into v_package from public.user_membership_packages
    where user_membership_package_id=p_ump_id and member_id=auth.uid() and status in ('active','expired');
  if not found then perform public.fail('not_renewable','22023'); end if;
  return public.create_membership_extension(auth.uid(),public.newest_package_version(v_package),p_discount_code);
end $$;

-- One-line changes to functions whose bodies are long, the way 0055 changed them: each pair must match exactly once.
do $$
declare
  r record; v_def text; v_new text;
begin
  for r in select * from (values
    -- the desk renewal prices and times the newest version (G9)
    ('public.record_counter_payment_before_terms(public.payment_kind,public.payment_method,uuid,bigint,text,text,text)'::regprocedure,
     'where membership_package_id = v_cur.membership_package_id;',
     'where membership_package_id = public.newest_package_version(v_cur.membership_package_id);'),
    -- drafts stay off the members' booking grid, coaches' "I run this" list, session requests and new codes (G6)
    ('public.amenity_grid(date)'::regprocedure, 'from public.amenities a where a.is_active),', 'from public.amenities a where a.is_active and a.published_at is not null),'),
    ('public.set_runs_amenity(bigint,boolean)'::regprocedure, 'where amenity_id = p_amenity and is_active)', 'where amenity_id = p_amenity and is_active and published_at is not null)'),
    ('public.check_session_time(timestamptz,timestamptz,bigint)'::regprocedure, 'where amenity_id = p_amenity_id and is_active)', 'where amenity_id = p_amenity_id and is_active and published_at is not null)'),
    ('public.create_discount(text,int,timestamptz,int,boolean,jsonb)'::regprocedure, 'p.membership_package_id = v_ref and p.is_active)', 'p.membership_package_id = v_ref and p.is_active and p.published_at is not null)'),
    ('public.create_discount(text,int,timestamptz,int,boolean,jsonb)'::regprocedure, 'a.amenity_id = v_ref and a.is_active)', 'a.amenity_id = v_ref and a.is_active and a.published_at is not null)')
  ) as t(fn, old_text, new_text) loop
    v_def := pg_get_functiondef(r.fn);
    if array_length(string_to_array(v_def, r.old_text), 1) <> 2 then
      raise exception '0061: % does not contain "%" exactly once', r.fn, r.old_text;
    end if;
    execute replace(v_def, r.old_text, r.new_text);   -- create or replace keeps the owner and grants
  end loop;
end $$;

-- G6: publish a draft once every required field is filled. For a package version (G9) the old one is archived in the same step.
create function public.publish_catalog_item(p_kind text, p_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_pkg public.membership_packages; v_am public.amenities; v_name text; v_desc text; v_price numeric; v_missing text[] := '{}';
begin
  perform public.require_active_user();
  perform public.require_permission('catalog:manage');
  if p_kind = 'plan' then
    select * into v_pkg from public.membership_packages where membership_package_id = p_id for update;
    if not found then perform public.fail('not_found'); end if;
    if v_pkg.published_at is not null then perform public.fail('already_published', 'P0002'); end if;
    if not v_pkg.is_active then perform public.fail('record_archived', 'P0002'); end if;
    v_name := v_pkg.name; v_desc := v_pkg.description; v_price := v_pkg.price;
  elsif p_kind = 'amenity' then
    select * into v_am from public.amenities where amenity_id = p_id for update;
    if not found then perform public.fail('not_found'); end if;
    if v_am.published_at is not null then perform public.fail('already_published', 'P0002'); end if;
    if not v_am.is_active then perform public.fail('record_archived', 'P0002'); end if;
    v_name := v_am.name; v_desc := v_am.description; v_price := v_am.price;
  else
    perform public.fail('bad_input');
  end if;
  if coalesce(btrim(v_name), '') = '' then v_missing := v_missing || 'name'::text; end if;
  if coalesce(btrim(v_desc), '') = '' then v_missing := v_missing || 'description'::text; end if;
  if coalesce(v_price, 0) <= 0 then v_missing := v_missing || 'price'::text; end if;
  if v_missing <> '{}' then raise exception 'missing_fields' using errcode = '22023', detail = array_to_string(v_missing, ','); end if;
  if p_kind = 'plan' then
    if v_pkg.replaces_id is not null then
      update public.membership_packages set is_active = false, archive_reason = 'Replaced by a new version'
        where membership_package_id = v_pkg.replaces_id and is_active;
    end if;
    if exists (select 1 from public.membership_packages where name = v_pkg.name and published_at is not null and is_active) then
      perform public.fail('name_taken', 'P0002');
    end if;
    update public.membership_packages set published_at = now() where membership_package_id = p_id;
  else
    update public.amenities set published_at = now() where amenity_id = p_id;
  end if;
  perform public.audit('catalog.publish', case p_kind when 'plan' then 'membership_packages' else 'amenities' end, p_id::text,
                       jsonb_build_object('name', v_name, 'replaces', v_pkg.replaces_id));
end $$;

-- G9: copy a published package into a draft that will replace it. One draft version per package at a time.
create function public.new_package_version(p_id bigint) returns bigint
language plpgsql security definer set search_path='' as $$
declare v public.membership_packages; v_id bigint;
begin
  perform public.require_active_user();
  perform public.require_permission('catalog:manage');
  select * into v from public.membership_packages where membership_package_id = p_id for update;
  if not found then perform public.fail('not_found'); end if;
  if v.published_at is null or not v.is_active then perform public.fail('not_published', 'P0002'); end if;
  if exists (select 1 from public.membership_packages where replaces_id = p_id and published_at is null) then
    perform public.fail('draft_exists', 'P0002');
  end if;
  insert into public.membership_packages (name, description, price, duration_days, benefits, image_path, replaces_id, published_at)
    values (v.name, v.description, v.price, v.duration_days, v.benefits, v.image_path, p_id, null)
    returning membership_package_id into v_id;
  perform public.audit('catalog.new_version', 'membership_packages', v_id::text, jsonb_build_object('name', v.name, 'replaces', p_id));
  return v_id;
end $$;

-- G6: a draft is removed for real. Anything published was (or could have been) sold, so it is archived instead (0024).
create function public.delete_catalog_item(p_kind text, p_id bigint) returns void
language plpgsql security definer set search_path='' as $$
declare v_name text; v_published timestamptz;
begin
  perform public.require_active_user();
  perform public.require_permission('catalog:manage');
  if p_kind = 'plan' then
    select name, published_at into v_name, v_published from public.membership_packages where membership_package_id = p_id for update;
  elsif p_kind = 'amenity' then
    select name, published_at into v_name, v_published from public.amenities where amenity_id = p_id for update;
  else
    perform public.fail('bad_input');
  end if;
  if not found then perform public.fail('not_found'); end if;
  if v_published is not null then perform public.fail('published_archive_instead', 'P0002'); end if;
  if p_kind = 'plan' then delete from public.membership_packages where membership_package_id = p_id;
  else delete from public.amenities where amenity_id = p_id;
  end if;
  perform public.audit('catalog.delete', case p_kind when 'plan' then 'membership_packages' else 'amenities' end, p_id::text,
                       jsonb_build_object('name', v_name));
end $$;

revoke all on function public.publish_catalog_item(text, bigint), public.new_package_version(bigint), public.delete_catalog_item(text, bigint)
  from public, anon;
grant execute on function public.publish_catalog_item(text, bigint), public.new_package_version(bigint), public.delete_catalog_item(text, bigint)
  to authenticated;
