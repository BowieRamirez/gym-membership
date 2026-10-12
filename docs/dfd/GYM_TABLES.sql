-- =====================================================================
-- Gym Membership: database tables (Supabase / PostgreSQL 15+)
--
-- Decisions this schema follows:
--   * Members and coaches use the app. Every person is a Supabase Auth
--     user; profiles.id = auth.users.id. Members sign up themselves (a
--     guest is a member with no active membership); the owner creates
--     coaches and front-desk staff through the create-user Edge Function.
--   * People are keyed by uuid. members.user_id and coaches.user_id are the
--     primary keys, so the same uuid identifies a person everywhere.
--   * PHP only. No currency columns; money is numeric(12,2).
--   * Payments: QR (gym's static QR + payment code + reference no. +
--     screenshot, verified by staff), cash at the counter, and walk-ins.
--     No payments API.
--
-- Tables, types, constraints and indexes only. Row level security,
-- server-side functions, storage buckets, pg_cron jobs and seed data are
-- planned as separate migrations in supabase/migrations/.
-- =====================================================================

create extension if not exists btree_gist;

-- ---------------------------------------------------------------------
-- Types
-- ---------------------------------------------------------------------

create type membership_status    as enum ('pending', 'active', 'expired', 'cancelled');
create type renewal_status       as enum ('pending', 'completed', 'cancelled');
create type payment_status       as enum ('pending', 'verified', 'rejected', 'expired', 'voided');
create type payment_method       as enum ('qr', 'cash', 'gcash', 'bank_transfer');
create type payment_kind         as enum ('membership', 'renewal', 'amenity', 'walk_in');
create type coach_hire_status    as enum ('active', 'ended', 'cancelled');
create type time_request_status  as enum ('pending', 'approved', 'rejected', 'cancelled');
create type requested_by_role    as enum ('member', 'coach');
create type session_status       as enum ('scheduled', 'completed', 'cancelled', 'no_show');
create type attendance_status    as enum ('present', 'late', 'absent', 'excused');
create type shift_status         as enum ('scheduled', 'cancelled');
create type amenity_usage_status as enum ('pending', 'verified', 'rejected');
create type notification_type    as enum ('time_request', 'membership', 'payment', 'attendance', 'message', 'system');

-- ---------------------------------------------------------------------
-- Identity and access
-- ---------------------------------------------------------------------

create table profiles (
	id                   uuid        primary key references auth.users (id) on delete cascade,
	username             text        not null unique,
	full_name            text        not null,
	phone                text,
	is_active            boolean     not null default true,
	must_change_password boolean     not null default true,
	created_at           timestamptz not null default now(),
	updated_at           timestamptz not null default now()
);

comment on table profiles is 'One row per Supabase Auth user. Login, password and sessions live in auth.users / auth.sessions';
comment on column profiles.must_change_password is 'True until the user replaces the temporary password the admin gave them';

create table roles (
	role_id     smallint    generated always as identity primary key,
	name        text        not null unique,
	description text,
	created_at  timestamptz not null default now()
);

comment on table roles is 'admin | employee | coach | member';

create table permissions (
	permission_id smallint    generated always as identity primary key,
	resource      text        not null,
	action        text        not null,
	description   text,
	created_at    timestamptz not null default now(),
	unique (resource, action)
);

comment on table permissions is 'Fine-grained permissions, e.g. resource = payments, action = verify';

create table role_permissions (
	role_id       smallint    not null references roles (role_id) on delete cascade,
	permission_id smallint    not null references permissions (permission_id) on delete cascade,
	granted_at    timestamptz not null default now(),
	primary key (role_id, permission_id)
);

create table user_roles (
	user_id     uuid        not null references profiles (id) on delete cascade,
	role_id     smallint    not null references roles (role_id) on delete restrict,
	assigned_at timestamptz not null default now(),
	assigned_by uuid        references profiles (id) on delete set null,
	primary key (user_id, role_id)
);

create table user_settings (
	user_id    uuid        primary key references profiles (id) on delete cascade,
	language   text        not null default 'en',
	timezone   text        not null default 'Asia/Manila',
	theme      text        not null default 'system' check (theme in ('system', 'light', 'dark')),
	updated_at timestamptz not null default now()
);

comment on table user_settings is 'Per-user preferences (one row per user)';

