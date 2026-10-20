-- Phase 1: retained catalog records and an admin-only, filtered archive.
alter table public.membership_packages add column archived_at timestamptz, add column archived_by uuid references public.profiles(id), add column archive_reason text;
alter table public.amenities add column archived_at timestamptz, add column archived_by uuid references public.profiles(id), add column archive_reason text;
alter table public.discounts add column archived_at timestamptz, add column archived_by uuid references public.profiles(id), add column archive_reason text;

-- Invoker security distinguishes ordinary PostgREST writes from the privileged restore RPC.
create function public.guard_catalog_archive() returns trigger
language plpgsql set search_path='' as $$
declare v_expired boolean := false;
begin
  if tg_op='INSERT' then
    new.archived_at := case when not new.is_active then now() end;
    new.archived_by := case when not new.is_active then auth.uid() end;
    new.archive_reason := case when not new.is_active then 'Created inactive' end;
    return new;
  end if;
  if tg_table_name='discounts' then v_expired := (to_jsonb(old)->>'expires_at')::timestamptz<=now(); end if;
  if not old.is_active or v_expired then
    if current_user in ('authenticated','anon') or not new.is_active or v_expired then
      raise exception 'record_archived' using errcode='P0002';
    end if;
    perform public.require_archive_admin();
    new.archived_at := null; new.archived_by := null; new.archive_reason := null;
  elsif not new.is_active then
    new.archived_at := now(); new.archived_by := auth.uid();
    new.archive_reason := coalesce(nullif(btrim(new.archive_reason),''),'Deactivated '||case tg_table_name when 'discounts' then 'code' when 'amenities' then 'amenity' else 'gym plan' end);
    if char_length(new.archive_reason)>500 then raise exception 'bad_archive_reason' using errcode='22023'; end if;
  else
    new.archived_at := old.archived_at; new.archived_by := old.archived_by; new.archive_reason := old.archive_reason;
  end if;
  return new;
end $$;
revoke all on function public.guard_catalog_archive() from public,anon,authenticated;
create trigger packages_archive_guard before insert or update on public.membership_packages for each row execute function public.guard_catalog_archive();
create trigger amenities_archive_guard before insert or update on public.amenities for each row execute function public.guard_catalog_archive();
create trigger discounts_archive_guard before insert or update on public.discounts for each row execute function public.guard_catalog_archive();
revoke delete on public.membership_packages,public.amenities,public.discounts from authenticated;

