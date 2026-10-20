-- Phase 1, first slice: admin-only, audited account soft deletion. No Auth identity or history is removed.
alter table public.profiles
  add column deleted_at timestamptz,
  add column deleted_by uuid references public.profiles(id),
  add column deletion_reason text,
  add constraint profiles_deletion_metadata check (
    (deleted_at is null and deleted_by is null and deletion_reason is null)
    or (deleted_at is not null and deleted_by is not null and not is_active
      and deletion_reason is not null and char_length(btrim(deletion_reason)) between 1 and 500)
  );

create index profiles_deleted_at_idx on public.profiles(deleted_at) where deleted_at is not null;

create function public.require_archive_admin() returns void
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_permission('users:manage');
  if not public.has_role('admin') then perform public.fail('forbidden','42501'); end if;
end $$;
revoke all on function public.require_archive_admin() from public,anon,authenticated;

create function public.archive_account(p_user uuid, p_reason text) returns void
language plpgsql security definer set search_path='' as $$
declare
  v_profile public.profiles;
  v_reason text := regexp_replace(p_reason,'^[[:space:]]+|[[:space:]]+$','','g');
begin
  -- Serialize administrative activation/role removal; recheck the actor after waiting.
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_archive_admin();
  if p_user=auth.uid() then perform public.fail('self_archive','22023'); end if;
  if coalesce(char_length(v_reason),0) not between 1 and 500 then
    perform public.fail('bad_archive_reason','22023');
  end if;
  select * into v_profile from public.profiles where id=p_user for update;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_profile.deleted_at is not null then perform public.fail('already_processed','P0002'); end if;
  if exists(select 1 from public.user_roles ur join public.roles r using(role_id) where ur.user_id=p_user and r.name='admin')
    and not exists(select 1 from public.profiles p join public.user_roles ur on ur.user_id=p.id
      join public.roles r using(role_id) where p.id<>p_user and p.is_active and r.name='admin') then
    perform public.fail('last_active_admin','22023');
  end if;
  if exists(select 1 from public.training_sessions where status='scheduled' and (member_id=p_user or coach_id=p_user))
    or exists(select 1 from public.coach_hires where status='active' and (member_id=p_user or coach_id=p_user))
    or exists(select 1 from public.payments where status='pending' and member_id=p_user) then
    perform public.fail('account_has_commitments','P0002');
  end if;
  update public.profiles set is_active=false,deleted_at=now(),deleted_by=auth.uid(),deletion_reason=v_reason where id=p_user;
  perform public.audit('user.archive','profiles',p_user::text,jsonb_build_object('reason',v_reason));
end $$;

create function public.restore_account(p_user uuid) returns void
language plpgsql security definer set search_path='' as $$
declare v_profile public.profiles;
begin
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_archive_admin();
  select * into v_profile from public.profiles where id=p_user for update;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_profile.deleted_at is null then perform public.fail('not_archived','P0002'); end if;
  update public.profiles set is_active=true,deleted_at=null,deleted_by=null,deletion_reason=null where id=p_user;
  perform public.audit('user.restore','profiles',p_user::text,
    jsonb_build_object('reason',v_profile.deletion_reason,'deleted_at',v_profile.deleted_at,'deleted_by',v_profile.deleted_by));
end $$;

create or replace function public.set_user_active(p_user uuid,p_active boolean) returns void
language plpgsql security definer set search_path='' as $$
declare v_profile public.profiles;
begin
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_permission('users:manage');
  if p_user=auth.uid() then perform public.fail('forbidden','42501'); end if;
  if p_active is null then perform public.fail('bad_input','22023'); end if;
  select * into v_profile from public.profiles where id=p_user for update;
  if not found then perform public.fail('not_found','22023'); end if;
  if v_profile.deleted_at is not null then perform public.fail('account_archived','P0002'); end if;
  if not p_active and exists(select 1 from public.user_roles ur join public.roles r using(role_id) where ur.user_id=p_user and r.name='admin')
    and not exists(select 1 from public.profiles p join public.user_roles ur on ur.user_id=p.id
      join public.roles r using(role_id) where p.id<>p_user and p.is_active and r.name='admin') then
    perform public.fail('last_active_admin','22023');
  end if;
  update public.profiles set is_active=p_active where id=p_user;
  perform public.audit(case when p_active then 'user.activate' else 'user.deactivate' end,'profiles',p_user::text);
end $$;

create or replace function public.revoke_role(p_user uuid,p_role text) returns void
language plpgsql security definer set search_path='' as $$
begin
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_permission('roles:manage');
  if p_user=auth.uid() and p_role='admin' then perform public.fail('forbidden','42501'); end if;
  if p_role='admin' and exists(select 1 from public.profiles p join public.user_roles ur on ur.user_id=p.id
      join public.roles r using(role_id) where p.id=p_user and p.is_active and r.name='admin')
    and not exists(select 1 from public.profiles p join public.user_roles ur on ur.user_id=p.id
      join public.roles r using(role_id) where p.id<>p_user and p.is_active and r.name='admin') then
    perform public.fail('last_active_admin','22023');
  end if;
  delete from public.user_roles where user_id=p_user and role_id=(select role_id from public.roles where name=p_role);
  perform public.audit('role.revoke','user_roles',p_user::text,jsonb_build_object('role',p_role));
end $$;

-- Commitment creation and archive use conflicting profile locks. An insert which waited for an archive
-- must observe the inactive participant rather than leaving a new commitment on a deleted account.
create function public.guard_account_commitment() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_row jsonb := to_jsonb(new); v_users uuid[];
begin
  if (tg_table_name='payments' and v_row->>'status'<>'pending')
    or (tg_table_name='coach_hires' and v_row->>'status'<>'active')
    or (tg_table_name='training_sessions' and v_row->>'status'<>'scheduled') then return new; end if;
  v_users := array_remove(array[(v_row->>'member_id')::uuid,(v_row->>'coach_id')::uuid],null);
  perform 1 from public.profiles where id=any(v_users) order by id for share;
  if exists(select 1 from public.profiles where id=any(v_users) and not is_active) then
    perform public.fail('account_inactive','22023');
  end if;
  return new;
end $$;
revoke all on function public.guard_account_commitment() from public,anon,authenticated;
create trigger payments_active_account before insert or update on public.payments
  for each row execute function public.guard_account_commitment();
create trigger hires_active_accounts before insert or update on public.coach_hires
  for each row execute function public.guard_account_commitment();
create trigger sessions_active_accounts before insert or update on public.training_sessions
  for each row execute function public.guard_account_commitment();

create or replace function public.mark_password_changed() returns void
language plpgsql security definer set search_path='' as $$
begin
  perform public.require_active_user();
  update public.profiles set must_change_password=false where id=auth.uid();
end $$;

revoke all on function public.archive_account(uuid,text),public.restore_account(uuid) from public,anon,authenticated;
grant execute on function public.archive_account(uuid,text),public.restore_account(uuid) to authenticated;