create table gym_settings (
	id                   smallint      primary key default 1 check (id = 1),
	gym_name             text          not null,
	qr_image_path        text,
	qr_account_name      text,
	walk_in_price        numeric(12,2) not null default 0 check (walk_in_price >= 0),
	payment_expiry_hours int           not null default 48 check (payment_expiry_hours > 0),
	receipt_prefix       text          not null default 'OR',
	updated_at           timestamptz   not null default now()
);

comment on table gym_settings is 'Single-row gym configuration';
comment on column gym_settings.qr_image_path is 'Path in the gym-assets bucket to the gym''s GCash / Maya / QR Ph code';
comment on column gym_settings.payment_expiry_hours is 'Unpaid QR payments expire after this many hours';

-- ---------------------------------------------------------------------
-- People
-- ---------------------------------------------------------------------

create table members (
	user_id    uuid        primary key references profiles (id) on delete restrict,
	goals      text,
	created_at timestamptz not null default now()
);

comment on table members is 'Member details. user_id is the member''s Auth uuid. Deactivate (profiles.is_active) instead of deleting';

create table coaches (
	user_id      uuid          primary key references profiles (id) on delete restrict,
	bio          text,
	specialty    text,
	hourly_rate  numeric(12,2) check (hourly_rate >= 0),
	is_available boolean       not null default true,
	created_at   timestamptz   not null default now()
);

comment on table coaches is 'Coach details. user_id is the coach''s Auth uuid. Deactivate (profiles.is_active) instead of deleting';

-- ---------------------------------------------------------------------
-- Catalog
-- ---------------------------------------------------------------------

create table membership_packages (
	membership_package_id bigint        generated always as identity primary key,
	name                  text          not null unique,
	description           text,
	price                 numeric(12,2) not null check (price >= 0),
	duration_days         int           not null check (duration_days > 0),
	is_active             boolean       not null default true,
	created_at            timestamptz   not null default now()
);

comment on table membership_packages is 'Membership packages for sale (prices in PHP)';

create table amenities (
	amenity_id    bigint        generated always as identity primary key,
	name          text          not null unique,
	description   text,
	price         numeric(12,2) not null default 0 check (price >= 0),
	duration_days int           check (duration_days > 0),
	is_active     boolean       not null default true,
	created_at    timestamptz   not null default now()
);

comment on table amenities is 'Amenities for sale (prices in PHP)';
comment on column amenities.duration_days is 'How long access lasts once paid; null = no expiry';

create table discounts (
	discount_id bigint      generated always as identity primary key,
	code        text        not null,
	percent     int         not null check (percent between 1 and 100),
	starts_at   timestamptz not null default now(),
	expires_at  timestamptz not null,
	is_active   boolean     not null default true,
	created_at  timestamptz not null default now(),
	check (expires_at > starts_at)
);

create unique index discounts_code_key on discounts (upper(code));

comment on table discounts is 'Discount codes. Codes are case-insensitive and usable once per member';

-- ---------------------------------------------------------------------
-- Entitlements
-- ---------------------------------------------------------------------

create table user_membership_packages (
	user_membership_package_id bigint            generated always as identity primary key,
	member_id                  uuid              not null references members (user_id),
	membership_package_id      bigint            not null references membership_packages (membership_package_id),
	starts_at                  timestamptz,
	ends_at                    timestamptz,
	status                     membership_status not null default 'pending',
	created_at                 timestamptz       not null default now(),
	check (ends_at > starts_at),
	check (status <> 'active' or (starts_at is not null and ends_at is not null))
);

create unique index user_membership_packages_one_active
	on user_membership_packages (member_id) where status = 'active';

comment on table user_membership_packages is 'A member''s membership. Pending until its payment is verified; dates are set on activation';

create table membership_renewals (
	renewal_id                 bigint         generated always as identity primary key,
	user_membership_package_id bigint         not null references user_membership_packages (user_membership_package_id),
	previous_ends_at           timestamptz    not null,
	new_starts_at              timestamptz    not null,
	new_ends_at                timestamptz    not null,
	status                     renewal_status not null default 'pending',
	created_at                 timestamptz    not null default now(),
	completed_at               timestamptz,
	notes                      text,
	check (new_ends_at > new_starts_at)
);

comment on table membership_renewals is 'Renewal of a membership term. Paid by the payment that points to it';
comment on column membership_renewals.previous_ends_at is 'The membership''s ends_at before this renewal, restored if the renewal payment is voided';
comment on column membership_renewals.new_starts_at is 'Current ends_at when renewed early, otherwise the renewal date';

