# Gym Membership

One app for the whole gym floor. Members manage their plan, coaches run their sessions, and the front desk verifies payments, all from a single .NET MAUI codebase.

> **Status:** the interface is complete and runs on an in-memory demo backend. Supabase (PostgreSQL) is the planned real backend and is not connected yet.

---

## What it does

| Role | Can do |
|---|---|
| **Member** | See days left on a membership, buy or renew a package (with a discount code), attach proof of payment, use amenities, hire a coach, message that coach, see their posted shifts, request, reschedule or cancel training sessions |
| **Coach** | Manage trainees, message them, post shifts, offer open slots, approve requests, schedule, reschedule or cancel sessions, mark attendance, edit a public profile |
| **Front desk** | Verify or reject payments, check members in, confirm amenity visits |
| **Admin** | Everything above, plus adding members and coaches, packages (add, edit, delete) and amenities, discount codes, revenue, and an audit log |

Anyone can create a member account from the sign-in screen. A new account is a **guest**: it can see Packages, My membership, Coaches, Alerts and Settings. Amenities, Messages, Sessions and Time requests show a "get a membership" notice until a package payment is verified by the owner or front desk. When the plan expires, the account goes back to guest. The owner adds coaches and front-desk staff on the Users page with a temporary password. People change their password under Settings, and the owner can reset a forgotten one.

Colour is only ever used for status: yellow for pending, green for verified, red for rejected, blue for scheduled. The dashed lane on the membership screen is the one recurring motif, borrowed from court floor markings.

## Run it

**You need:** the [.NET 10 SDK](https://dotnet.microsoft.com/download) and the MAUI workload (`dotnet workload install maui`).

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

### Run the tests

```bash
dotnet test tests/GymMembership.Tests
```

## Tech stack

| | |
|---|---|
| App | .NET 10, .NET MAUI 10, C#, XAML |
| Pattern | MVVM with `CommunityToolkit.Mvvm` and `CommunityToolkit.Maui` |
| Backend (planned) | Supabase: PostgreSQL, Auth, Row Level Security, Storage |
| Backend (now) | `DemoGateway`, an in-memory stand-in with the same rules |
| Typography | Barlow and Barlow Condensed |
| Tests | xUnit |

## Project structure

```
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
    ├── dfd/                         Data flow diagram and database diagram
    ├── specs/                       Product requirements and technical spec
    └── superpowers/plans/           Phase 1 implementation plan
```

The app talks to the backend through a single interface, `ISupabaseGateway`. Moving to the real database means swapping one line in `MauiProgram.cs`.

## Option under consideration: owner-only, manual cash payments

> **Not implemented.** This is an alternative design kept for later. Nothing in the app or specs has changed yet.

If the owner ends up being the only person using the system, payments can be recorded by hand at the counter instead of members paying through the app. No payment API is needed.

### Process

**Adding members and coaches**
- The owner adds them as profiles (name, phone, optional notes; coaches also get specialty and rate), not login accounts.
- Only the owner logs in (plus front-desk employees later, if hired).
- A later "invite / enable login" step can give members or coaches app access if it's ever needed.

**Monthly membership (cash)**
1. The member pays at the counter.
2. The owner opens the member, picks a package and taps **Record payment**. The payment is saved as `verified` straight away.
3. The membership becomes active; the end date is the start date plus the package's `duration_days`.
4. Renewals work the same way and extend from the current end date, not from today, when paid early.
5. A receipt number is generated to write on a paper receipt.

**Walk-ins**
- Recorded as a day pass: optional name, amount and date. No member profile needed.
- Counted in today's income and check-ins.

**Keeping cash honest**
- Payments are never deleted or edited. Mistakes are voided with a reason and recorded again; the audit log keeps the history.
- An end-of-day cash summary shows expected cash (memberships + walk-ins) to compare with the drawer.
- An optional payment method (Cash / GCash / Bank transfer) with a reference number covers transfers too.

### What would change

| Area | Change |
|--|---|
| `payments` table | Add `method`, `receipt_no`, `kind` (membership / renewal / walk-in), `recorded_by`, `voided_at` / `void_reason` |
| `payments` CHECK rule | Allow walk-in payments that aren't linked to a membership or amenity |
| `verify_payment` | Replace with `record_payment` (saved as verified right away) and `void_payment` |
| "Verifier can't verify own payment" rule | Remove, or keep only for a future multi-staff setup |
| Members and coaches | Profiles not tied to login accounts |
| Proof upload and QR flow | Drop or defer |
| Reports | Daily cash summary; revenue split by membership vs walk-in |

**Open question:** should members and coaches keep the mobile app (to view their membership, sessions and messages), or is the system owner-only from end to end? This decides whether member and coach logins are removed entirely or just in-app payment.

## Roadmap

- [x] Screens for every role on a demo backend
- [ ] Supabase schema, row level security and server-side functions
- [x] Admin-created accounts, discount codes, member and coach messaging, coach shifts and trainees, reschedule and cancel (demo backend)
- [ ] Supabase-backed gateway
- [ ] Phase 2: products and sales, employees, working hours, sales reports

## Docs

- [Product requirements](docs/specs/PRD.md)
- [Technical spec](docs/specs/technical-spec.md)
- [Phase 1 implementation plan](docs/superpowers/plans/2026-10-04-gym-membership-phase1.md)
