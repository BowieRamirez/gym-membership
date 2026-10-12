-- 0006: row level security (plan 6), table privileges and function privileges
--
-- Two layers: a table privilege says which columns the signed-in user may write at all,
-- and a policy says which rows. Anything not granted here is written by a function in 0005.
-- Every policy also needs is_active_user(), except a user reading their own profile so the
-- app can tell that an account was deactivated.

-- ---------------------------------------------------------------------
-- Privileges
-- ---------------------------------------------------------------------

revoke all on all tables in schema public from anon, authenticated;
grant select on all tables in schema public to authenticated;

grant update (full_name, phone)                                  on public.profiles            to authenticated;
grant update (language, timezone, theme)                         on public.user_settings       to authenticated;
grant update                                                     on public.gym_settings        to authenticated;
grant update (goals)                                             on public.members             to authenticated;
grant update (bio, specialty, hourly_rate, is_available)         on public.coaches             to authenticated;
grant insert, update, delete                                     on public.membership_packages to authenticated;
grant insert, update, delete                                     on public.amenities           to authenticated;
grant insert, update, delete                                     on public.discounts           to authenticated;
grant insert (coach_id, starts_at, ends_at, note), update (starts_at, ends_at, note, status), delete
                                                                 on public.shift_posts         to authenticated;
grant insert (coach_id, member_id, sender_id, body), update (read_at)
                                                                 on public.messages            to authenticated;
grant insert (user_amenity_id, proof_path, notes)                on public.amenity_usages      to authenticated;
grant update (read_at), delete                                   on public.user_notifications  to authenticated;

-- ---------------------------------------------------------------------
-- Turn RLS on for every table
-- ---------------------------------------------------------------------

alter table public.profiles                  enable row level security;
alter table public.roles                     enable row level security;
alter table public.permissions               enable row level security;
alter table public.role_permissions          enable row level security;
alter table public.user_roles                enable row level security;
alter table public.user_settings             enable row level security;
alter table public.gym_settings              enable row level security;
alter table public.members                   enable row level security;
alter table public.coaches                   enable row level security;
alter table public.membership_packages       enable row level security;
alter table public.amenities                 enable row level security;
alter table public.discounts                 enable row level security;
alter table public.user_membership_packages  enable row level security;
alter table public.membership_renewals       enable row level security;
alter table public.user_amenities            enable row level security;
alter table public.payments                  enable row level security;
alter table public.coach_hires               enable row level security;
alter table public.training_sessions         enable row level security;
alter table public.time_requests             enable row level security;
alter table public.session_attendance        enable row level security;
alter table public.shift_posts               enable row level security;
alter table public.messages                  enable row level security;
alter table public.gym_check_ins             enable row level security;
alter table public.amenity_usages            enable row level security;
alter table public.user_notifications        enable row level security;
alter table public.audit_log                 enable row level security;

-- ---------------------------------------------------------------------
-- Identity
-- ---------------------------------------------------------------------

create policy profiles_select_self on public.profiles for select to authenticated
	using (id = auth.uid());

create policy profiles_select on public.profiles for select to authenticated
	using (
		public.is_active_user() and (
			public.is_staff()
			or exists (select 1 from public.coaches c where c.user_id = profiles.id)
			or exists (
				select 1 from public.coach_hires h
				where (h.coach_id = auth.uid() and h.member_id = profiles.id)
				   or (h.member_id = auth.uid() and h.coach_id = profiles.id)
			)
		)
	);

create policy profiles_update_own on public.profiles for update to authenticated
	using (id = auth.uid() and public.is_active_user())
	with check (id = auth.uid());

create policy roles_select on public.roles for select to authenticated
	using (public.is_active_user());
create policy permissions_select on public.permissions for select to authenticated
	using (public.is_active_user());
create policy role_permissions_select on public.role_permissions for select to authenticated
	using (public.is_active_user());

create policy user_roles_select on public.user_roles for select to authenticated
	using (public.is_active_user() and (user_id = auth.uid() or public.has_role('admin')));

create policy user_settings_select on public.user_settings for select to authenticated
	using (public.is_active_user() and user_id = auth.uid());
create policy user_settings_update on public.user_settings for update to authenticated
	using (public.is_active_user() and user_id = auth.uid())
	with check (user_id = auth.uid());

create policy gym_settings_select on public.gym_settings for select to authenticated
	using (public.is_active_user());
create policy gym_settings_update on public.gym_settings for update to authenticated
	using (public.is_active_user() and public.has_permission('gym_settings:manage'))
	with check (public.has_permission('gym_settings:manage'));