create table user_amenities (
	user_amenity_id bigint            generated always as identity primary key,
	member_id       uuid              not null references members (user_id),
	amenity_id      bigint            not null references amenities (amenity_id),
	status          membership_status not null default 'pending',
	availed_at      timestamptz       not null default now(),
	expires_at      timestamptz
);

comment on table user_amenities is 'A member''s amenity access. Pending until its payment is verified';

-- ---------------------------------------------------------------------
-- Payments
-- ---------------------------------------------------------------------

create table payments (
	payment_id                 bigint         generated always as identity primary key,
	payment_code               text           not null unique,
	kind                       payment_kind   not null,
	method                     payment_method not null,
	status                     payment_status not null default 'pending',
	amount                     numeric(12,2)  not null check (amount >= 0),
	discount_id                bigint         references discounts (discount_id),
	discount_amount            numeric(12,2)  not null default 0 check (discount_amount >= 0),

	-- Who paid
	member_id                  uuid           references members (user_id),
	walk_in_name               text,

	-- What it pays for (exactly one, or none for a walk-in)
	user_membership_package_id bigint         references user_membership_packages (user_membership_package_id),
	renewal_id                 bigint         references membership_renewals (renewal_id),
	user_amenity_id            bigint         references user_amenities (user_amenity_id),

	-- QR proof
	reference_no               text,
	proof_path                 text,
	expires_at                 timestamptz,

	-- Staff actions
	recorded_by                uuid           references profiles (id),
	verified_by                uuid           references profiles (id),
	verified_at                timestamptz,
	receipt_no                 text           unique,
	rejected_reason            text,
	voided_by                  uuid           references profiles (id),
	voided_at                  timestamptz,
	void_reason                text,

	created_at                 timestamptz    not null default now(),

	constraint payments_target_matches_kind check (
		case kind
			when 'membership' then user_membership_package_id is not null and renewal_id is null and user_amenity_id is null
			when 'renewal'    then renewal_id is not null and user_membership_package_id is null and user_amenity_id is null
			when 'amenity'    then user_amenity_id is not null and user_membership_package_id is null and renewal_id is null
			when 'walk_in'    then num_nonnulls(user_membership_package_id, renewal_id, user_amenity_id) = 0
		end
	),
	constraint payments_payer check (
		(kind = 'walk_in' and member_id is null and walk_in_name is not null)
		or (kind <> 'walk_in' and member_id is not null and walk_in_name is null)
	),
	constraint payments_walk_in_at_counter check (kind <> 'walk_in' or method <> 'qr'),
	constraint payments_qr_expires check (method <> 'qr' or expires_at is not null),
	constraint payments_counter_recorded check (method = 'qr' or recorded_by is not null),
	constraint payments_not_self_verified check (verified_by is null or member_id is null or verified_by <> member_id),
	constraint payments_verified_complete check (
		status not in ('verified', 'voided')
		or (verified_by is not null and verified_at is not null and receipt_no is not null)
	),
	constraint payments_rejected_reason check (status <> 'rejected' or rejected_reason is not null),
	constraint payments_voided_complete check (
		status <> 'voided'
		or (voided_by is not null and voided_at is not null and void_reason is not null)
	)
);

create unique index payments_reference_no_key
	on payments (reference_no) where reference_no is not null and status in ('pending', 'verified');
create unique index payments_discount_once_per_member
	on payments (member_id, discount_id) where discount_id is not null and status in ('pending', 'verified');

comment on table payments is 'QR, cash, GCash and bank payments in PHP. Verified payments are never edited, only voided';
comment on column payments.payment_code is 'Short code (e.g. GYM-7F3K2) the member types into the e-wallet message so staff can match the transfer';
comment on column payments.amount is 'Amount charged after discount, kept even if the package price changes later';
comment on column payments.reference_no is 'E-wallet or bank reference number entered by the member or staff';
comment on column payments.proof_path is 'Screenshot path in the payment-proofs bucket';
comment on column payments.receipt_no is 'Assigned on verification, e.g. OR-2026-000123';

-- ---------------------------------------------------------------------
-- Coaching
-- ---------------------------------------------------------------------

create table coach_hires (
	coach_hire_id bigint            generated always as identity primary key,
	member_id     uuid              not null references members (user_id),
	coach_id      uuid              not null references coaches (user_id),
	status        coach_hire_status not null default 'active',
	hired_at      timestamptz       not null default now(),
	ended_at      timestamptz,
	check (member_id <> coach_id),
	check (ended_at is null or ended_at >= hired_at)
);

