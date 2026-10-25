# Gym Membership System — Technical Spec

Status: Built · Original date: 2026-10-04 · Updated: 2026-10-25
Companion: [PRD.md](PRD.md) · Schema source: the migrations in `supabase/migrations/` (summary in [database-plan.md](database-plan.md#current-state-2026-10-25))

## As built (2026-10-25)

Sections 1 to 10 are the original design. Where they differ, this wins:

| Topic | As built |
|---|---|
| Packages | MAUI 10, `CommunityToolkit.Maui` 15.0.1, `CommunityToolkit.Mvvm` 8.4.2, `Supabase` 8.3.1, `PDFsharp` 6.2.4 (revenue PDF), `ZXing.Net.Maui.Controls` 0.10.4 (member QR show and scan) |
| Targets | Android and Windows are built and released; iOS is in the project file but not built or tested |
| Layers | Views → ViewModels → Services → `ISupabaseGateway`, as planned. `SupabaseGateway` is the real backend; `DemoGateway` is an in-memory stand-in with the same rules, used when `Resources/Raw/supabase.json` is absent |
| Shells | `MemberShell`, `CoachShell` and `StaffShell` (front desk, sub-admin, super admin; pages hidden by role). Phones use a bottom navigation bar |
| Roles | member, coach, employee, subadmin, admin (shown as "Super admin"); see the database plan |
| Tests | xUnit against `DemoGateway` (725 tests), and the database rules on a throwaway local Postgres in `supabase/tests/local` (Node + `embedded-postgres`, not pgTAP; 309 checks) |
| Realtime | Chat uses Realtime change signals with a safety refresh; the bell and amenity pages poll |
| Email | Supabase Auth with Brevo SMTP; 6-digit codes for email confirmation and password reset |
| Edge Functions | `create-user` and `reset-password` (super admin; sub-admin for members and coaches only) |

## 1. Stack (versions confirmed via Context7, 2026-10-04)

| Layer | Choice |
|---|---|
| Client | .NET MAUI **10.0.90** (pinned via `<MauiVersion>`; required by CommunityToolkit.Maui 15.0.1), C# + XAML, single project |
| Target frameworks | `net10.0-android`, `net10.0-ios`, `net10.0-windows10.0.19041.0` (Mac Catalyst optional) |
| MVVM | `CommunityToolkit.Mvvm`; UI helpers via `CommunityToolkit.Maui` |
| Backend client | `Supabase` NuGet 8.2.0 (repo: supabase-csharp) — Auth, PostgREST, Realtime, Storage |
| Backend | Supabase: PostgreSQL, Auth, Storage, Realtime, `pg_cron` |
| Tests | xUnit (client), pgTAP (SQL/RLS) |

Pin exact package versions in the first implementation task; re-check at that time.

## 2. Architecture

```
MAUI app (Views → ViewModels → Services) ──supabase-csharp──▶ Supabase
  Android/iOS: Member + Coach shells                          Auth · Postgres+RLS · RPC
  Windows: Admin + Employee shells                            Storage · Realtime · pg_cron
```

- **Views** (XAML) bind to **ViewModels** only; ViewModels call **Services** (`IAuthService`, `IMembershipService`, `IPaymentService`, `ICoachService`, `ISessionService`, `INotificationService`, …). Services are the only layer touching `Supabase.Client`.
- **Shell** exposes a different section set per role, chosen after login from the user's roles. This is UX only; RLS enforces access.
- Platform split is by role/form factor using separate Shell flyouts and `OnIdiom`/`OnPlatform` templates, not separate projects.
- Session is stored via `SecureStorage`; the Supabase client refreshes tokens automatically.

## 3. Schema changes to the existing diagram

| Change | Reason |
|---|---|
| `users` → `profiles(id uuid PK = auth.users.id, username, is_active, created_at, updated_at)` | Supabase Auth owns credentials |
| Drop `user_session`, `password_hash`, `email_verified_at`, `last_login_at` | Provided by Supabase Auth |
| All `user_id INT` FKs → `uuid` referencing `profiles(id)` | Follows the change above |
| Keep `roles`, `permissions`, `user_roles`, `role_permissions` | Drive RLS via `has_permission()` |
| Replace free-text `status` VARCHARs with enums (`coach_hire_status`, `session_status`, `amenity_usage_status`, `requested_by_role`) | Integrity; `payment_status`, `membership_status`, `time_request_status`, `attendance_status` already enums |
| `TIMESTAMP` → `timestamptz` | Correct across timezones (`user_settings.timezone` exists) |
| Add `audit_log(id, actor_id, action, entity, entity_id, details jsonb, created_at)` | PRD F11 |
| Add `check_ins(id, member_id, checked_in_at, recorded_by)` | Gym attendance (DFD "Record attendance") |
| Add exclusion constraint on `training_sessions (coach_id, tstzrange(scheduled_start, scheduled_end))` for non-cancelled rows; `CHECK (scheduled_end > scheduled_start)` | No double-booking |
| `payments`: add `proof_path text null` (uploaded proof of payment) | PRD F3 proof upload |
| `payments`: add `CHECK` that exactly one of `user_membership_package_id` / `user_amenity_id` is set | Payment settles one thing |
| Fix FK names (`fk_*_client_id_clients` → `members`) and the duplicated `payments → users` relationship | Diagram hygiene |
| Add indexes on all FK columns and on `(status)` for payments/memberships | Query performance |

`membership_renewals.status` currently reuses `membership_status` but documents `pending|completed|cancelled`; introduce `renewal_status`.

**Phase 2 tables (not built in Phase 1):** `products`, `product_stock`, `sales`, `sale_items`, `employees`, `work_shifts` / `time_logs`.

## 4. Row Level Security

- RLS enabled on every table; no table is readable without a policy.
- `has_permission(text)` — `SECURITY DEFINER` function resolving the caller's permissions from `user_roles → role_permissions → permissions`.
- Policy summary:

| Table group | Member | Coach | Employee | Admin |
|---|---|---|---|---|
| profiles, user_settings | own row | own row | own row | all |
| membership_packages, amenities | read active | read active | read | write |
| user_membership_packages, user_amenities, membership_renewals | own | — | read | all |
| payments | own (insert/read) | — | verify/reject | all |
| coaches | read all available | own | read | write |
| coach_hires, training_sessions, time_requests | own | rows for own coach_id | read | all |
| session_attendance | read own | write for own sessions | read | all |
| check_ins | read own | — | insert | all |
| user_notifications | own | own | own | own |
| audit_log | — | — | — | read; inserts only via RPC/trigger |

- Verification of a payment is only possible through `verify_payment()` which rejects `verified_by = payments.user_id`.
- The service-role key is never shipped in the client.

## 5. RPC functions (atomic server-side flows)

| Function | Effect |
|---|---|
| `avail_membership(package_id)` | Insert pending `user_membership_packages` + pending `payments` (QR token generated) |
| `verify_payment(payment_id, approve bool)` | Permission check; set status/verifier; if approved activate membership (dates from `duration_days`) or complete renewal; notify; write `audit_log` |
| `renew_membership(user_membership_package_id)` | Insert pending `membership_renewals` + payment |
| `respond_time_request(id, approve bool)` | Counterpart-only; on approve create `training_sessions` row (overlap constraint enforced); notify |
| `record_session_attendance(...)`, `record_check_in(member_id)` | Role-checked inserts |
| `expire_memberships()` | Scheduled by `pg_cron` daily: mark expired, send 7-day/1-day reminders |

All write RPCs run in a single transaction and are `SECURITY DEFINER` with explicit permission checks.

## 6. Storage and Realtime

- Storage buckets: `payment-proofs`, `amenity-proofs` (private; path prefixed by owner id; policies mirror table RLS). Rows store the object path in a new `payments.proof_path` column and in `amenity_usages.proof`.
- Realtime: client subscribes to `user_notifications` filtered by own `user_id` to update the unread badge.

## 7. Error handling (client)

Services translate exceptions into a small result set: `Offline`, `Unauthorized`, `Conflict` (e.g. overlap, already verified), `Validation`, `Unknown`. A shared `IDialogService` shows them; ViewModels do not catch Supabase exceptions directly. Network calls use a timeout and a single retry for idempotent reads.

## 8. Testing

- **pgTAP:** each RLS policy (allowed and denied cases per role), each RPC (happy path, wrong role, double-verify, overlap).
- **xUnit:** ViewModels and services against a fake `ISupabaseGateway` wrapper.
- **Manual UAT:** one script per role covering the PRD flows.
- RLS/RPC tests are written before the client code that depends on them (TDD).

## 9. Project layout (proposed)

```
gym-membership/
  docs/                     PRD, specs, DFD, DB diagram
  supabase/
    migrations/             SQL schema, RLS, RPC, cron
    tests/                  pgTAP
  src/GymMembership.App/    MAUI project (Views, ViewModels, Services, Models)
  tests/GymMembership.Tests/
```

## 10. Risks and open items

- RLS mistakes are the main security risk → mandatory pgTAP coverage.
- `supabase-csharp` is community-maintained; wrap it behind `ISupabaseGateway` so it can be swapped.
- Windows front-desk UX (grids, keyboard flow) may need more design effort than mobile.
- Open questions are listed in the PRD §8.
