-- Live system test, 2026-11-25.
-- 1. assign_role gave the System admin role to any account (a member in the test), and revoke_role then refused to take it back
--    (super_admin_protected). The app never offers it and create-user refuses it; assign_role now refuses it too.
-- 2. Coach access bought, then the membership refunded: the unpaid coach access payment stayed open, Xendit took the money and no access
--    or refund followed. A cancelled term now expires its member's unpaid coach access; money that still arrives becomes an approved
--    'online' refund through confirm_online_payment (0095).

create or replace function public.assign_role(p_user uuid, p_role text)
 returns void
 language plpgsql
 security definer
 set search_path to ''
as $function$
declare
	v_role smallint;
begin
	perform public.require_permission('roles:manage');
	if p_role = 'admin' then perform public.fail('bad_input', '22023'); end if;   -- 0147: never handed out

	select role_id into v_role from public.roles where name = p_role;
	if not found then perform public.fail('bad_input', '22023'); end if;
	if not exists (select 1 from public.profiles where id = p_user) then perform public.fail('not_found', '22023'); end if;

	insert into public.user_roles (user_id, role_id, assigned_by) values (p_user, v_role, auth.uid()) on conflict do nothing;
	if p_role = 'coach' then
		insert into public.coaches (user_id) values (p_user) on conflict do nothing;
	elsif p_role = 'member' then
		insert into public.members (user_id) values (p_user) on conflict do nothing;
	end if;

	perform public.audit('role.assign', 'user_roles', p_user::text, jsonb_build_object('role', p_role));
end;
$function$;

-- coach access is bought for the term current when it was bought (buy_coach_access), so the payments made during that term are its own
create function public.expire_coach_access_of_cancelled_term() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  update public.payments set status = 'expired'
  where member_id = new.member_id and kind = 'coach_addon' and status = 'pending'
    and created_at >= new.starts_at and created_at < new.ends_at;
  return null;
end $$;

create trigger membership_terms_cancel_coach_access
  after update of cancelled_at on public.membership_terms
  for each row when (old.cancelled_at is null and new.cancelled_at is not null)
  execute function public.expire_coach_access_of_cancelled_term();

revoke all on function public.expire_coach_access_of_cancelled_term() from public, anon, authenticated;
revoke all on function public.assign_role(uuid, text) from public, anon;
grant execute on function public.assign_role(uuid, text) to authenticated;