create unique index coach_hires_one_active
	on coach_hires (member_id, coach_id) where status = 'active';

comment on table coach_hires is 'A member hiring a coach';

create table training_sessions (
	training_session_id bigint         generated always as identity primary key,
	coach_id            uuid           not null references coaches (user_id),
	member_id           uuid           not null references members (user_id),
	title               text,
	scheduled_start     timestamptz    not null,
	scheduled_end       timestamptz    not null,
	status              session_status not null default 'scheduled',
	notes               text,
	created_at          timestamptz    not null default now(),
	check (scheduled_end > scheduled_start),
	constraint training_sessions_no_coach_overlap exclude using gist (
		coach_id with =, tstzrange(scheduled_start, scheduled_end) with &&
	) where (status = 'scheduled'),
	constraint training_sessions_no_member_overlap exclude using gist (
		member_id with =, tstzrange(scheduled_start, scheduled_end) with &&
	) where (status = 'scheduled')
);

comment on table training_sessions is 'Training sessions between a coach and a member';

create table time_requests (
	time_request_id     bigint              generated always as identity primary key,
	coach_id            uuid                not null references coaches (user_id),
	member_id           uuid                references members (user_id),
	training_session_id bigint              references training_sessions (training_session_id) on delete set null,
	requested_by        requested_by_role   not null,
	requested_start     timestamptz         not null,
	requested_end       timestamptz         not null,
	status              time_request_status not null default 'pending',
	message             text,
	responded_at        timestamptz,
	created_at          timestamptz         not null default now(),
	check (requested_end > requested_start),
	check (requested_by = 'coach' or member_id is not null),
	check (training_session_id is null or member_id is not null)
);

comment on table time_requests is 'Booking or reschedule requests, and open slots offered by coaches';
comment on column time_requests.member_id is 'Null when a coach is offering an open slot';
comment on column time_requests.training_session_id is 'Set when the request moves an existing session';

create table session_attendance (
	training_session_id bigint            primary key references training_sessions (training_session_id) on delete cascade,
	status              attendance_status not null default 'present',
	checked_in_at       timestamptz,
	recorded_by         uuid              references profiles (id),
	notes               text,
	created_at          timestamptz       not null default now()
);

comment on table session_attendance is 'Attendance for a session (one per session). Recording it also sets the session status';

create table shift_posts (
	shift_post_id bigint       generated always as identity primary key,
	coach_id      uuid         not null references coaches (user_id),
	starts_at     timestamptz  not null,
	ends_at       timestamptz  not null,
	note          text,
	status        shift_status not null default 'scheduled',
	created_at    timestamptz  not null default now(),
	check (ends_at > starts_at),
	constraint shift_posts_no_overlap exclude using gist (
		coach_id with =, tstzrange(starts_at, ends_at) with &&
	) where (status = 'scheduled')
);

comment on table shift_posts is 'Hours a coach is on the floor, visible to their trainees';

create table messages (
	message_id bigint      generated always as identity primary key,
	coach_id   uuid        not null references coaches (user_id),
	member_id  uuid        not null references members (user_id),
	sender_id  uuid        not null references profiles (id),
	body       text        not null check (length(trim(body)) > 0),
	sent_at    timestamptz not null default now(),
	read_at    timestamptz,
	check (sender_id = coach_id or sender_id = member_id)
);

comment on table messages is 'Coach and member conversation. Sending requires an active coach_hires row (enforced by RLS)';

-- ---------------------------------------------------------------------
-- Operations
-- ---------------------------------------------------------------------

create table gym_check_ins (
	check_in_id   bigint      generated always as identity primary key,
	member_id     uuid        references members (user_id),
	payment_id    bigint      unique references payments (payment_id),
	checked_in_at timestamptz not null default now(),
	recorded_by   uuid        references profiles (id),
	check (num_nonnulls(member_id, payment_id) = 1)
);

comment on table gym_check_ins is 'Gym entries. A member, or a walk-in identified by their walk-in payment';

create table amenity_usages (
	amenity_usage_id bigint               generated always as identity primary key,
	user_amenity_id  bigint               not null references user_amenities (user_amenity_id),
	used_at          timestamptz          not null default now(),
	proof_path       text,
	status           amenity_usage_status not null default 'pending',
	verified_by      uuid                 references profiles (id),
	verified_at      timestamptz,
	notes            text,
	check (status = 'pending' or (verified_by is not null and verified_at is not null))
);

