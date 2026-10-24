-- 0036: the sub-admin role (Phase 13, D15).
--
-- The existing admin role is the super admin and keeps everything. A sub-admin does the front desk's work plus: the admin Overview,
-- editing packages, amenities and discount codes, the revenue report, and adding, deactivating, reactivating and resetting the
-- password of members and coaches. Only the super admin hands out roles, archives or deletes (catalog records and accounts), restores
-- from Archive, voids payments, decides refunds, changes gym settings and reads the audit log, and only the super admin acts on
-- front-desk employees, sub-admins and super admins.
--
-- Most of this follows from permissions; the rest is the explicit super-admin check has_role('admin') already used for archiving.
-- Hard deletes of packages, amenities and codes need nothing here: 0024 took the delete right away from everyone.

insert into public.roles (name, description) values ('subadmin', 'Sub-admin: front desk plus catalog, discounts, revenue and member/coach accounts');

insert into public.role_permissions (role_id, permission_id)
select r.role_id, p.permission_id from public.roles r join public.permissions p
	on (p.resource, p.action) in (('payments', 'verify'), ('payments', 'record'), ('check_ins', 'record'), ('amenity_usages', 'verify'),
	                              ('amenities', 'set_status'), ('support', 'reply'), ('reports', 'cash'), ('reports', 'revenue'),
	                              ('catalog', 'manage'), ('discounts', 'manage'), ('users', 'manage'))
where r.name = 'subadmin';

-- staff = anyone who works the desk
create or replace function public.is_staff() returns boolean
language sql stable security definer
set search_path = ''
as $$
	select public.has_role('admin') or public.has_role('subadmin') or public.has_role('employee');
$$;

-- May the caller act on this person's account? The super admin: anyone. Anyone else with users:manage: members and coaches only.
create function public.can_manage_person(p_user uuid) returns boolean
language sql stable security definer
set search_path = ''
as $$
	select public.is_active_user() and public.has_permission('users:manage') and (public.has_role('admin') or not exists (
		select 1 from public.user_roles ur join public.roles r using (role_id)
		where ur.user_id = p_user and r.name in ('admin', 'subadmin', 'employee')));
$$;
revoke all on function public.can_manage_person(uuid) from public, anon;
grant execute on function public.can_manage_person(uuid) to authenticated;

-- The Users page lists everyone's roles for whoever manages users.
drop policy user_roles_select on public.user_roles;
create policy user_roles_select on public.user_roles for select to authenticated
	using (public.is_active_user() and (user_id = auth.uid() or public.has_permission('users:manage')));

-- Same as 0023, plus: only people the caller may manage.
create or replace function public.set_user_active(p_user uuid,p_active boolean) returns void
language plpgsql security definer set search_path='' as $$
declare v_profile public.profiles;
begin
  perform pg_catalog.pg_advisory_xact_lock(23001,1);
  perform public.require_permission('users:manage');
  if p_user=auth.uid() then perform public.fail('forbidden','42501'); end if;
  if p_active is null then perform public.fail('bad_input','22023'); end if;
  if not public.can_manage_person(p_user) then perform public.fail('forbidden','42501'); end if;
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

-- Same as 0024, except that switching a deactivated (not deleted) account back on is set_user_active's decision, which checks who
-- may manage whom; bringing back a deleted account is still a super-admin restore.
create or replace function public.guard_archived_person() returns trigger
language plpgsql security definer set search_path='' as $$
declare v_user uuid;
begin
  if auth.uid() is null then return case when tg_op='DELETE' then old else new end; end if;
  if tg_table_name='profiles' then
    if not old.is_active and new.is_active then
      if old.deleted_at is not null then perform public.require_archive_admin();
      elsif not public.can_manage_person(old.id) then perform public.fail('forbidden','42501'); end if;
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
