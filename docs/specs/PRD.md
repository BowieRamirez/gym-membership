# Gym Membership & Coach Management System — PRD

Status: Draft for review · Date: 2026-10-04
Sources: `docs/dfd/Gym DFD.png`, `docs/dfd/gym-membership-db-diagram.md`
Companion: [technical-spec.md](technical-spec.md)

## 1. Purpose

A real gym needs one system to sell memberships, verify payments, connect members with coaches, schedule training, and track attendance. Members and coaches use mobile devices; Admin and Employee staff use a Windows front-desk app. One .NET MAUI codebase serves all of them, backed by Supabase (PostgreSQL).

## 2. Users and roles

| Role | Platform | Goal |
|---|---|---|
| **Member (Customer)** | Android / iOS | Avail memberships, coaches and amenities; track membership status; request training sessions |
| **Coach** | Android / iOS | Publish availability, manage training sessions and mentee requests, mark attendance |
| **Admin** | Windows (mobile optional) | Manage users, coaches, plans, amenities; track revenue; audit |
| **Employee** | Windows | Verify payments at the front desk (Phase 1); sell products and track working hours (Phase 2) |

## 3. Scope

### Phase 1 — Core
| ID | Feature | Roles | Acceptance summary |
|---|---|---|---|
| F1 | Auth & profile | All | Email/password sign-up and login; session persists across restarts; settings (theme, language, timezone) |
| F2 | Membership packages | Member, Admin | Member browses active packages and avails one; Admin creates/edits/deactivates packages |
| F3 | Payments | Member, Admin, Employee | Payment created with QR; member uploads proof; staff verify or reject; verified payment activates the related entitlement |
| F4 | Membership status & renewal | Member | Member sees status/expiry; can renew; renewal extends `ends_at` once its payment is verified |
| F5 | Amenities | Member, Admin | Admin manages catalog; member avails and logs usage with proof; staff verify usage |
| F6 | Coaches & hiring | Member, Coach, Admin | Member browses available coaches, hires/ends a hire; coach toggles availability; Admin manages coaches |
| F7 | Training sessions | Member, Coach | Either party proposes a time request; the other approves/rejects; approval creates a session; session can be completed, cancelled, or no-show |
| F8 | Attendance | Coach, Admin | Coach marks session attendance (present/late/absent/excused); gym check-ins are recorded |
| F9 | Notifications | All | In-app list with unread state; created for time requests, payments, renewals/expiry |
| F10 | Admin management | Admin | Manage users and role assignments; revenue summary by period |
| F11 | Audit log | Admin | Sensitive actions (payment verify/reject, role change, package price change) are recorded and viewable read-only |

### Phase 2 — Extras (new tables required)
Products & inventory, product sales (Employee), Employee management, Working Hours (clock in/out, tracking), Sales reports, full Audit UI.

### Out of scope (v1)
Online payment gateways, push notifications, multi-branch, offline mode.

## 4. Key user flows

1. **Avail membership:** pick package → pending membership + pending payment → staff verify → membership `active` with dates from `duration_days` → member notified.
2. **Renew:** renewal + payment created → verified → `ends_at` extended → completed.
3. **Book training:** time request (pending) → counterpart approves → session scheduled → both notified.
4. **Expiry:** daily job marks expired memberships and sends reminders (e.g. 7 days and 1 day before).

## 5. Business rules

- A member needs an `active` membership to avail coaches or log amenity usage (confirm per amenity — assumption).
- A coach cannot have overlapping scheduled sessions.
- Only Admin/Employee roles can verify payments; a verifier cannot verify their own payment.
- Payment states: `pending → verified | rejected | expired`. Verified payments are immutable.
- Currency defaults to USD and is configurable (ISO 4217 on each money row).

## 6. Non-functional requirements

- List screens render in < 2 s on a typical connection.
- All data access is protected by RLS; the client holds only the Supabase anon key.
- Accessible: labels, sufficient contrast, light/dark theme.
- Targets: Android 5.0+ (API 21), iOS 13+, Windows 10 1809+/11.

## 7. Success criteria

- Every Phase 1 flow completes end-to-end for each role against a real Supabase project.
- RLS tests prove a member cannot read or modify another member's data.
- Front desk can verify a payment and activate a membership in under 30 seconds.

## 8. Open questions

1. Currency and locale of the gym (default USD assumed).
2. Which payment QR provider is used, and is proof upload sufficient for verification?
3. Must an active membership gate amenity usage and coach hiring?
4. Do coaches get paid through the system (`hourly_rate`, `payout` notification type exist)? Deferred unless needed.
5. Is `members` the same person as the "User/Customer" in the DFD? Assumed yes.