comment on table amenity_usages is 'Amenity visits, confirmed by staff';
comment on column amenity_usages.proof_path is 'Path in the amenity-proofs bucket';

create table user_notifications (
	notification_id bigint            generated always as identity primary key,
	user_id         uuid              not null references profiles (id) on delete cascade,
	type            notification_type not null,
	title           text              not null,
	body            text,
	reference       jsonb,
	read_at         timestamptz,
	created_at      timestamptz       not null default now()
);

comment on table user_notifications is 'In-app notifications. Unread when read_at is null';
comment on column user_notifications.reference is 'Deep-link payload, e.g. {"time_request_id": 42}';

create table audit_log (
	audit_id   bigint      generated always as identity primary key,
	actor_id   uuid        references profiles (id) on delete set null,
	action     text        not null,
	entity     text        not null,
	entity_id  text,
	details    jsonb,
	created_at timestamptz not null default now()
);

comment on table audit_log is 'Append-only record of sensitive actions. Written only by server-side functions';

-- ---------------------------------------------------------------------
-- Indexes on foreign keys and common filters
-- ---------------------------------------------------------------------

create index role_permissions_permission_id_idx      on role_permissions (permission_id);
create index user_roles_role_id_idx                  on user_roles (role_id);
create index user_roles_assigned_by_idx              on user_roles (assigned_by);

create index user_membership_packages_member_id_idx  on user_membership_packages (member_id);
create index user_membership_packages_package_id_idx on user_membership_packages (membership_package_id);
create index user_membership_packages_status_idx     on user_membership_packages (status);
create index membership_renewals_ump_id_idx          on membership_renewals (user_membership_package_id);
create index user_amenities_member_id_idx            on user_amenities (member_id);
create index user_amenities_amenity_id_idx           on user_amenities (amenity_id);

create index payments_member_id_idx                  on payments (member_id);
create index payments_discount_id_idx                on payments (discount_id);
create index payments_ump_id_idx                     on payments (user_membership_package_id);
create index payments_renewal_id_idx                 on payments (renewal_id);
create index payments_user_amenity_id_idx            on payments (user_amenity_id);
create index payments_recorded_by_idx                on payments (recorded_by);
create index payments_verified_by_idx                on payments (verified_by);
create index payments_voided_by_idx                  on payments (voided_by);
create index payments_status_idx                     on payments (status);
create index payments_created_at_idx                 on payments (created_at);

create index coach_hires_coach_id_idx                on coach_hires (coach_id);
create index coach_hires_member_id_idx               on coach_hires (member_id);
create index training_sessions_coach_id_idx          on training_sessions (coach_id, scheduled_start);
create index training_sessions_member_id_idx         on training_sessions (member_id, scheduled_start);
create index time_requests_coach_id_idx              on time_requests (coach_id);
create index time_requests_member_id_idx             on time_requests (member_id);
create index time_requests_session_id_idx            on time_requests (training_session_id);
create index session_attendance_recorded_by_idx      on session_attendance (recorded_by);
create index shift_posts_coach_id_idx                on shift_posts (coach_id, starts_at);
create index messages_conversation_idx               on messages (coach_id, member_id, sent_at);
create index messages_member_id_idx                  on messages (member_id);
create index messages_sender_id_idx                  on messages (sender_id);

create index gym_check_ins_member_id_idx             on gym_check_ins (member_id, checked_in_at);
create index gym_check_ins_recorded_by_idx           on gym_check_ins (recorded_by);
create index amenity_usages_user_amenity_id_idx      on amenity_usages (user_amenity_id);
create index amenity_usages_verified_by_idx          on amenity_usages (verified_by);
create index user_notifications_user_id_idx          on user_notifications (user_id, created_at desc);
create index audit_log_actor_id_idx                  on audit_log (actor_id);
create index audit_log_entity_idx                    on audit_log (entity, entity_id);
create index audit_log_created_at_idx                on audit_log (created_at);

-- ---------------------------------------------------------------------
-- updated_at triggers
-- ---------------------------------------------------------------------

create or replace function set_updated_at() returns trigger
language plpgsql
set search_path = ''
as $$
begin
	new.updated_at = now();
	return new;
end;
$$;

create trigger profiles_set_updated_at      before update on profiles      for each row execute function set_updated_at();
create trigger user_settings_set_updated_at before update on user_settings for each row execute function set_updated_at();
create trigger gym_settings_set_updated_at  before update on gym_settings  for each row execute function set_updated_at();