-- ---------------------------------------------------------------------
-- People
-- ---------------------------------------------------------------------

create policy members_select on public.members for select to authenticated
	using (
		public.is_active_user() and (
			user_id = auth.uid()
			or public.is_staff()
			or exists (select 1 from public.coach_hires h where h.coach_id = auth.uid() and h.member_id = members.user_id)
		)
	);
create policy members_update_own on public.members for update to authenticated
	using (public.is_active_user() and user_id = auth.uid())
	with check (user_id = auth.uid());

create policy coaches_select on public.coaches for select to authenticated
	using (public.is_active_user());
create policy coaches_update on public.coaches for update to authenticated
	using (public.is_active_user() and (user_id = auth.uid() or public.has_role('admin')))
	with check (user_id = auth.uid() or public.has_role('admin'));

-- ---------------------------------------------------------------------
-- Catalog
-- ---------------------------------------------------------------------

create policy packages_select on public.membership_packages for select to authenticated
	using (public.is_active_user() and (is_active or public.has_permission('catalog:manage')));
create policy packages_insert on public.membership_packages for insert to authenticated
	with check (public.is_active_user() and public.has_permission('catalog:manage'));
create policy packages_update on public.membership_packages for update to authenticated
	using (public.is_active_user() and public.has_permission('catalog:manage'))
	with check (public.has_permission('catalog:manage'));
-- deleting something already used is stopped by the foreign keys (error 23503), which the app reports as "in use"
create policy packages_delete on public.membership_packages for delete to authenticated
	using (public.is_active_user() and public.has_permission('catalog:manage'));

create policy amenities_select on public.amenities for select to authenticated
	using (public.is_active_user() and (is_active or public.has_permission('catalog:manage')));
create policy amenities_insert on public.amenities for insert to authenticated
	with check (public.is_active_user() and public.has_permission('catalog:manage'));
create policy amenities_update on public.amenities for update to authenticated
	using (public.is_active_user() and public.has_permission('catalog:manage'))
	with check (public.has_permission('catalog:manage'));
create policy amenities_delete on public.amenities for delete to authenticated
	using (public.is_active_user() and public.has_permission('catalog:manage'));

-- members never read the table: they enter a code and a function checks it
create policy discounts_select on public.discounts for select to authenticated
	using (public.is_active_user() and public.has_permission('discounts:manage'));
create policy discounts_insert on public.discounts for insert to authenticated
	with check (public.is_active_user() and public.has_permission('discounts:manage'));
create policy discounts_update on public.discounts for update to authenticated
	using (public.is_active_user() and public.has_permission('discounts:manage'))
	with check (public.has_permission('discounts:manage'));
create policy discounts_delete on public.discounts for delete to authenticated
	using (public.is_active_user() and public.has_permission('discounts:manage'));

-- ---------------------------------------------------------------------
-- Entitlements and payments: read only from the app, written by functions
-- ---------------------------------------------------------------------

create policy ump_select on public.user_membership_packages for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.is_staff()));

create policy renewals_select on public.membership_renewals for select to authenticated
	using (
		public.is_active_user() and (
			public.is_staff()
			or exists (select 1 from public.user_membership_packages u
			           where u.user_membership_package_id = membership_renewals.user_membership_package_id and u.member_id = auth.uid())
		)
	);

create policy user_amenities_select on public.user_amenities for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.is_staff()));

create policy payments_select on public.payments for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.is_staff()));

-- ---------------------------------------------------------------------
-- Coaching
-- ---------------------------------------------------------------------

create policy coach_hires_select on public.coach_hires for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or coach_id = auth.uid() or public.is_staff()));

create policy time_requests_select on public.time_requests for select to authenticated
	using (
		public.is_active_user() and (
			coach_id = auth.uid() or member_id = auth.uid()
			or (member_id is null and public.coaches_member(coach_id, auth.uid()))
		)
	);

create policy training_sessions_select on public.training_sessions for select to authenticated
	using (public.is_active_user() and (coach_id = auth.uid() or member_id = auth.uid() or public.is_staff()));

create policy session_attendance_select on public.session_attendance for select to authenticated
	using (
		public.is_active_user() and (
			public.is_staff()
			or exists (select 1 from public.training_sessions s
			           where s.training_session_id = session_attendance.training_session_id
			             and (s.coach_id = auth.uid() or s.member_id = auth.uid()))
		)
	);

create policy shift_posts_select on public.shift_posts for select to authenticated
	using (public.is_active_user() and (coach_id = auth.uid() or public.is_staff() or public.coaches_member(coach_id, auth.uid())));
