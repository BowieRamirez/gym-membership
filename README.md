# Gym Membership

One app for the whole gym floor. Members manage their plan, coaches run their sessions, and the front desk handles payments, all from a single .NET MAUI codebase.

> **Status:** the interface is complete and runs on an in-memory demo backend. Supabase (PostgreSQL) is the planned real backend and is not connected yet. See the [database plan](#database-plan-supabase).

## Contents

- [What it does](#what-it-does)
- [Accounts and access](#accounts-and-access)
- [Getting started](#getting-started)
- [Tech stack](#tech-stack)
- [Project structure](#project-structure)
- [Database plan (Supabase)](#database-plan-supabase)
- [Roadmap](#roadmap)
- [Docs](#docs)

## What it does

| Role | Can do |
|---|---|
| **Member** | See days left on a membership, buy or renew a package (with a discount code), attach proof of payment, use amenities, hire a coach, message that coach, see their posted shifts, request, reschedule or cancel training sessions |
| **Coach** | Manage trainees, message them, post shifts, offer open slots, approve requests, schedule, reschedule or cancel sessions, mark attendance, edit a public profile |
| **Front desk** | Verify or reject payments, check members in, confirm amenity visits |
| **Admin** | Everything above, plus adding members, coaches and front-desk staff, resetting passwords, packages (add, edit, delete) and amenities, discount codes, revenue, and an audit log |

**Design:** colour is only ever used for status: yellow for pending, green for verified, red for rejected, blue for scheduled. The dashed lane on the membership screen is the one recurring motif, borrowed from court floor markings.

## Accounts and access

- **Members sign up themselves** from the sign-in screen. A new account starts as a **guest**.
- **Guests** can open Packages, My membership, Coaches, Alerts and Settings. Amenities, Messages, Sessions and Time requests show a "get a membership" notice.
- **A guest becomes a full member** once a package payment is verified by the owner or front desk, and goes back to guest when the plan expires.
- **The owner adds coaches and front-desk staff** (and can add members) on the Users page with a temporary password.
- **Passwords:** anyone can change theirs under Settings, and the owner can reset a forgotten one.

## Getting started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- The MAUI workload: `dotnet workload install maui`

### Run the app

```bash
git clone https://github.com/BowieRamirez/gym-membership.git
cd gym-membership
dotnet run --project src/GymMembership.App -f net10.0-windows10.0.19041.0
```

Or open `gym-membership.slnx` in Visual Studio and run **GymMembership.App** on *Windows Machine*. For Android, pick an emulator as the target instead.

### Demo logins

The login screen has one-click buttons for each role. To type them in, the password is `demo1234`.

| Role | Email |
|---|---|
| Member | `member@demo.io` |
| Coach | `coach@demo.io` |
| Front desk | `employee@demo.io` |
| Admin | `admin@demo.io` |

All data is seeded in memory and resets every time the app starts.

### Use a real Supabase project

The app runs on the demo backend until it finds your project details.

1. Create the project, then from this folder run `npx supabase login`, `npx supabase link --project-ref <ref>` and `npx supabase db push --include-seed`. Deploy the two functions with `npx supabase functions deploy create-user` and `npx supabase functions deploy reset-password`.
2. Copy `src/GymMembership.App/Resources/Raw/supabase.sample.json` to `supabase.json` in the same folder and fill in the project URL and the **anon** key (Project settings, API). That file is git-ignored. Never put the service-role key in the app.
3. Run the app again. The sign-in screen no longer shows the demo buttons. To get demo logins on a development project, run `supabase/scripts/seed-demo-users.mjs` (see the comment at the top for the variables it needs).

Delete `supabase.json` to go back to the demo.

### Run the tests

```bash
dotnet test tests/GymMembership.Tests
```

## Tech stack

| Layer | Choice |
|---|---|
| App | .NET 10, .NET MAUI 10, C#, XAML |
| Pattern | MVVM with `CommunityToolkit.Mvvm` and `CommunityToolkit.Maui` |
| Backend (now) | `DemoGateway`, an in-memory stand-in with the same rules |
| Backend (planned) | Supabase: PostgreSQL, Auth, Row Level Security, Storage, Edge Functions |
| Typography | Barlow and Barlow Condensed |
| Tests | xUnit |

## Project structure

```text
gym-membership/
├── src/
│   ├── GymMembership.Core/          Plain .NET library, no UI
│   │   ├── Models/                  Data models
│   │   ├── Services/                One service per feature, plus error handling
│   │   ├── ViewModels/              One ViewModel per screen
│   │   └── Demo/                    In-memory backend used until Supabase is connected
│   └── GymMembership.App/           The MAUI app
│       ├── Views/                   Pages (XAML)
│       ├── Controls/                Lane meter, status chip, banner
│       ├── Resources/Styles/        Colours and styles
│       └── *Shell.xaml              Member, coach and staff navigation
├── tests/
│   └── GymMembership.Tests/         Behaviour tests against the demo backend
└── docs/
    ├── dfd/                         Data flow diagram, database diagram and GYM_TABLES.sql
    ├── specs/                       Product requirements and technical spec
    └── superpowers/plans/           Phase 1 implementation plan
```

The app talks to the backend through a single interface, `ISupabaseGateway`. Moving to the real database means swapping one line in `MauiProgram.cs`.

## Database plan (Supabase)

> **Written and tested locally, not applied to a Supabase project yet.** This is a summary. The full build spec is [`docs/specs/database-plan.md`](docs/specs/database-plan.md). The migrations are in [`supabase/migrations/`](supabase/migrations) and pass `supabase/tests/local`. The app's models, services and demo backend already follow the new schema. The Supabase-backed gateway is written; it has not run against a real project yet.

### Decisions

| Topic | Decision |
|---|---|
| Logins | Members, coaches, front desk and admin all use the app. Every person is a Supabase Auth user and `profiles.id` is their Auth uuid. Passwords and sessions stay in Supabase Auth, not in our tables |
| Member sign-up | Members sign up themselves. A database trigger creates their profile, member row and member role. A guest is a member with no active membership, not a separate role |
| Staff accounts | The owner creates coaches and front-desk staff through a `create-user` Edge Function, because it needs the service-role key, which never ships in the app. They change the temporary password on first login |
| IDs | People are keyed by uuid: `members.user_id` and `coaches.user_id` are the primary keys. Other tables use auto-numbered `bigint` IDs |
| Currency | PHP only, so there are no currency columns |
| Payments | No payments API. Members pay by QR in the app, or staff record cash and walk-ins at the counter |

### Tables

| Group | Tables |
|---|---|
| Identity | `profiles`, `roles`, `permissions`, `role_permissions`, `user_roles`, `user_settings`, `gym_settings` |
| People | `members`, `coaches` |
| Catalog | `membership_packages`, `amenities`, `discounts` |
| Entitlements | `user_membership_packages`, `membership_renewals`, `user_amenities` |
| Payments | `payments` |
| Coaching | `coach_hires`, `training_sessions`, `time_requests`, `session_attendance`, `shift_posts`, `messages` |
| Operations | `gym_check_ins`, `amenity_usages`, `user_notifications`, `audit_log` |

### How payments work

#### QR payment (in the app)

1. The member picks a package, renewal or amenity, and can enter a discount code.
2. A pending payment is created with the amount after discount, a short payment code (e.g. `GYM-7F3K2`) and a 48-hour expiry.
3. The app shows the gym's QR (GCash / Maya / QR Ph, stored in `gym_settings`), the amount in ₱ and the payment code to type into the e-wallet message.
4. The member pays, then enters the reference number and uploads a screenshot.
5. Staff check it against the gym's e-wallet and approve or reject it with a reason.
6. Approving assigns a receipt number (e.g. `OR-2026-000123`), activates the membership or amenity (or completes the renewal), notifies the member and writes the audit log.
7. A rejected payment is final; the member taps **Pay again**. An unpaid payment expires after 48 hours and its pending membership is cancelled.

#### Cash at the counter

Staff record the payment and it is verified straight away with a receipt number. A GCash payment made at the counter works the same way, with a reference number.

#### Walk-ins

Staff record a walk-in payment with the person's name, and a check-in is created with it. The price defaults to `gym_settings.walk_in_price`.

#### Corrections

Verified payments are never edited. Only an admin can void one, with a reason. Voiding reverses what the payment activated and is recorded in the audit log.

### Rules the database enforces

- One active membership per member, and one active hire per member and coach.
- Each payment pays for exactly one thing, except walk-ins, which have a name instead of a member.
- Staff can't verify their own payment.
- The same e-wallet reference number can't be used twice, and a discount code works once per member.
- No overlapping scheduled sessions for a coach or a member, and no overlapping shifts for a coach.
- Messages are only between a coach and a member, sent by one of them.

### Still to build

1. **Migrations** in `supabase/migrations/`:
   - the tables from `GYM_TABLES.sql`
   - row level security for every table
   - server-side functions: `avail_membership`, `renew_membership`, `submit_payment_proof`, `verify_payment`, `record_counter_payment`, `void_payment`, `record_check_in` and the rest
   - the sign-up trigger that creates a member's profile and role
   - storage buckets: `payment-proofs`, `amenity-proofs`, `gym-assets`
   - nightly expiry jobs with `pg_cron`
   - seed data
2. **The `create-user` Edge Function** for coach and staff accounts.
3. **App models:** update `Models.cs`, the services and `DemoGateway` to match (uuid member and coach IDs, no currency, payment code instead of QR token).
4. **Pages:** QR payment with reference number, counter payment and walk-in, gym settings.
5. **Tests:** pgTAP for each security rule and function, and xUnit for the payment flows.
6. **Docs:** update the PRD, technical spec and database diagram.

## Roadmap

- [x] Screens for every role on a demo backend
- [x] Admin-created accounts, discount codes, member and coach messaging, coach shifts and trainees, reschedule and cancel (demo backend)
- [x] Guest sign-up with locked member pages, password change and reset, employee creation (demo backend)
- [x] Database table design ([`GYM_TABLES.sql`](docs/dfd/GYM_TABLES.sql))
- [x] Supabase migrations: tables, row level security, functions, storage, scheduled jobs (checked on a local Postgres, not yet pushed to a project)
- [x] `create-user` and `reset-password` Edge Functions (written, not yet deployed or run)
- [x] App models, services, demo backend and pages follow the new schema: QR payments with codes and reference numbers, counter and walk-in payments, voids, gym settings
- [x] Supabase-backed gateway (`SupabaseGateway`, tested against a fake Supabase server, not yet against a real project)
- [ ] Phase 2: products and sales, employees, working hours, sales reports

## Docs

- [Product requirements](docs/specs/PRD.md)
- [Technical spec](docs/specs/technical-spec.md)
- [Database plan](docs/specs/database-plan.md)
- [Database tables](docs/dfd/GYM_TABLES.sql)
- [Database diagram](docs/dfd/gym-membership-db-diagram.md)
- [Data flow diagram](docs/dfd/Gym%20DFD.png)
- [Phase 1 implementation plan](docs/superpowers/plans/2026-10-04-gym-membership-phase1.md)
