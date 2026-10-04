# Gym Membership

One app for the whole gym floor. Members manage their plan, coaches run their sessions, and the front desk verifies payments, all from a single .NET MAUI codebase.

> **Status:** the interface is complete and runs on an in-memory demo backend. Supabase (PostgreSQL) is the planned real backend and is not connected yet.

---

## What it does

| Role | Can do |
|---|---|
| **Member** | See days left on a membership, buy or renew a package, attach proof of payment, use amenities, hire a coach, request training sessions |
| **Coach** | Offer open slots, approve requests, manage sessions, mark attendance, edit a public profile |
| **Front desk** | Verify or reject payments, check members in, confirm amenity visits |
| **Admin** | Everything above, plus users and roles, packages and amenities, revenue, and an audit log |

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

## Roadmap

- [x] Screens for every role on a demo backend
- [ ] Supabase schema, row level security and server-side functions
- [ ] Supabase-backed gateway and a sign-up page
- [ ] Phase 2: products and sales, employees, working hours, sales reports

## Docs

- [Product requirements](docs/specs/PRD.md)
- [Technical spec](docs/specs/technical-spec.md)
- [Phase 1 implementation plan](docs/superpowers/plans/2026-10-04-gym-membership-phase1.md)
