# Database implementation plan (Supabase)

> **Status (2026-10-25): built and live.** Sections 1 to 17 are the original hand-off spec (migrations `0001` to `0012`) and are kept as written. The migrations in [`supabase/migrations/`](../../supabase/migrations) are now the source of truth; [Current state](#current-state-2026-10-25) lists what each later migration changed. `GYM_TABLES.sql` is the original design only.

## Current state (2026-10-25)

Live project: migrations `0001` to `0038` applied; Edge Functions `create-user` and `reset-password` deployed. Every migration passes `supabase/tests/local` (309 checks, plus the populated-history upgrade checks in `phase7-migration.mjs`). Each file starts with a comment that explains it.

**Roles.** `member`, `coach`, `employee` (front desk), `subadmin` and `admin` (shown as "Super admin"). The sub-admin has the front desk's permissions plus `catalog:manage`, `discounts:manage`, `reports:revenue` and `users:manage` for members and coaches only. Only the super admin hands out roles, archives, restores, voids payments (`payments:void`, also needed for refund decisions), changes gym settings and reads the audit log. From the app, nobody can change, deactivate or delete a super admin (0037 triggers). Role lists live in three places that must agree: `handle_new_user` (latest in 0037), `apply_role_from_metadata` (0011, 0038) and the `create-user` function.

**Tables added since the original design:** `user_last_seen`, `support_threads`, `support_messages` (0015, 0017), `amenity_follows` (0014), `membership_terms`, `refund_requests` (0018), `time_request_events` (0031), `reservations` (0029), `coach_visits` (0032), `member_codes`, `member_scans` (0034).

| Migration | What it adds or changes |
|---|---|
| 0013 | Profile pictures (`avatars` bucket, `profiles.avatar_path`) |
| 0014 | Amenity status (available / in use / unavailable) through `set_amenity_status`, with alerts to followers |
| 0015–0017 | Messenger: delivery tracking, front-desk support chat, photos (`chat-attachments` bucket), Realtime and last seen |
| 0018–0020 | Paid membership terms and queued extensions; cancel and refund within 24 hours of verification; members can withdraw an unverified payment; approved refunds lower revenue on the approval day |
| 0021–0022 | Session-request limits per coach, coach invitations, and one active booking per coach and member pair |
| 0023–0024 | Archive instead of delete for accounts and catalog records (packages, amenities, discount codes), restore by the super admin |
| 0025 | Refund decisions need `payments:void` |
| 0026 | Discount codes with a use limit |
| 0027 | Receipt-level `revenue_lines` for the revenue PDF |
| 0028 | Gym opening hours (Asia/Manila) and coach shift rules |
| 0029 | Amenity reservations without overlaps, with a 30-minute cooldown |
| 0030–0031 | Structured training requests with coach counter-proposals and an event history |
| 0032 | Coach visits: check in and out at the gym, automatic checkout at closing; hiring needs the coach checked in |
| 0033 | Shifts cannot be posted in the past; coach rate on the profile |
| 0034 | A permanent member QR code (identification only) and an audited `scan_member` |
| 0035 | First and last name columns; `confirm_my_email` sets `profiles.confirmed_email` from an emailed-code session |
| 0036 | The `subadmin` role and the permission split above |
| 0037 | Super admin accounts are protected from the app |
| 0038 | Sub-admins created through `create-user` get their role (and repairs accounts made before the fix) |

**Rules for new migrations:** never edit an applied migration; add the next number. Revoke execute on new functions from `public, anon` (the check "anonymous reaches no table and no security definer function" in `rules.mjs` fails otherwise). Some migrations have a read-only preflight in `supabase/preflight/` to run on the live project first.

## How to use this document

This is a hand-off spec for whoever builds the backend. Build in the order of [section 13](#13-order-of-work). Where this document and older docs disagree (`docs/specs/PRD.md`, `docs/specs/technical-spec.md`, `docs/dfd/gym-membership-db-diagram.md`), this document wins; those docs are updated as the last step.

Rules for the implementer:

- Keep the app's existing structure: Views → ViewModels → Services → `ISupabaseGateway`. Only the gateway talks to Supabase.
- Never ship the service-role key in the app. Anything that needs it runs in an Edge Function.
- Every table has row level security turned on. Anything that changes more than one row, money, roles or status goes through a `security definer` function, not direct table writes.
- Keep `DemoGateway` and the tests passing at every step.

## Contents

1. [Decisions](#1-decisions)
2. [What the app does today](#2-what-the-app-does-today)
3. [Schema](#3-schema)
4. [Accounts and sign-in](#4-accounts-and-sign-in)
5. [Roles and permissions](#5-roles-and-permissions)
6. [Row level security](#6-row-level-security)
7. [Server-side functions](#7-server-side-functions)
8. [Payments in detail](#8-payments-in-detail)
9. [Storage](#9-storage)
10. [Scheduled jobs](#10-scheduled-jobs)
11. [Seed data](#11-seed-data)
12. [App changes](#12-app-changes)
13. [Order of work](#13-order-of-work)
14. [Tests](#14-tests)
15. [Defaults and open choices](#15-defaults-and-open-choices)
16. [Definition of done](#16-definition-of-done)
17. [Out of scope](#17-out-of-scope)

## 1. Decisions

| Topic | Decision |
|---|---|
| Who uses the app | Members and coaches on mobile; front desk (employee) and admin (owner) on Windows. Everyone logs in |
| Identity | Every person is a Supabase Auth user. `profiles.id` = `auth.users.id`. Passwords and sessions stay in Supabase Auth; there is no `users` or `user_session` table |
| Member sign-up | Members sign up themselves. A trigger on `auth.users` creates their profile, settings, member row and `member` role |
| Guests | A guest is a member with no active membership. It is not a separate role |
| Staff accounts | The owner creates coaches and front-desk staff (and can create members) through the `create-user` Edge Function, with a temporary password |
| IDs | People are keyed by uuid. `members.user_id` and `coaches.user_id` are primary keys. Other tables use `bigint generated always as identity` |
| Currency | PHP only. No currency columns. Money is `numeric(12,2)` |
| Payments | No payments API. QR in the app (gym's static QR + payment code + reference number + screenshot, verified by staff), cash at the counter, and walk-ins |
| Deleting | People are deactivated (`profiles.is_active = false`), never deleted. Payments are voided, never deleted or edited. Catalog items that have been used are deactivated (`is_active = false`) instead of deleted |
| Time | All timestamps are `timestamptz`. The gym's timezone is `Asia/Manila` |

## 2. What the app does today

- .NET 10 MAUI app. All backend access goes through `ISupabaseGateway` (`src/GymMembership.Core/Services/Foundation.cs`):
  - `SignInAsync`, `SignUpAsync(email, password, username)`, `SignOutAsync`, `RestoreSessionAsync`, `CurrentUserId`
  - `ListAsync<T>(where)`, `InsertAsync<T>`, `UpdateAsync<T>`, `DeleteAsync<T>`
  - `RpcAsync(function, args)`, `UploadAsync(bucket, path, data)`
- The only implementation today is `DemoGateway` (in memory), registered in `src/GymMembership.App/MauiProgram.cs`:

  ```csharp
  s.AddSingleton<ISupabaseGateway, DemoGateway>();
  ```

- Models in `src/GymMembership.Core/Models/Models.cs` map to table names with Postgrest attributes.
- RPC names the services call today: `assign_role`, `avail_amenity`, `avail_membership`, `cancel_session`, `change_password`, `create_user`, `record_check_in`, `record_session_attendance`, `renew_membership`, `request_reschedule`, `reset_password`, `respond_time_request`, `revenue_summary`, `revoke_role`, `set_user_active`, `verify_amenity_usage`, `verify_payment`.
- Features in the app: guest sign-up with locked member pages, admin-created accounts, password change and owner reset, packages, amenities, discount codes, payments with proof, coach hires, coach and member messaging, coach shifts, trainees, time requests, sessions with reschedule and cancel, attendance, gym check-ins, amenity usage, notifications, revenue, audit log.

## 3. Schema

The full DDL is in [`docs/dfd/GYM_TABLES.sql`](../dfd/GYM_TABLES.sql). Summary:

### 3.1 Status types

| Type | Values |
|---|---|
| `membership_status` | pending, active, expired, cancelled (also used by `user_amenities`) |
| `renewal_status` | pending, completed, cancelled |
| `payment_status` | pending, verified, rejected, expired, voided |
| `payment_method` | qr, cash, gcash, bank_transfer |
| `payment_kind` | membership, renewal, amenity, walk_in |
| `coach_hire_status` | active, ended, cancelled |
| `time_request_status` | pending, approved, rejected, cancelled |
| `requested_by_role` | member, coach |
| `session_status` | scheduled, completed, cancelled, no_show |
| `attendance_status` | present, late, absent, excused |
| `shift_status` | scheduled, cancelled |
| `amenity_usage_status` | pending, verified, rejected |
| `notification_type` | time_request, membership, payment, attendance, message, system |

### 3.2 Tables (26)

| Group | Table | Purpose and key rules |
|---|---|---|
| Identity | `profiles` | One per Auth user: username (unique), full_name, phone, is_active, must_change_password |
| | `roles` | admin, employee, coach, member |
| | `permissions` | (resource, action) pairs, unique |
| | `role_permissions` | Role ↔ permission, composite key |
| | `user_roles` | User ↔ role, composite key, assigned_by |
| | `user_settings` | One per user: language, timezone (`Asia/Manila`), theme |
| | `gym_settings` | Single row (`id = 1`): gym_name, qr_image_path, qr_account_name, walk_in_price, payment_expiry_hours (48), receipt_prefix (`OR`) |
| People | `members` | Key `user_id`, goals |
| | `coaches` | Key `user_id`, bio, specialty, hourly_rate, is_available |
| Catalog | `membership_packages` | name (unique), price ≥ 0, duration_days > 0, is_active |
| | `amenities` | name (unique), price ≥ 0, duration_days (null = no expiry), is_active |
| | `discounts` | code (unique, case-insensitive), percent 1–100, starts_at < expires_at, is_active |
| Entitlements | `user_membership_packages` | A member's membership. Pending until paid; dates set on activation. One active per member |
| | `membership_renewals` | previous_ends_at, new_starts_at, new_ends_at, status. Paid by the payment that points to it |
| | `user_amenities` | A member's amenity access. Pending until paid; expires_at from duration_days |
| Payments | `payments` | See [section 8](#8-payments-in-detail) |
| Coaching | `coach_hires` | member ↔ coach, one active per pair, member ≠ coach |
| | `training_sessions` | coach, member, start < end, no overlapping scheduled sessions per coach or per member |
| | `time_requests` | Booking, reschedule (`training_session_id`) or open slot (no member) |
| | `session_attendance` | One per session (key `training_session_id`) |
| | `shift_posts` | Coach floor hours, no overlapping scheduled shifts per coach |
| | `messages` | coach ↔ member conversation; sender must be one of them |
| Operations | `gym_check_ins` | Exactly one of member or walk-in payment |
| | `amenity_usages` | Visit with optional proof, verified by staff |
| | `user_notifications` | Unread when `read_at` is null; `reference` is jsonb |
| | `audit_log` | Append-only, written by functions only |

### 3.3 Delete behaviour

| When this is deleted | What happens |
|---|---|
| An Auth user | Its profile, roles, settings and notifications are deleted. Blocked if the profile has a member or coach row (deactivate instead) |
| A role or permission | Its links in `role_permissions` are deleted; deleting a role still assigned to a user is blocked |
| A training session | Its attendance row is deleted; time requests that pointed to it keep `training_session_id = null` |
| Anything else | Blocked by foreign keys. Use `is_active`, status changes or voids |

## 4. Accounts and sign-in

### 4.1 Supabase Auth settings

- Email + password provider on. Email sign-ups **enabled** (members sign up).
- Email confirmation: see [section 15](#15-defaults-and-open-choices).
- Minimum password length 6, to match the app's validation.

### 4.2 Sign-up trigger: `handle_new_user()`

`after insert on auth.users`, `security definer`, `set search_path = ''`:

1. Insert `profiles` (id = new.id, username and full_name from `raw_user_meta_data`, `must_change_password` from metadata, default false for self sign-up).
2. Insert `user_settings` with defaults.
3. Read `raw_app_meta_data ->> 'role'` (set only by the Edge Function; members can't set app metadata). Default `member`.
4. Insert into `user_roles`; insert into `members` or `coaches` to match. Employees get no member or coach row.

The app's `SignUpAsync(email, password, username)` passes `username` in `options.data`.

### 4.3 Edge Function: `create-user`

- Caller must be signed in with the `admin` role (check through the caller's JWT and `user_roles`).
- Input: name, email, role (`member` | `coach` | `employee`), temporary password.
- Calls `auth.admin.createUser` with `email_confirm: true`, `user_metadata: { username, full_name, must_change_password: true }` and `app_metadata: { role }`. The trigger does the rest.
- Writes `audit_log` (`create_user`).
- Replaces the `create_user` RPC.

### 4.4 Edge Function: `reset-password`

- Caller must be admin. Input: user id, new temporary password.
- Calls `auth.admin.updateUserById`, sets `profiles.must_change_password = true`, writes `audit_log`.
- Replaces the `reset_password` RPC.

### 4.5 Changing a password

- The app calls `supabase.auth.updateUser({ password })`, then the RPC `mark_password_changed()`, which sets `must_change_password = false` for the caller.
- Replaces the `change_password` RPC.
- If `must_change_password` is true after sign-in, the app sends the user to Settings before anything else.

### 4.6 Deactivating

`set_user_active(p_user uuid, p_active boolean)`: admin only, can't deactivate yourself, writes `audit_log`. A deactivated user's requests are refused by `is_active_user()` in every policy and function (Auth itself still lets them sign in, so the app signs them out when `is_active` is false).

## 5. Roles and permissions

### 5.1 Helper functions

All `stable`, `security definer`, `set search_path = ''`:

| Function | Returns |
|---|---|
| `has_role(p_role text)` | Caller has that role |
| `has_permission(p_permission text)` | Caller has `resource:action` through `user_roles` → `role_permissions` → `permissions` |
| `is_staff()` | Caller is admin or employee |
| `is_active_user()` | Caller's profile is active |
| `is_active_member(p_user uuid default auth.uid())` | User has an active, unexpired membership (the "not a guest" check) |
| `coaches_member(p_coach uuid, p_member uuid)` | There is an active `coach_hires` row for the pair |

### 5.2 Seed permissions

| Permission | admin | employee | coach | member |
|---|:-:|:-:|:-:|:-:|
| `payments:verify`, `payments:record` | ✓ | ✓ | | |
| `payments:void` | ✓ | | | |
| `check_ins:record`, `amenity_usages:verify` | ✓ | ✓ | | |
| `users:manage`, `roles:manage` | ✓ | | | |
| `catalog:manage`, `discounts:manage`, `gym_settings:manage` | ✓ | | | |
| `reports:revenue`, `audit:read` | ✓ | | | |
| `reports:cash` | ✓ | ✓ | | |
| `sessions:manage`, `shifts:manage`, `attendance:record` | | | ✓ | |

## 6. Row level security

Turn on RLS for every table. Every policy also requires `is_active_user()`. "Own" means the row's member/coach/user column equals `auth.uid()`. "Active member" means `is_active_member()`.

| Table | Select | Insert | Update | Delete |
|---|---|---|---|---|
| `profiles` | Own; staff all; coaches their trainees; members their hired coaches and all coaches' names | Trigger only | Own (full_name, phone only); admin | No |
| `roles`, `permissions`, `role_permissions` | Everyone signed in | No (seed) | No | No |
| `user_roles` | Own; admin all | Functions only | No | Functions only |
| `user_settings` | Own | Trigger only | Own | No |
| `gym_settings` | Everyone signed in | No (seed) | Admin | No |
| `members` | Own; staff; their coaches | Trigger only | Own (goals) | No |
| `coaches` | Everyone signed in | Trigger only | Own (bio, specialty, is_available); admin | No |
| `membership_packages`, `amenities` | Active rows for everyone; admin all | Admin | Admin | Admin, only if never used |
| `discounts` | Admin (members only use codes through functions) | Admin | Admin | Admin, only if never used |
| `user_membership_packages`, `membership_renewals`, `user_amenities` | Own; staff | Functions only | Functions only | No |
| `payments` | Own; staff | Functions only | Functions only | No |
| `coach_hires` | Own (member or coach); staff | Active member, through `hire_coach` | Functions only | No |
| `time_requests` | Own (member or coach); open slots visible to the coach's trainees | Functions only | Functions only | No |
| `training_sessions` | Own (member or coach); staff | Functions only | Functions only | No |
| `session_attendance` | Session's member and coach; staff | Functions only | Functions only | No |
| `shift_posts` | Coach own; trainees of that coach; staff | Coach own | Coach own | Coach own (or set cancelled) |
| `messages` | Own conversation | Sender is the caller, `coaches_member(coach_id, member_id)`, and the member is an active member | Recipient may set `read_at` only | No |
| `gym_check_ins` | Own; staff | Functions only | No | No |
| `amenity_usages` | Own; staff | Own, active amenity | Functions only | No |
| `user_notifications` | Own | Functions only | Own (`read_at` only) | Own |
| `audit_log` | Admin | Functions only | No | No |

Guest lock: functions and policies for amenities, messages, sessions and time requests require `is_active_member()` for members, matching the app's locked pages.

Before finishing, check every `InsertAsync`, `UpdateAsync` and `DeleteAsync` call in `Services.cs` and `Features.cs`. Each must either match a policy above or be moved into a function.

## 7. Server-side functions

All functions: `security definer`, `set search_path = ''`, check `is_active_user()` and the caller's role or permission first, write `audit_log` for sensitive actions, and send notifications where listed.

**Errors:** raise with these SQLSTATE codes so the gateway can map them to `AppErrorKind`:

| Code | Meaning | `AppErrorKind` |
|---|---|---|
| `42501` | Not allowed | Unauthorized |
| `23505`, `23P01`, `P0002` (custom: state conflict) | Duplicate, overlap, wrong state | Conflict |
| `22023` | Bad input | Validation |

### 7.1 Memberships, amenities, payments

| Function | Caller | Behaviour |
|---|---|---|
| `avail_membership(p_package_id bigint, p_discount_code text default null)` | Member | Package must be active. Refuse if the member already has an active or pending membership (renew instead). Apply discount (see 8.3). Create pending `user_membership_packages` + pending QR `payments` (kind membership). Returns the payment |
| `avail_amenity(p_amenity_id bigint, p_discount_code text default null)` | Active member | Same pattern with `user_amenities` (kind amenity) |
| `renew_membership(p_ump_id bigint, p_discount_code text default null)` | Member (own) | Membership must be active or expired. `previous_ends_at = ends_at`, `new_starts_at = greatest(ends_at, now())`, `new_ends_at = new_starts_at + duration_days`. Refuse if a pending renewal exists. Create pending renewal + pending QR payment (kind renewal) |
| `submit_payment_proof(p_payment_id bigint, p_reference_no text, p_proof_path text)` | Member (own) | Payment must be pending QR and not expired. Save reference number and proof path. Notify staff (type payment) |
| `verify_payment(p_payment_id bigint, p_approve boolean, p_reason text default null)` | `payments:verify` | Payment must be pending; caller is not the payer. Approve: see 8.4. Reject: reason required, status rejected, cancel the pending membership/renewal/amenity. Notify the member; audit |
| `record_counter_payment(p_kind payment_kind, p_method payment_method, p_member_id uuid default null, p_item_id bigint default null, p_walk_in_name text default null, p_reference_no text default null, p_discount_code text default null)` | `payments:record` | `p_method` can't be qr. For membership/amenity: `p_item_id` is the package/amenity id; create the entitlement. For renewal: `p_item_id` is the membership id. For walk_in: amount = `gym_settings.walk_in_price`, also insert `gym_check_ins`. Payment is verified immediately (8.4). Caller can't record a payment for themselves. Audit |
| `void_payment(p_payment_id bigint, p_reason text)` | `payments:void` | Payment must be verified. Status voided with voided_by/at/reason. Reverse: membership → cancelled; renewal → membership `ends_at = previous_ends_at` (expired if in the past), renewal cancelled; amenity → cancelled. Notify the member; audit |
| `revenue_summary(p_from date, p_to date)` | `reports:revenue` | Verified payments per day (Manila time), split by kind and method. Excludes voided |
| `daily_cash_summary(p_day date)` | `reports:cash` | Expected cash for the day: verified cash payments minus voids, by kind |

### 7.2 Coaching

| Function | Caller | Behaviour |
|---|---|---|
| `hire_coach(p_coach_id uuid)` | Active member | Coach must be available. Create active hire. Notify coach |
| `end_hire(p_hire_id bigint)` | Member or coach on the hire | Status ended, `ended_at = now()`. Cancel future scheduled sessions between them |
| `request_time(p_coach_id uuid, p_start timestamptz, p_end timestamptz, p_message text default null)` | Active member with an active hire, or the coach (open slot, no member) | Create pending time request. Notify the other side |
| `request_reschedule(p_session_id bigint, p_start timestamptz, p_end timestamptz, p_message text default null)` | Session's member or coach | Session must be scheduled and in the future. Create pending time request with `training_session_id`. Notify the other side |
| `respond_time_request(p_request_id bigint, p_approve boolean)` | The side that didn't raise it (a member claiming an open slot counts as approving) | Approve: create a training session, or move the linked session. Overlap errors come from the exclusion constraints (23P01 → Conflict). Notify; set `responded_at` |
| `cancel_session(p_session_id bigint, p_reason text default null)` | Session's member or coach | Scheduled sessions only. Status cancelled. Notify the other side |
| `record_session_attendance(p_session_id bigint, p_status attendance_status, p_notes text default null)` | The session's coach | Upsert `session_attendance`. Session status: absent → no_show, otherwise completed. Notify member |

### 7.3 Front desk and accounts

| Function | Caller | Behaviour |
|---|---|---|
| `record_check_in(p_member_id uuid)` | `check_ins:record` | Member must be an active member. Insert `gym_check_ins` |
| `verify_amenity_usage(p_usage_id bigint, p_approve boolean)` | `amenity_usages:verify` | Pending only. Set status, verified_by/at. Notify member |
| `assign_role(p_user uuid, p_role text)` / `revoke_role(p_user uuid, p_role text)` | `roles:manage` | Can't remove your own admin role. Assigning coach creates the `coaches` row if missing. Audit |
| `set_user_active(p_user uuid, p_active boolean)` | `users:manage` | See 4.6 |
| `mark_password_changed()` | Anyone signed in | See 4.5 |

### 7.4 Internal helpers

| Function | Purpose |
|---|---|
| `generate_payment_code()` | `GYM-` + 5 characters from `ABCDEFGHJKMNPQRSTUVWXYZ23456789`; retry on unique conflict |
| `next_receipt_no()` | `concat(receipt_prefix, '-', year, '-', lpad(nextval('receipt_no_seq')::text, 6, '0'))`, e.g. `OR-2026-000123`. Create `receipt_no_seq` in the functions migration |
| `apply_discount(p_member uuid, p_code text, p_price numeric)` | Returns discount id, discount amount and final amount (8.3) |
| `activate_payment_target(p_payment_id bigint)` | Shared by `verify_payment` and `record_counter_payment` (8.4) |
| `notify(p_user uuid, p_type notification_type, p_title text, p_body text, p_reference jsonb)` | Insert into `user_notifications` |
| `audit(p_action text, p_entity text, p_entity_id text, p_details jsonb)` | Insert into `audit_log` with `actor_id = auth.uid()` |

## 8. Payments in detail

### 8.1 Columns

`payments`: payment_id, payment_code (unique), kind, method, status, amount, discount_id, discount_amount, member_id (null for walk-ins), walk_in_name, user_membership_package_id / renewal_id / user_amenity_id (one of them, or none for walk-ins), reference_no, proof_path, expires_at, recorded_by, verified_by, verified_at, receipt_no (unique), rejected_reason, voided_by, voided_at, void_reason, created_at.

### 8.2 Checks in the table

- The linked item matches `kind`; walk-ins link nothing.
- Walk-ins have `walk_in_name` and no member; everything else has a member and no walk-in name.
- Walk-ins can't be QR. QR payments have `expires_at`. Non-QR payments have `recorded_by`.
- `verified_by <> member_id`.
- Verified or voided payments have verified_by, verified_at and receipt_no. Rejected payments have a reason. Voided payments have voided_by, voided_at and void_reason.
- A reference number is unique among pending and verified payments.
- A discount is used once per member (among pending and verified payments).

### 8.3 Discounts

- Code matched case-insensitively; must be active and `starts_at <= now() < expires_at`; not already used by this member.
- `discount_amount = round(price * percent / 100, 2)`, `amount = price - discount_amount`.
- Invalid code → Validation error with a clear message.

### 8.4 Activating on verification

1. Set status verified, verified_by (the caller), verified_at, receipt_no = `next_receipt_no()`.
2. Membership: `starts_at = now()`, `ends_at = now() + duration_days`, status active.
3. Renewal: membership `ends_at = new_ends_at`, status active; renewal completed, `completed_at = now()`.
4. Amenity: status active, `expires_at = now() + duration_days` (null if no duration).
5. Notify the member (type payment) and write `audit_log`.

### 8.5 QR flow (member)

1. `avail_membership` / `avail_amenity` / `renew_membership` creates a pending payment with `payment_code` and `expires_at = now() + payment_expiry_hours`.
2. The app shows the QR image from `gym_settings.qr_image_path` (bucket `gym-assets`), `qr_account_name`, the amount in ₱ and the payment code, with the instruction to type the code into the e-wallet message.
3. The member pays, uploads a screenshot to `payment-proofs/{uid}/{payment_id}-{file}` and calls `submit_payment_proof`.
4. Staff see pending payments with screenshot, amount, code and reference number on Verify Payments, check the gym's e-wallet and call `verify_payment`.
5. Rejected is final; the app offers **Pay again**, which calls the avail/renew function again.
6. The nightly job expires unpaid QR payments and cancels their pending membership/renewal/amenity.

### 8.6 Counter flow (front desk)

- Cash or GCash at the counter: `record_counter_payment` with method cash or gcash (reference number for GCash). Verified straight away with a receipt number.
- Walk-in: `record_counter_payment` with kind walk_in and a name. Creates the check-in.
- Mistakes: admin calls `void_payment` with a reason, then the payment is recorded again.

## 9. Storage

| Bucket | Access | Path |
|---|---|---|
| `payment-proofs` | Private. Member uploads and reads `{own uid}/...`; staff read all | `{uid}/{payment_id}-{file}` |
| `amenity-proofs` | Private. Same rules | `{uid}/{amenity_usage_id}-{file}` |
| `gym-assets` | Private. Anyone signed in reads; admin writes | `qr.png` and similar |

Images only (`image/png`, `image/jpeg`, `image/webp`), maximum 5 MB.

## 10. Scheduled jobs

`pg_cron`, in UTC (Manila is UTC+8):

| Job | Schedule | Does |
|---|---|---|
| `expire_payments()` | Every 15 minutes | Pending QR payments past `expires_at` → expired; cancel their pending membership/renewal/amenity; notify member |
| `expire_memberships()` | Daily 16:05 UTC (00:05 Manila) | Active memberships past `ends_at` → expired; amenities past `expires_at` → expired. Members become guests |
| `send_expiry_reminders()` | Daily 00:00 UTC (08:00 Manila) | Notify members whose membership ends in 3 days, once per membership |

## 11. Seed data

- Roles: admin, employee, coach, member. Permissions and role links from [5.2](#52-seed-permissions).
- `gym_settings` row: gym name, walk-in price, 48-hour expiry, `OR` prefix.
- Demo packages and amenities with PHP prices, matching `DemoGateway`.
- Demo users matching the app's demo logins (`member@demo.io`, `coach@demo.io`, `employee@demo.io`, `admin@demo.io`, password `demo1234`). Create them with a seed script that uses the Auth admin API (or `supabase/seed.sql` inserting into `auth.users` for local development only), so the trigger sets up their rows.

## 12. App changes

### 12.1 Models (`Models.cs`)

| Model | Change |
|---|---|
| `Profile` | Add `FullName`, `Phone`, `MustChangePassword` |
| `UserRole` | Composite key (`user_id`, `role_id`); drop `UserRoleId` |
| `UserSettings` | Key is `user_id`; add `Timezone` |
| `MembershipPackage` | Drop `Currency` |
| `UserMembershipPackage` | `UserId` → `MemberId` (`member_id`) |
| `Payment` | Drop `Qr`, `Currency`, `DiscountCode`. Add `PaymentCode`, `Kind`, `Method`, `DiscountId`, `DiscountAmount`, `MemberId` (rename of `UserId`), `WalkInName`, `RenewalId`, `UserAmenityId`, `ReferenceNo`, `ExpiresAt`, `RecordedBy`, `VerifiedBy`, `ReceiptNo`, `RejectedReason`, `VoidedBy`, `VoidedAt`, `VoidReason` |
| `Amenity` | Add `Price`, `DurationDays` |
| `UserAmenity` | `UserId` → `MemberId`; add `Status`, `ExpiresAt` |
| `AmenityUsage` | `Proof` → `ProofPath` |
| `Coach`, `Member` | Key is `user_id` (string uuid); drop the int `Id` |
| `CoachHire`, `TimeRequest`, `TrainingSession`, `ShiftPost`, `ChatMessage` | `CoachId` and `MemberId` become string uuids |
| `UserNotification` | Drop `IsRead`; add `ReadAt` and a computed `IsRead => ReadAt != null` |
| `AuditEntry` | Add `Details` |
| `Discount` | Add `StartsAt` |
| New | `GymSettings`, `GymCheckIn`, `MembershipRenewal`, `SessionAttendance` |
| `RevenueRow` | Drop `Cur`; add kind and method |

### 12.2 Gateway and services

- Add `SupabaseGateway : ISupabaseGateway` using the `Supabase` C# client. Read the project URL and anon key from configuration, never the service-role key.
- Add an Edge Function call to the gateway (e.g. `InvokeFunctionAsync(name, body)`) for `create-user` and `reset-password`.
- `SignUpAsync` passes `username` as user metadata.
- Map Postgres errors to `AppErrorKind` using the codes in section 7.
- Swap the one line in `MauiProgram.cs` to `SupabaseGateway` (keep `DemoGateway` for tests and offline demos).
- Update `DemoGateway` to follow the same rules (uuid IDs, PHP, payment code, counter payments, voids, guest lock), so tests describe the real behaviour.

### 12.3 Pages

- My membership / Packages: show QR, amount in ₱, payment code, reference number field, screenshot upload, **Pay again** after rejection.
- Verify Payments: show reference number and screenshot; reject needs a reason.
- New: Counter payment and Walk-in (front desk), Gym settings with QR upload (admin), Void payment (admin, from payment history).
- Revenue: split by kind and method; daily cash summary for the front desk.
- Force password change when `must_change_password` is true.

## 13. Order of work

1. `supabase init`; migrations in `supabase/migrations/`:
   1. `0001_extensions_and_types.sql`: `btree_gist`, `pg_cron`, all enums
   2. `0002_tables.sql`: tables, constraints, indexes and the `updated_at` trigger from `GYM_TABLES.sql`
   3. `0003_helpers.sql`: section 5.1 and 7.4 helpers, `receipt_no_seq`
   4. `0004_auth_trigger.sql`: `handle_new_user()`
   5. `0005_functions.sql`: section 7.1–7.3
   6. `0006_rls.sql`: section 6
   7. `0007_storage.sql`: section 9
   8. `0008_cron.sql`: section 10
   9. `supabase/seed.sql` and the seed script: section 11
2. Edge Functions in `supabase/functions/`: `create-user`, `reset-password`.
3. pgTAP tests in `supabase/tests/` (section 14); run `supabase db reset` then `supabase test db`.
4. App: models, `DemoGateway`, services, `SupabaseGateway`, pages (section 12). Run `dotnet test`.
5. Docs: update `PRD.md`, `technical-spec.md`, `gym-membership-db-diagram.md` and `GYM_TABLES.sql` to match what was built; tick the README roadmap.
6. Commit in steps (database, edge functions, app, tests, docs).

## 14. Tests

### 14.1 pgTAP (`supabase/tests/`)

- Every RLS policy, allowed and denied, for each role and for a guest.
- Sign-up trigger creates profile, settings, member row and member role; `create-user` roles land correctly.
- `avail_membership`: refuses with an active or pending membership; applies discounts; refuses expired, inactive, reused and unknown codes.
- `verify_payment`: double verify (Conflict), self verify (Unauthorized), reject without reason (Validation), activation dates correct.
- `renew_membership`: early renewal extends from `ends_at`; late renewal from now; void restores `previous_ends_at`.
- Reference number reused (Conflict).
- `record_counter_payment`: cash verified immediately with receipt; walk-in creates a check-in; QR method refused.
- `void_payment`: admin only; reverses each kind.
- Overlapping sessions and shifts refused (Conflict).
- Messages refused without an active hire or for a guest.
- `expire_payments` and `expire_memberships` change the right rows only.
- `audit_log` can't be updated or deleted by anyone.

### 14.2 xUnit (`tests/GymMembership.Tests`)

QR flow end to end, counter cash, walk-in, void, discount rules, guest lock, forced password change, against `DemoGateway`.

## 15. Defaults and open choices

| Item | Default | Change if |
|---|---|---|
| Unpaid QR payment expiry | 48 hours (`gym_settings.payment_expiry_hours`) | |
| Rejected payment | Final; member pays again | |
| Receipt number | `OR-2026-000123` | |
| Discount use | Once per member | |
| Pending memberships | One at a time per member | |
| Walk-in price | `gym_settings.walk_in_price` | |
| Email confirmation on sign-up | **Open choice.** On is safer; off is simpler for a front-desk sign-up | Decide before going live |

## 16. Definition of done

- `supabase db reset` runs all migrations and the seed without errors.
- `supabase test db` and `dotnet test` pass.
- Every PRD flow works end to end for each role against a local Supabase: sign up as guest, buy by QR, verify, renew, counter cash, walk-in, void, hire coach, message, request and reschedule a session, attendance, check-in, amenity usage, notifications, revenue, audit log.
- No table has RLS turned off; the service-role key exists only in Edge Function secrets.
- Front desk can record a counter payment or verify a QR payment in under 30 seconds.

## 17. Out of scope

Online payment gateways or payments APIs, push notifications, multiple branches, offline mode, and Phase 2 (products and sales, employee working hours, sales reports).