create policy shift_posts_insert on public.shift_posts for insert to authenticated
	with check (public.is_active_user() and coach_id = auth.uid());
create policy shift_posts_update on public.shift_posts for update to authenticated
	using (public.is_active_user() and coach_id = auth.uid())
	with check (coach_id = auth.uid());
create policy shift_posts_delete on public.shift_posts for delete to authenticated
	using (public.is_active_user() and coach_id = auth.uid());

-- a conversation: readable by its two people, writable only while the member has an active hire and plan
create policy messages_select on public.messages for select to authenticated
	using (public.is_active_user() and (coach_id = auth.uid() or member_id = auth.uid()));
create policy messages_insert on public.messages for insert to authenticated
	with check (
		public.is_active_user()
		and sender_id = auth.uid()
		and public.coaches_member(coach_id, member_id)
		and public.is_active_member(member_id)
	);
create policy messages_mark_read on public.messages for update to authenticated
	using (public.is_active_user() and sender_id <> auth.uid() and (coach_id = auth.uid() or member_id = auth.uid()))
	with check (sender_id <> auth.uid() and (coach_id = auth.uid() or member_id = auth.uid()));

-- ---------------------------------------------------------------------
-- Operations
-- ---------------------------------------------------------------------

create policy check_ins_select on public.gym_check_ins for select to authenticated
	using (public.is_active_user() and (member_id = auth.uid() or public.is_staff()));

create policy amenity_usages_select on public.amenity_usages for select to authenticated
	using (
		public.is_active_user() and (
			public.is_staff()
			or exists (select 1 from public.user_amenities ua
			           where ua.user_amenity_id = amenity_usages.user_amenity_id and ua.member_id = auth.uid())
		)
	);
create policy amenity_usages_insert on public.amenity_usages for insert to authenticated
	with check (
		public.is_active_user() and public.is_active_member()
		and exists (select 1 from public.user_amenities ua
		            where ua.user_amenity_id = amenity_usages.user_amenity_id
		              and ua.member_id = auth.uid() and ua.status = 'active'
		              and (ua.expires_at is null or ua.expires_at > now()))
	);

create policy notifications_select on public.user_notifications for select to authenticated
	using (public.is_active_user() and user_id = auth.uid());
create policy notifications_update on public.user_notifications for update to authenticated
	using (public.is_active_user() and user_id = auth.uid())
	with check (user_id = auth.uid());
create policy notifications_delete on public.user_notifications for delete to authenticated
	using (public.is_active_user() and user_id = auth.uid());

create policy audit_select on public.audit_log for select to authenticated
	using (public.is_active_user() and public.has_permission('audit:read'));

-- ---------------------------------------------------------------------
-- Function privileges: nothing is callable unless it is listed here.
-- Internal helpers, trigger functions and the Auth trigger stay owner-only.
-- ---------------------------------------------------------------------

do $$
declare
	f record;
begin
	for f in
		select p.oid::regprocedure as sig
		from pg_proc p join pg_namespace n on n.oid = p.pronamespace
		where n.nspname = 'public' and p.prokind = 'f'
	loop
		execute format('revoke all on function %s from public, anon, authenticated', f.sig);
	end loop;
end;
$$;

-- used inside policies, so the signed-in role must be able to run them
grant execute on function
	public.has_role(text), public.has_permission(text), public.is_staff(), public.is_active_user(),
	public.is_active_member(uuid), public.coaches_member(uuid, uuid)
to authenticated;

-- the functions the app calls
grant execute on function
	public.avail_membership(bigint, text),
	public.avail_amenity(bigint, text),
	public.renew_membership(bigint, text),
	public.submit_payment_proof(bigint, text, text),
	public.verify_payment(bigint, boolean, text),
	public.record_counter_payment(public.payment_kind, public.payment_method, uuid, bigint, text, text, text),
	public.void_payment(bigint, text),
	public.revenue_summary(date, date),
	public.daily_cash_summary(date),
	public.hire_coach(uuid),
	public.end_hire(bigint),
	public.request_time(uuid, timestamptz, timestamptz, text),
	public.request_reschedule(bigint, timestamptz, timestamptz, text),
	public.respond_time_request(bigint, boolean),
	public.cancel_session(bigint, text),
	public.record_session_attendance(bigint, public.attendance_status, text),
	public.record_check_in(uuid),
	public.verify_amenity_usage(bigint, boolean),
	public.assign_role(uuid, text),
	public.revoke_role(uuid, text),
	public.set_user_active(uuid, boolean),
	public.mark_password_changed()
to authenticated;
