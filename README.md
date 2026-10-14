# Gym Membership

[![Release](https://img.shields.io/github/v/release/BowieRamirez/gym-membership?include_prereleases&label=release)](https://github.com/BowieRamirez/gym-membership/releases)
[![Tests](https://github.com/BowieRamirez/gym-membership/actions/workflows/tests.yml/badge.svg)](https://github.com/BowieRamirez/gym-membership/actions/workflows/tests.yml)
[![Android build](https://github.com/BowieRamirez/gym-membership/actions/workflows/android.yml/badge.svg)](https://github.com/BowieRamirez/gym-membership/actions/workflows/android.yml)
[![Windows build](https://github.com/BowieRamirez/gym-membership/actions/workflows/windows.yml/badge.svg)](https://github.com/BowieRamirez/gym-membership/actions/workflows/windows.yml)

One app for the whole gym floor. Members manage their plan, coaches run their sessions, and the front desk handles payments, all from a single .NET MAUI codebase.

> **Status: alpha, [version 0.1.0a](https://github.com/BowieRamirez/gym-membership/releases/tag/v0.1.0a).** Installable on Android and Windows, running on a real Supabase backend. See [Project status](#project-status) for what works and what is still open.

## Contents

- [Project status](#project-status)
- [What it does](#what-it-does)
- [Accounts and access](#accounts-and-access)
- [Download and install (testers)](#download-and-install-testers)
- [Getting started](#getting-started)
- [Builds and releases](#builds-and-releases)
- [Tech stack](#tech-stack)
- [Project structure](#project-structure)
- [Database plan (Supabase)](#database-plan-supabase)
- [Roadmap](#roadmap)
- [Docs](#docs)

## Project status

*Last updated 2026-10-14.* The first alpha, **[0.1.0a](https://github.com/BowieRamirez/gym-membership/releases/tag/v0.1.0a)**, is published and the Android and Windows builds are green on `main`.

| Area | Status | Notes |
|---|---|---|
| **App screens** | Done | Member, coach, front-desk and admin screens, including guest sign-up with locked pages, QR payments, counter and walk-in payments, messaging, sessions, shifts and attendance |
| **Phones** | Done | Member, coach and shared pages are laid out for phone screens (full-width buttons, stacked cards, 16-unit padding) |
| **Sign-up rules** | Done | Names accept letters only (also enforced in the database by migration `0012`, see below); email up to 100 characters; password 6 to 64 characters |
| **Coach hiring** | Done | A coach can pause hiring; members still see them, grayed out, and cannot hire until the coach resumes |
| **Backend** | Live | Release builds use the real Supabase project: authentication, row level security and both Edge Functions answer. The demo backend remains for builds without project settings |
| **Database** | Needs one step | Migrations `0001` to `0012` are in the repository. **`0012` (names are letters only) still has to be run on the project** |
| **Android** | Released | One signed APK for phones (arm64) and Android Studio emulators (x86_64), 33 MB |
| **Windows** | Released | Signed MSIX installer (recommended) and a signed MSI, both self-contained, 66 MB and 120 MB |
| **CI/CD** | Working | Separate Tests, Android and Windows workflows; a version tag publishes to the Releases page ([how](#builds-and-releases)) |
| **Tests** | Passing | 143 unit tests and 57 database rule checks on every push |

**Not done yet**

- The installers have not been tried on a clean phone and a clean PC (sign in, close, reopen, still signed in).
- No dedicated smoke-test account is set up, so the release check does not yet cover a real sign-in.
- A free Supabase project pauses after about a week without use; a paid plan or a keep-alive job is needed before testers rely on it.
- Windows and Android warn about the self-signed test certificate the first time. A purchased certificate would remove that.
- Not built: iOS and Mac, the Google Play and Microsoft Store listings, and [Phase 2](#roadmap) (products and sales, employees, working hours, sales reports).

**Known issues**

- On the Coaches page, a coach you have not hired shows "No shifts posted yet" even when they have shifts. The database only lets members see the shifts of coaches they hired.
- The Trainees page says "1 sessions completed" (plural wording).

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

## Download and install (testers)

Every build is published on the **[Releases page](https://github.com/BowieRamirez/gym-membership/releases)**. The newest is **[0.1.0a](https://github.com/BowieRamirez/gym-membership/releases/tag/v0.1.0a)**, an alpha, so GitHub labels it *Pre-release*. Pick the file for your device:

| For | Download | Size |
|---|---|---|
| **Android phone or Android Studio emulator** | [`GymMembership-0.1.0a-android.apk`](https://github.com/BowieRamirez/gym-membership/releases/download/v0.1.0a/GymMembership-0.1.0a-android.apk) | 33 MB |
| **Windows PC** (recommended) | [`GymMembership-0.1.0a-win-x64-sideload.zip`](https://github.com/BowieRamirez/gym-membership/releases/download/v0.1.0a/GymMembership-0.1.0a-win-x64-sideload.zip), the MSIX installer with its certificate and `Install.ps1` | 66 MB |
| **Windows PC** (alternative) | [`GymMembership-0.1.0a-win-x64.msi`](https://github.com/BowieRamirez/gym-membership/releases/download/v0.1.0a/GymMembership-0.1.0a-win-x64.msi), the MSI installer | 120 MB |
| Windows, manual MSIX install | [`GymMembership-0.1.0a-win-x64.msix`](https://github.com/BowieRamirez/gym-membership/releases/download/v0.1.0a/GymMembership-0.1.0a-win-x64.msix) and [`GymMembership-signing.cer`](https://github.com/BowieRamirez/gym-membership/releases/download/v0.1.0a/GymMembership-signing.cer) | 66 MB |

Each file has a checksum next to it (add `.sha256` to the file name, for example [`GymMembership-0.1.0a-android.apk.sha256`](https://github.com/BowieRamirez/gym-membership/releases/download/v0.1.0a/GymMembership-0.1.0a-android.apk.sha256)). These links point at 0.1.0a; for a newer version open the [Releases page](https://github.com/BowieRamirez/gym-membership/releases).

### Android

1. Download the APK on the phone (or drag it onto an emulator).
2. Open it. When asked, allow **Install unknown apps** for your browser or Files app.
3. If Play Protect warns about an unknown developer, choose **Install anyway**.
4. To update, install the newer APK over the old one. It is signed with the same key, so your data stays.

One file works on phones (arm64) and on Android Studio emulators (x86_64).

### Windows

Install **one** of the two, not both.

- **MSIX (recommended).** Download the sideload zip and unzip it. Right-click `Install.ps1`, choose **Run with PowerShell** and accept the administrator prompt. It trusts the app's test certificate and installs the app. Newer versions install over it. Uninstall from **Settings, Apps**.
- **MSI.** Download the `.msi` and run it. Windows SmartScreen may say the publisher is unknown: choose **More info, Run anyway**. No certificate step; it installs for all users under Program Files. Uninstall from **Settings, Apps**.

The apps are signed with a self-signed test certificate, so Windows and Android show a warning the first time.

### Sign in

The release builds use the real Supabase project. Create your own account with **Create an account** on the sign-in screen, or ask the owner for a staff account. You start as a guest until a payment is verified.

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

# the database rules, against a throwaway local Postgres (no Docker)
cd supabase/tests/local && npm install && npm test
```

## Builds and releases

Three GitHub Actions workflows build the app on every push and pull request. Android and Windows are separate workflows, so one platform failing never blocks the other.

| Workflow | Runs on | What it does |
|---|---|---|
| [Tests](.github/workflows/tests.yml) | Ubuntu | Unit tests and the database tests |
| [Android](.github/workflows/android.yml) | Ubuntu | Builds and signs the universal APK (arm64 + x86_64) |
| [Windows](.github/workflows/windows.yml) | Windows | Builds the signed MSIX and the MSI installer |

**To publish a release,** push a version tag. Both platforms then attach their files to the same release:

```bash
git tag -a v0.1.0b -m "Gym Membership 0.1.0b"
git push origin v0.1.0b
```

The tag is the version. `v0.1.0a`, `v1.2.3` and `v1.2.3-rc.1` are accepted; a trailing letter or a `-suffix` marks a pre-release. Signing keys and Supabase settings live in the repository's Actions secrets. Details, the secret list and how the builds are verified: [`docs/specs/ci-cd-pipelines.md`](docs/specs/ci-cd-pipelines.md).

## Tech stack

| Layer | Choice |
|---|---|
| App | .NET 10, .NET MAUI 10, C#, XAML |
| Pattern | MVVM with `CommunityToolkit.Mvvm` and `CommunityToolkit.Maui` |
| Backend | Supabase: PostgreSQL, Auth, Row Level Security, Storage, Edge Functions, through `SupabaseGateway` |
| Demo backend | `DemoGateway`, an in-memory stand-in with the same rules, used when no `supabase.json` is present |
| Typography | Barlow and Barlow Condensed |
| Tests | xUnit, plus the database rules checked on a local Postgres |
| CI/CD | GitHub Actions: separate Android and Windows workflows, signed APK, MSIX and MSI on the Releases page |

## Project structure

```text
gym-membership/
├── src/
│   ├── GymMembership.Core/          Plain .NET library, no UI
│   │   ├── Models/                  Data models
│   │   ├── Services/                One service per feature, plus error handling
│   │   ├── ViewModels/              One ViewModel per screen
│   │   ├── Backend/                 SupabaseGateway, the real backend
│   │   └── Demo/                    In-memory backend used when no project settings are present
│   └── GymMembership.App/           The MAUI app
│       ├── Views/                   Pages (XAML)
│       ├── Controls/                Lane meter, status chip, banner
│       ├── Resources/Styles/        Colours and styles
│       └── *Shell.xaml              Member, coach and staff navigation
├── tests/
│   └── GymMembership.Tests/         Behaviour tests against the demo backend
├── supabase/
│   ├── migrations/                  Database schema, security rules, functions, jobs
│   ├── functions/                   Edge Functions (create-user, reset-password)
│   ├── scripts/                     Owner and demo account scripts
│   └── tests/local/                 Database rule tests on a local Postgres
├── installer/windows/               WiX definition of the MSI installer
├── scripts/                         check-supabase.mjs, the release smoke check
├── .github/workflows/               Tests, Android and Windows pipelines
└── docs/
    ├── dfd/                         Data flow diagram, database diagram and GYM_TABLES.sql
    ├── specs/                       Product requirements, technical spec, database plan, CI/CD spec
    └── superpowers/plans/           Implementation plans
```

The app talks to the backend through a single interface, `ISupabaseGateway`. `MauiProgram.cs` picks `SupabaseGateway` when `Resources/Raw/supabase.json` has real project details, and `DemoGateway` otherwise.

## Database plan (Supabase)

> **Applied to the connected Supabase project and used by the release builds.** This is a summary. The full build spec is [`docs/specs/database-plan.md`](docs/specs/database-plan.md). The migrations are in [`supabase/migrations/`](supabase/migrations) and pass `supabase/tests/local`. The tables, security rules and both Edge Functions answer on the project. **Migration `0012` (names are letters only) is new: run it with `npx supabase db push` after checking the query at the top of the file.**

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

### Next steps

1. **Run migration `0012`** on the Supabase project (names are letters only; the file starts with a query that lists any old names that would break it).
2. **Create a dedicated smoke-test member** and add it as the `SUPABASE_SMOKE_EMAIL` and `SUPABASE_SMOKE_PASSWORD` Actions secrets, so every release also checks a real sign-in.
3. **Free-tier projects pause after about a week without use.** Use a paid plan or a small keep-alive job before testers rely on it.
4. **Docs:** bring the PRD, technical spec and database diagram up to date with the schema.

## Roadmap

- [x] Screens for every role on a demo backend
- [x] Admin-created accounts, discount codes, member and coach messaging, coach shifts and trainees, reschedule and cancel (demo backend)
- [x] Guest sign-up with locked member pages, password change and reset, employee creation (demo backend)
- [x] Database table design ([`GYM_TABLES.sql`](docs/dfd/GYM_TABLES.sql))
- [x] Supabase migrations: tables, row level security, functions, storage, scheduled jobs, names letters only (`0012`, pending on the project)
- [x] `create-user` and `reset-password` Edge Functions (deployed on the project)
- [x] App models, services, demo backend and pages follow the new schema: QR payments with codes and reference numbers, counter and walk-in payments, voids, gym settings
- [x] Supabase-backed gateway (`SupabaseGateway`), checked against the real project
- [x] Register rules: letters-only names, email and password limits; coaches can pause hiring
- [x] CI/CD: separate Android and Windows pipelines, signed APK, MSIX and MSI, Releases page
- [x] First alpha release, [0.1.0a](https://github.com/BowieRamirez/gym-membership/releases/tag/v0.1.0a)
- [ ] Try the installers on a clean phone and a clean PC
- [ ] Optional: nightly pre-release builds, keep-alive for the Supabase project
- [ ] Phase 2: products and sales, employees, working hours, sales reports

## Docs

- [Product requirements](docs/specs/PRD.md)
- [Technical spec](docs/specs/technical-spec.md)
- [Database plan](docs/specs/database-plan.md)
- [CI/CD pipelines (Windows and Android)](docs/specs/ci-cd-pipelines.md)
- [Releases](https://github.com/BowieRamirez/gym-membership/releases)
- [Database tables](docs/dfd/GYM_TABLES.sql)
- [Database diagram](docs/dfd/gym-membership-db-diagram.md)
- [Data flow diagram](docs/dfd/Gym%20DFD.png)
- [Phase 1 implementation plan](docs/superpowers/plans/2026-10-04-gym-membership-phase1.md)