create function public.archive_catalog_record(p_type text,p_id bigint,p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare v_key text; v_table text; v_row jsonb; v_reason text := regexp_replace(p_reason,'^[[:space:]]+|[[:space:]]+$','','g');
begin
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_archive_admin();
  v_table := case p_type when 'plan' then 'membership_packages' when 'amenity' then 'amenities' when 'code' then 'discounts' end;
  if v_table is null then perform public.fail('bad_input','22023'); end if;
  v_key := case p_type when 'plan' then 'membership_package_id' when 'amenity' then 'amenity_id' else 'discount_id' end;
  if coalesce(char_length(v_reason),0) not between 1 and 500 then perform public.fail('bad_archive_reason','22023'); end if;
  execute format('select to_jsonb(t) from public.%I t where %I=$1 for update',v_table,v_key) into v_row using p_id;
  if v_row is null then perform public.fail('not_found','22023'); end if;
  if not (v_row->>'is_active')::boolean or (p_type='code' and (v_row->>'expires_at')::timestamptz<=now()) then perform public.fail('record_archived','P0002'); end if;
  execute format('update public.%I set is_active=false,archive_reason=$2 where %I=$1',v_table,v_key) using p_id,v_reason;
  perform public.audit('catalog.archive',v_table,p_id::text,jsonb_build_object('reason',v_reason));
end $$;

create function public.restore_archived_record(p_type text,p_id text) returns void
language plpgsql security definer set search_path='' as $$
declare v_key text; v_table text; v_row jsonb; v_profile public.profiles;
begin
  -- Same lock ordering as archive_account and set_user_active.
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_archive_admin();
  if p_type='account' then
    select * into v_profile from public.profiles where id=p_id::uuid for update;
    if not found then perform public.fail('not_found','22023'); end if;
    if v_profile.is_active then perform public.fail('not_archived','P0002'); end if;
    if v_profile.deleted_at is not null then perform public.restore_account(p_id::uuid);
    else perform public.set_user_active(p_id::uuid,true); end if;
    return;
  end if;
  v_table := case p_type when 'plan' then 'membership_packages' when 'amenity' then 'amenities' when 'code' then 'discounts' end;
  if v_table is null then perform public.fail('bad_input','22023'); end if;
  v_key := case p_type when 'plan' then 'membership_package_id' when 'amenity' then 'amenity_id' else 'discount_id' end;
  execute format('select to_jsonb(t) from public.%I t where %I=$1 for update',v_table,v_key) into v_row using p_id::bigint;
  if v_row is null then perform public.fail('not_found','22023'); end if;
  if p_type='code' and (v_row->>'expires_at')::timestamptz<=now() then perform public.fail('record_not_restorable','P0002'); end if;
  if (v_row->>'is_active')::boolean then perform public.fail('not_archived','P0002'); end if;
  execute format('update public.%I set is_active=true where %I=$1',v_table,v_key) using p_id::bigint;
  perform public.audit('catalog.restore',v_table,p_id,jsonb_build_object('archived_at',v_row->'archived_at','archived_by',v_row->'archived_by','reason',v_row->'archive_reason'));
end $$;

create function public.archive_records(p_type text default '',p_status text default '',p_from date default null,p_to date default null,p_reason text default '',p_offset integer default 0,p_limit integer default 31)
returns table(record_type text,record_id text,name text,status text,archived_at timestamptz,archived_by text,reason text,details text,can_restore boolean)
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_archive_admin();
  if p_offset is null or p_offset<0 or p_limit is null or p_limit not between 1 and 101 or p_from>p_to
    or coalesce(p_type,'') not in ('','account','code','plan','amenity') or coalesce(p_status,'') not in ('','inactive','deleted','expired') then
    perform public.fail('bad_archive_filter','22023');
  end if;
  return query
  with records as (
    select 'account'::text as typ,p.id::text as rid,p.full_name as title,
      case when p.deleted_at is not null then 'deleted' else 'inactive' end as state,
      coalesce(p.deleted_at,a.created_at) as stamp,coalesce(p.deleted_by,a.actor_id)::text as actor,
      coalesce(p.deletion_reason,case when a.created_at is not null then 'Deactivated account' end,'Unknown (legacy record)') as why,
      p.username as info,true as restorable
    from public.profiles p left join lateral (
      select e.created_at,e.actor_id from public.audit_log e where e.entity='profiles' and e.entity_id=p.id::text and e.action='user.deactivate' order by e.created_at desc limit 1
    ) a on true where not p.is_active
    union all
    select 'code',d.discount_id::text,d.code,case when d.expires_at<=now() then 'expired' else 'inactive' end,
      coalesce(d.archived_at,case when d.expires_at<=now() then d.expires_at end),d.archived_by::text,
      coalesce(d.archive_reason,case when d.expires_at<=now() then 'Expired code' end,'Unknown (legacy record)'),
      d.percent::text||'% discount; valid until '||to_char(d.expires_at at time zone 'Asia/Manila','YYYY-MM-DD HH24:MI')||' (Asia/Manila)',d.expires_at>now()
    from public.discounts d where not d.is_active or d.expires_at<=now()
    union all
    select 'plan',p.membership_package_id::text,p.name,'inactive',p.archived_at,p.archived_by::text,coalesce(p.archive_reason,'Unknown (legacy record)'),
      'PHP '||p.price::text||'; '||p.duration_days::text||' days; '||coalesce(p.description,''),true from public.membership_packages p where not p.is_active
    union all
    select 'amenity',a.amenity_id::text,a.name,'inactive',a.archived_at,a.archived_by::text,coalesce(a.archive_reason,'Unknown (legacy record)'),
      'PHP '||a.price::text||'; '||coalesce(a.description,''),true from public.amenities a where not a.is_active
  )
  select r.typ,r.rid,r.title,r.state,r.stamp,r.actor,r.why,r.info,r.restorable from records r
  where (coalesce(p_type,'')='' or r.typ=p_type) and (coalesce(p_status,'')='' or r.state=p_status)
    and (p_from is null or (r.stamp at time zone 'Asia/Manila')::date>=p_from)
    and (p_to is null or (r.stamp at time zone 'Asia/Manila')::date<=p_to)
    and position(lower(coalesce(btrim(p_reason),'')) in lower(r.why))>0
  order by r.stamp desc nulls last,r.typ,r.rid limit p_limit offset p_offset;
end $$;
revoke all on function public.archive_catalog_record(text,bigint,text),public.restore_archived_record(text,text),public.archive_records(text,text,date,date,text,integer,integer) from public,anon,authenticated;
grant execute on function public.archive_catalog_record(text,bigint,text),public.restore_archived_record(text,text),public.archive_records(text,text,date,date,text,integer,integer) to authenticated;

-- Archived account roles/profile details stay read-only, including privileged management RPCs.
create function public.guard_archived_person() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_user uuid;
begin
  if auth.uid() is null then return case when tg_op='DELETE' then old else new end; end if;
  if tg_table_name='profiles' then
    if not old.is_active and new.is_active then perform public.require_archive_admin();
    elsif not old.is_active and (to_jsonb(new)-array['updated_at','deleted_at','deleted_by','deletion_reason'])
        is distinct from (to_jsonb(old)-array['updated_at','deleted_at','deleted_by','deletion_reason']) then
      perform public.fail('record_archived','P0002');
    end if;
    return new;
  end if;
  v_user := case when tg_op='DELETE' then old.user_id else new.user_id end;
  perform 1 from public.profiles where id=v_user for share;
  if exists(select 1 from public.profiles where id=v_user and not is_active) then perform public.fail('record_archived','P0002'); end if;
  return case when tg_op='DELETE' then old else new end;
end $$;
revoke all on function public.guard_archived_person() from public,anon,authenticated;
create trigger profiles_archive_guard before update on public.profiles for each row execute function public.guard_archived_person();
create trigger coaches_archive_guard before update on public.coaches for each row execute function public.guard_archived_person();
create trigger roles_archive_guard before insert or update or delete on public.user_roles for each row execute function public.guard_archived_person();

-- Apply the active-account rule even where an older bucket policy only checks a role permission.
create policy storage_active_accounts on storage.objects as restrictive for all to authenticated
  using (public.is_active_user()) with check (public.is_active_user());
