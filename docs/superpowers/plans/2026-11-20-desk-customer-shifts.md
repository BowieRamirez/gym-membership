# Desk customer picking, payment screen and cash shift discrepancies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-20
Last updated: 2026-11-20
Status: Part 2 (coach revamp, Phases 8-15) PLANNED, waiting on E1-E12, not built. Part 1: Phases 1-6 implemented and verified (1216 unit tests, Windows & Android builds, local DB tests, and `0117` dry-run). Phase 7: awaiting live migration push, live grants check, owner hand checks, and branch push.
Branch: `desk-customer-shifts-2026-11-20` (cut from `origin/main` at `8c1555f`, v0.9.0 + `0116`). **Owner's rule: this branch is not merged into `main` yet.** Pushed to GitHub for the owner; no PR until the owner asks.

**Goal:** The owner's list of 2026-11-20 (`docs (1).pdf`): Pay again goes straight to paying and takes a discount code; staff pick a customer by typing (suggestions, no Find button) on the Cashier and on Book for a customer; the bug where a customer can't be chosen on Book for a customer is fixed; the Cashier's payment screen makes the customer obvious; a picked customer's status is re-read from the server; the Cashier's table hides people who already have a membership or Day pass, the amenity booking doesn't; a cash shift's shortage or overage is shown apart from sales, needs a reason, and supervisors can add investigation notes with an audit trail.

**Architecture:** Reuse what exists: `FindMembersAsync` / `MemberHit` (`Services.cs:1380`, carries `HasActiveMembership`, `PaidUntil`), `DeskBookingViewModel` and `CashierViewModel` (`StaffViewModels.cs`), the 500 ms debounce with newest-wins cancel already used by the code check (`CashierViewModel` `_codeCheck`), `MyPaymentsViewModel.PayAgainAsync` + `OnlinePay.OpenAsync`, `check_discount_code` (0107) and `CodeCheckFeedback`, `cash_shifts` / `close_cash_shift` / `cash_shifts_list` (0050: difference and close note already there), `audit()` and the Revenue page's shift table. New database work: a shift close reason, an append-only supervisor notes table with one function, a desk-side duplicate-membership guard if the reproduction shows one is missing.

**Tech Stack:** Supabase (Postgres migrations), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** owner's list `C:\Users\bowie\Downloads\docs (1).pdf` (4 pages, 2026-11-20): Pay again, customer dropdown and suggestions, the pick bug, "Customer Payment UI Revamp & Membership-Based Filtering" (sections 1-4), and "Shortage / Overage / Investigation notes".

## How to use this plan

Same rules as `2026-11-19-owner-fixes.md`:

- Decisions first. One phase at a time, in order; each phase ends working and committed.
- **Skills, every phase:**
  - `ponytail` full mode for the whole session (smallest diff, reuse before writing).
  - `superpowers:test-driven-development` for every rule: the failing test first, watched failing.
  - `superpowers:systematic-debugging` for the pick bug (Phase 1): reproduce before fixing.
  - `impeccable` (and `frontend-design` per CLAUDE.md) for every screen change. Cashier, Book for a customer and Revenue are staff pages: **Windows only**. Pay again is a member page: Android and Windows, light and dark, 1.3 font scale, real phone layout.
  - `ponytail:ponytail-review` on each phase's diff before its commit.
  - `superpowers:verification-before-completion` before any "done".
  - A fresh whole-branch review at the end (`superpowers:requesting-code-review`).
- Push each phase's migrations live when the phase is finished (dry run first) and check live grants. Commits pushed to `origin/desk-customer-shifts-2026-11-20`; nothing merges into `main`.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Bug: can't choose a customer on Book for a customer | | Done (`cb66143`) |
| 2 | Customer box: suggestions while typing, no Find button (Cashier and Book for a customer) | D1 | Done (`fa114c2`) |
| 3 | Status re-read on pick; Cashier table filtered by status, amenities show everyone; no duplicate sale | D2, D3, D4 | Done (`a285616`) |
| 4 | Cashier payment screen: the customer is obvious | D5 | Done (`0f5dcdc`) |
| 5 | Pay again: straight to paying, with a discount code | D6 | Done (`e93eac3`) |
| 6 | Cash shift: shortage / overage, reason, supervisor notes, audit | D7, D8 | Done (`25c3a8f`) |
| 7 | Whole-branch checks, review, owner hand checks | | In progress |

## 1. Findings (2026-11-20)

| Item | Today | Consequence |
|---|---|---|
| 1 Pay again | Member Payments page (`PaymentsPage.xaml:25,54`) → `MyPaymentsViewModel.PayAgainAsync` (`MemberViewModels.cs:579`) asks "Create a new payment?", calls `svc.PayAgainAsync(item)` with **no code** (`Services.cs:461` passes `null`), then says "Pay it online on Packages." The member has to go to Packages and press Pay online themselves. Only for rejected/expired package payments (`CanPayAgain`, `Services.cs:310`). | Open the Xendit checkout straight after the payment is made; pass a code through `ExtendAsync` / `AvailAsync` / `RenewAsync` (they already take one). |
| 2 Find button | Book for a customer (`AmenityStatusPage.xaml:37-42`) and the Cashier (`CashierPage.xaml:~155-164`) both need a typed search plus **Find** (or Enter) before any row shows. | Suggestions as you type replace Find in both. |
| 3 Pick bug | Book for a customer: the hit rows' **Choose** is `OnPickCustomer` (`AmenityStatusPage.xaml.cs:17`) → `Booking.PickMemberCommand` → `Member = hit`. Code reads right; the owner says nothing happens. Suspects to check by reproducing: the rows' `x:DataType="svc:MemberHit"` inside a `BindingContext="{Binding Booking}"` panel, a later refresh (`AutoRefreshAsync` of the status page) rebuilding `Booking`, or `Member` being cleared by `FindAsync`. | Reproduce on a Windows build (demo first, then live) before changing anything. |
| 4 Payment screen | Cashier order panel shows the member's name in small type above the order (screenshot: "Order / Lilian Sabado"); the table rows look the same whether picked or not. | Revamp the order panel and mark the picked row (PDF §1). |
| 5 Status | `MemberHit.HasActiveMembership` comes from the search's moment; picking a row reuses it. A Day pass is a 1-day package term (0098), so `HasActiveMembership` already covers "valid Day pass" while it lasts (ends at Manila midnight). | Re-read the one member on pick (PDF §2) without dropping the pick (PDF §4 last rule). |
| 6 Filtering | Cashier lists every active account (`FindAsync`, `StaffViewModels.cs:291`: only `inactive` hidden). The Cashier kinds are Membership, Renewal, Amenity, Day pass. **Renewal is for people who have a membership**, so hiding them everywhere on the Cashier would break Renewal. | Filter by the chosen kind (D2). |
| 7 Duplicate sale | `user_membership_packages_one_active` allows one active package per member, and `avail_membership` refuses a Day pass for a member (`already_member`, 0098). Whether the desk's membership sale refuses a member who already has one needs a database test. | Test first; guard only if it fails (D4). |
| 8 Shifts | 0050 already: works out `expected` and `difference` (`counted - expected`), records the cashier (`cashier_id`), needs a note when the count is off (`note_required`), audits `cash_shift.close`, and cash taken is tied to `payments.cash_shift_id`. Revenue page lists shifts with `DifferenceText` ("₱50.00 short"). Missing: shortage and overage as their own figures, a reason choice, notes by a supervisor after closing, their audit trail, and a check that the difference never counts as sales. | Phase 6. |

## 2. Decisions

| # | Decision | Answer |
|---|---|---|
| D1 | **Customer box (Cashier and Book for a customer):** typing 2 or more letters shows up to 8 suggestions under the box (name, then email/phone in grey, and the status chip), 300 ms after the last key, newest search wins. Arrow keys / click choose one; choosing fills the box with the name and picks the customer. The **Find** button goes; Enter picks the top suggestion. The Cashier's table under it stays and follows the same filter (the owner's screenshot shows it); Book for a customer shows suggestions only, no table. The "dropdown beside customer" in the list is read as this suggestion list; a separate dropdown of all members would be hundreds of names. Recommended. | Owner, 2026-11-20: as recommended |
| D2 | **Cashier table filter, by kind:** **Membership** and **Day pass** hide anyone with an active membership or a valid Day pass (PDF §3). **Renewal** shows only people with a membership (the opposite: they're the ones who renew). **Amenity** and **Book for a customer** show everyone (PDF §3 Amenities). A hidden person typed by name gets one line: "Lilian Sabado already has a membership until 16 Dec 2026." so staff aren't left guessing. Recommended. | Owner, 2026-11-20: as recommended |
| D3 | **Status re-read on pick:** picking a customer (suggestion or row) re-reads that one member from the server (`member_status(p_member uuid)` or `FindMembersAsync` by id, whichever already returns it) and updates the chip; the pick stays. If the fresh status now makes them ineligible for the kind (bought a membership in the app a minute ago), the order says so and Complete payment is off; the pick is not cleared. Statuses: **Member until <date>**, **Day pass until midnight**, **No membership**. Expired terms are No membership. | Owner, 2026-11-20: as recommended |
| D4 | **No duplicate sale:** the database refuses a desk membership or Day pass sale to a member who already has an active membership or Day pass (`already_member`), whatever the app shows. Renewal unaffected. Only built if the Phase 3 test shows it isn't refused today. | Owner, 2026-11-20: as recommended |
| D5 | **Payment screen (Cashier order panel):** top of the panel is the customer: full name large (the page's biggest text after the total), status chip, email and phone under it, and a **Change** link. The chosen row in the table gets the selected background and a check. Before anyone is picked the panel says "Choose a customer". Order lines, method and code sit under a divider. The Record confirm already names the customer (`OrderWho`). | Owner, 2026-11-20: as recommended |
| D6 | **Pay again:** a small popup: what it is, the price, a **Discount code** box with the live check (same line as Packages: "Code works: 10% off. You pay ₱1,350.00."), and **Pay online**. Pay online makes the payment with the code and opens the Xendit checkout at once (the "auto redirect"), then checks it on return as Packages does. Cancel makes nothing. Recommended. Alternative: send the member to Packages with the Buy popup opened for that package. | Owner, 2026-11-20: as recommended |
| D7 | **Shift close reason ("options in the UI"):** when the count differs, the cashier picks a reason: **Wrong change given**, **Sale not recorded**, **Counting mistake**, **Float was wrong**, **Other**; the note box stays and is required for Other. Stored as `close_reason`. Recommended. | Owner, 2026-11-20: as recommended |
| D8 | **Supervisor investigation notes:** "supervisor" = admin and system admin (front desk can't). On the Revenue page's shift table, a shift that's short or over shows **Shortage ₱x** or **Overage ₱x** in its own column, the reason, the note, and **Add note**. Notes are append-only (`cash_shift_notes`: shift, author, note, time), never edited or deleted; each one writes an audit row `cash_shift.note`. A shift can be marked **Resolved** with a last note. Shortages and overages are totalled on their own line in Revenue and the PDF, never added to or taken from sales. Recommended. | Owner, 2026-11-20: as recommended |

## Global Constraints

- Migrations start at `0117`, next free number in phase order. Every new or recreated function revokes `public, anon, authenticated` explicitly and grants only who needs it; check live grants after each push.
- No IP addresses in any log (0083).
- Manila time for "today" and "midnight" (`Asia/Manila`).
- `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo builds and put it back at once.
- Demo gateway mirrors every new rule (`DemoGateway`, `DemoCashShifts`, `DemoSubscriptions`, `DemoReservations`).
- Staff pages Windows only; member pages Android and Windows.
- This branch never merges into `main` until the owner says so.

## Review Focus

1. **Picked customer, then the 30-second auto refresh** → the pick, the order and the typed code stay (PDF §4 last rule).
2. **Member buys a membership in the app while the desk has them picked for Membership** → on Complete payment the server refuses `already_member`; the screen shows the fresh status, keeps the pick.
3. **Day pass bought this morning, desk on Membership kind at 11:59 pm vs 12:01 am** → hidden, then shown (expired is not active).
4. **Typing fast, slow answer for "li" arriving after "lilian"** → the list shows "lilian"'s results only.
5. **Count off by ₱0.01** → reason required; Shortage ₱0.01 shows; sales totals unchanged.

---

## Phase 1: The pick bug

### Task 1: Choosing a customer on Book for a customer

**Files:** found by reproduction; likely `src/GymMembership.App/Views/AmenityStatusPage.xaml(.cs)` and `DeskBookingViewModel` (`StaffViewModels.cs:~2485`), test `tests/GymMembership.Tests/DeskBookingTests.cs`.

- [x] Reproduce (systematic-debugging): demo build, front desk, Amenity status → Book for a customer → search → Choose. Note whether `PickMember` runs and what clears `Member`. Same on the Cashier for comparison.
- [x] Failing test for the root cause found (for example: `Booking.PickMemberCommand.Execute(hit)` then a status refresh → `Booking.Member == hit`).
- [x] Fix at the root, tests pass, `ponytail:ponytail-review`, commit.

## Phase 2: Suggestions as you type (D1)

### Task 2: One search behaviour for both screens

**Files:** `StaffViewModels.cs` (`CashierViewModel` and `DeskBookingViewModel`: `OnSearchChanged` → debounced `SuggestAsync`, `Suggestions` collection, `ChooseSuggestion(MemberHit)`; `Find` command removed), a small shared helper only if both copies would be identical (`MemberSuggest` in `StaffViewModels.cs`), `CashierPage.xaml`, `AmenityStatusPage.xaml` (suggestion list under the box, Find button out), demo mirror untouched (search already demoed), test `tests/GymMembership.Tests/MemberSuggestTests.cs`.

- [x] Failing tests: one letter → no search; "li" then "lil" within 300 ms → one search for "lil"; an old slow answer never replaces a newer one (Review Focus 4); choosing a suggestion picks the member and fills the box; Enter picks the top one.
- [x] Build, screens with `impeccable` (Windows, light/dark), commit.

## Phase 3: Fresh status and the filter (D2, D3, D4)

### Task 3: Status on pick, filter by kind

**Files:** `Services.cs` (`MemberHit` gets `DayPassUntil` or a `StatusText`; `MemberStatusAsync(string memberId) -> AppResult<MemberHit>`; reuse the search query by id if it already returns these), `CashierViewModel` (`Visible(MemberHit, kind) -> bool` per D2; `PickMember` re-reads status, keeps `Member`; `HiddenHint`), `DeskBookingViewModel` (re-read on pick, no filter), `CashierPage.xaml` (status chip per row, hidden-hint line), tests `tests/GymMembership.Tests/CashierFilterTests.cs`.

- [x] Failing tests: Membership and Day pass kinds hide an active member and a Day pass holder, show a lapsed one; Renewal shows only members; Amenity and Book for a customer show all; pick re-reads and keeps the pick through a refresh (Review Focus 1, 3); a stale "No membership" becomes "Member until …" on pick and Complete payment turns off.
- [x] Code, tests pass, commit.

### Task 4: The database refuses a duplicate sale (D4)

**Files:** `supabase/tests/local/desk-duplicate-sale.mjs`; only if it fails: migration `0117_desk_no_duplicate_membership.sql` (the desk sale function refuses `already_member` for Membership / Day pass when `is_active_member`), `Foundation.cs` sentence if missing, demo mirror.

- [x] Test (Review Focus 2): desk sells Membership to a member with an active term → `already_member`, nothing written; Renewal for the same member still works.
- [x] If red: migration, push (dry run first), live grants, commit. (Green as-is: `already_availed` already enforced by `record_counter_payment` and `start_desk_online_payment`.)

## Phase 4: Payment screen (D5)

### Task 5: The customer at the top of the order

**Files:** `CashierPage.xaml` (order panel header, chosen row style), `CashierViewModel` (`OrderWhoStatus`, `ChangeCustomer` command clearing the pick on purpose), test in `CashierFilterTests.cs` (Change clears; nothing else does).

- [x] Test first, then the panel with `impeccable` (Windows, light/dark): name large, status chip, contact, Change; chosen row marked; "Choose a customer" empty state. Commit.

## Phase 5: Pay again (D6)

### Task 6: Code and straight to Xendit

**Files:** `Services.cs` (`PayAgainAsync(PaymentItem item, string? code)` passing the code to `ExtendAsync` / `AvailAsync` / `RenewAsync`), `MyPaymentsViewModel` (`PayAgainCode`, live check reusing `CheckCodeAsync` + `CodeCheckFeedback` as in `PackagesViewModel`, then `PayOnlineAsync(payment.Id)` and the return check), `PaymentsPage.xaml(.cs)` (popup, phone and Windows), demo mirror, test `tests/GymMembership.Tests/PayAgainTests.cs`.

- [x] Failing tests: Pay again with a good code → payment made with the code, checkout opened once; a bad code → reason line, Pay online still allowed without it; cancel → nothing made; a 100% code → verified at once, no checkout (0111).
- [x] Screens with `impeccable`: Android and Windows, light/dark, 1.3 font scale. Commit.

## Phase 6: Shortage, overage and notes (D7, D8)

### Task 7: Reason on close, notes table

**Files:** migration `01xx_cash_shift_discrepancies.sql`: `cash_shifts.close_reason text check (close_reason in ('wrong_change','unrecorded_sale','counting_mistake','float_wrong','other'))`; `close_cash_shift(p_counted, p_note, p_reason)` (recreated: a difference needs a reason; `other` needs the note); `cash_shift_notes(note_id, shift_id, author_id, note text check length 1-1000, created_at)` with RLS on and no direct grants; `add_cash_shift_note(p_shift bigint, p_note text, p_resolve boolean default false)` (admins only, writes audit `cash_shift.note`); `cash_shifts.resolved_at`; `cash_shifts_list` adds `shortage`, `overage`, `close_reason`, `notes jsonb`, `resolved_at`. `supabase/tests/local/cash-shifts.mjs`, demo `DemoCashShifts.cs`, `Foundation.cs` (`reason_required`).

- [x] Failing tests: closing ₱0.01 short with no reason → `reason_required` (Review Focus 5); with a reason → shortage 0.01, overage 0; front desk calling `add_cash_shift_note` → refused; admin note → stored and audited, can't be updated or deleted; Revenue's sales total for the period doesn't change with the difference.
- [x] Migration, push (dry run first), live grants, commit. (`0117_cash_shift_discrepancies.sql` pushed live to `avxcitoddadbblxuhiik` after `--dry-run`; `revoke all ... from public, anon, authenticated` + `grant execute ... to authenticated` in `0117`.)

### Task 8: Screens

**Files:** `CashierPage.xaml` + `CashierViewModel` (reason chips under the count when it differs; `CloseReason`), `RevenuePage.xaml` + view model (Shortage and Overage columns, reason, notes, Add note / Resolved for admins), `RevenueReport.cs` (shortage and overage lines apart from sales), `Models.cs` (`CashShift` new columns), tests `tests/GymMembership.Tests/CashShiftTests.cs`.

- [x] Tests first (reason needed only when off; Add note hidden for front desk; PDF totals keep sales apart), screens with `impeccable` (Windows, light/dark). Commit.

## Phase 7: Checks

- [x] Whole test suite and database tests green; live grants checked for every migration on this branch. (1216/1216 unit tests, `desk-duplicate-sale.mjs` 3/3, `cash-shifts.mjs` 11/11, Windows & Android builds green; live grants check after live push.)
- [ ] `superpowers:requesting-code-review` on the whole branch; fixes committed.
- [ ] Owner hand checks on the real project (staff pages Windows; Pay again Android and Windows).
- [ ] Branch pushed; merge only when the owner says so.

---

# Part 2: Coach revamp (owner, 2026-11-20)

**Not built yet.** E1, E2, E12 answered by the owner (2026-11-20): **no coach rate at all; the gym pays its coaches; members never pay for a coach.** Waiting on the rest. Same branch, same rules ("How to use this plan"). Migrations start at `0118`. Coach and member screens get Android and Windows (light, dark, 1.3 font scale); front desk and admin screens are Windows only.

**Source:** owner's message 2026-11-20: coaches become staff; the coach's own rate goes; a member can hire a coach only on a package with **coach included** (a switch when making a package); the Day pass is sold **with or without a coach** (extra fee); the 10-step booking flow pasted in chat (choose amenity → date, time, length → available coaches → pick one → checked again on confirm → booking saved → QR check-in at the desk with the coach's arrival confirmed → session → damage check → payment closed once verified). Owner's follow-up: "remove the coach rate as i said … the coach payment [is] handled by the gym, members don't have to pay their rates", so the flow's rate, fee and rate-snapshot parts (steps 4, 5, 6, 10) are dropped.

## Phase table (Part 2)

| Phase | What | Needs | Status |
|---|---|---|---|
| 8 | Coach rate and coach fees removed; With a coach costs the same as Solo | E1, E2 | |
| 9 | "Coach included" switch on packages; Day pass with or without a coach | E3, E4, E5 | |
| 10 | Hiring only on a coach-included package; existing hires | E6, E7 | |
| 11 | Booking with a coach: available coaches, checked again on confirm | E8, E9 | |
| 12 | Booking QR at the desk; coach arrival; session status apart from payment | E10 | |
| 13 | Damage report after the session, reviewed by management | E11 | |
| 14 | Coach hours report for the gym's coach pay | E12 | |
| 15 | Part 2 checks, review, owner hand checks | | |

## 3. Findings: coaches today

| Item | Today | Consequence |
|---|---|---|
| Rate | Each coach sets their own `coaches.hourly_rate` on their profile (0002, 0033: positive or empty, "Rate not set"). Members see it on the Coaches page; the money is between member and coach (0072). | Removed (E1). |
| Booking with a coach | `book_amenity_for` (0064 G14, 0090) has Solo / With a coach. A coach is free when they run the amenity (`coach_amenities`), have a posted shift over the whole time (0028), and have no confirmed booking or scheduled session then; an exclusion constraint on `reservations` (`coach_id` with `tstzrange`, confirmed only) stops two confirmed bookings of one coach overlapping. The fee is the **amenity's** `coach_fee` per hour (0090), added to the price, paid to the gym. | Steps 3 and 5 of the flow already exist; the amenity's coach fee stops being charged (E2). |
| Hiring | Members send a hire request, the coach accepts (0073, 0074). An active hire unlocks time requests, sessions and messages (0021, 0031, 0051). Any member with an active membership can hire, any coach, any number (0071, 0072). | Hiring needs a coach-included package (E6). |
| Packages | `membership_packages` has name, price, `duration_days`, `is_day_pass` (0098), versions and publish; no coach flag. | New `includes_coach` switch (E3). |
| QR | Each member has a gym check-in QR (0034); bookings have no QR or code of their own. | Booking QR (E10). |
| Session status | `reservations.status` (requested, confirmed, cancelled…) mixes booking and payment; coach sessions have attendance (`record_session_attendance`, 0005, `too_soon` 0110). | Session status kept apart (E10). |
| Damage | Nothing. Amenities can be set to Maintenance on the status page. | New report (E11). |

## 4. Decisions (Part 2): recommendations, owner to answer

| # | Decision | Answer |
|---|---|---|
| E1 | **Coach rate removed.** The rate goes from the coach's profile, the Coaches page, the coach's own settings and every booking screen. `coaches.hourly_rate` is dropped with its check (0033), since nothing reads it any more. Coaches are staff; how the gym pays them is outside the app. | Owner, 2026-11-20: remove it |
| E2 | **No coach fee on bookings.** With a coach costs the same as Solo (the amenity's price per hour only). The amenity's `coach_fee` (0064/0090) is no longer charged and its box goes from the amenity admin page; the column is dropped with its grants (0064, 0109). Old app versions sending With a coach are charged nothing extra. | Owner, 2026-11-20: members don't pay coach rates |
| E3 | **Coach-included package:** a **Coach included** switch when creating or versioning a package (`membership_packages.includes_coach`). The package price already covers the coach. A member on such a package may hire a coach (E6): their own coach for the term, with time requests, sessions and messages as hires have today. Members see a "Coach included" tag on Packages. Recommended. | |
| E4 | **What coach-included gives over a plain package**, now that coached bookings cost nothing: (a) a hired coach of their own (E6) **and** no limit on upcoming coached bookings (plain members: 2, E8 item 5); (b) the hire only, same booking limit as everyone. Recommended **(a)**, so the coach-included price buys something visible. | |
| E5 | **Day pass with a coach:** the extra fee is a **package price paid to the gym**, not a coach rate. The Day pass package gets a **coach add-on price** (`coach_addon_price`, flat, set by admin). On buying (app or desk) the member picks **No coach** or **With coach (+₱x)**. With coach: they may book amenities with a coach that day. **No coach: no coached bookings that day** (otherwise the add-on buys nothing, since coached bookings are free). Recommended. | |
| E6 | **Hiring:** Hire on the Coaches page shows only to members whose active package has `includes_coach`; the database refuses others (`coach_not_included`). Accept/decline (0074) stays. A hire ends when that package's term ends (no coach-included term left → hire becomes `ended`). Recommended. | |
| E7 | **Hires already active** for members without a coach-included package: (a) end today, with a notice to both; (b) **run until that member's current term ends**, no new ones. Recommended (b): nobody loses what they paid for. | |
| E8 | **Members on a plain package (for example 1 month) and coaches: per session.** See "Per-session coaching for 1-month members" below. Recommended. | |
| E9 | **Coach taken while confirming (step 5):** the confirm re-checks inside the same transaction (the exclusion constraint already makes it race-proof). If the coach was taken: nothing is charged or saved, the message "Coach Ana was just booked for that time. Choose another coach or time." shows, and the coach list reloads with the member's amenity, day and time kept. | |
| E10 | **Booking QR and arrival (steps 7-8):** every confirmed booking gets a booking code and QR (on My bookings and the receipt). The desk scans it: the app reads the booking's **current** status from the server (cancelled, wrong day, not paid → refused with the reason). For a coached booking the desk then taps **Coach is here** (`coach_arrived_at`, audit row); until then the session isn't started. Session status is its own column: **Scheduled → Checked in → In session → Completed / No-show**, apart from the payment's status. The coach marks Completed (after the start, `too_soon` rule of 0110). Recommended. | |
| E11 | **Damage report (step 9):** after a coached session, the coach answers "Anything broken or damaged?" No / Yes with a description (and optional photo). Yes makes a `damage_reports` row (booking, amenity, coach, description, status Open / Reviewed / Closed), alerts admins, and offers **Set amenity to Maintenance**. Admins review and close with a note. **Nothing is charged automatically** to coach or member. Solo bookings: the desk can file one too. Recommended. | |
| E12 | **Paying coaches (step 10):** the gym pays its coaches; members pay nothing for a coach. The booking's own payment (the amenity) works as today and is closed once verified. To help the gym pay coaches, a **Coach hours** report on Revenue (admins) lists, per coach and date range: completed coached sessions, hours coached, no-shows, and hires active in the range. No money amounts. Recommended (the report); paying them is outside the app. | Owner, 2026-11-20: gym pays coaches |

### Per-session coaching for 1-month members (E8, recommendation)

1. **No hire.** A member on a package without a coach (1 month, 3 months, Day pass with no coach) can't hire; the Coaches page shows each coach's shifts and the amenities they run, with **Book a session** instead of Hire.
2. **Book a session = book an amenity With a coach** (flow steps 1-6). There is no coach session without an amenity and a time; the amenity booking is the coach session. This reuses `book_amenity_for` instead of building a second booking system.
3. **No coach fee.** The member pays only the amenity's price, exactly as Solo; the screen says "Coach: free with your membership".
4. **Same rules as amenity bookings:** whole hours, inside the coach's shift and gym hours, the member's membership covers the time (Day pass: ends by midnight, Q10 of 2026-11-19, and only With coach, E5), cancel 24 hours or more ahead to get the amenity payment back; later, the gym keeps it.
5. **Limit:** at most **2 upcoming coached bookings** per member at a time without a coach-included package, so one member can't reserve a coach's whole week. Coached bookings are free now, so this limit (and the 24-hour cancel rule) is what keeps coaches' time fair. Set in Gym settings (`max_coached_bookings`, default 2).
6. **Time requests and messages** (today only for hires) stay for hires only. A per-session member talks to the coach through the booking: the booking shows the coach's name, and the coach sees the member's name and notes on their calendar.
7. **The desk** books the same way for a member at the counter (Book for a customer, With a coach), with the same limit and no coach fee.
8. **No-shows count:** a member marked No-show on 2 coached bookings in 30 days can't book a coach for 7 days (the desk can still book for them). Recommended, since a free booking that's never used still blocks the coach.

## Global Constraints (Part 2)

- Everything in Part 1's Global Constraints, plus: migrations from `0118`; members are never charged for a coach (the Day pass coach add-on is a package price); no coach rate anywhere; no automatic charge from a damage report.
- Staff pages Windows only; member and coach pages Android and Windows.

## Review Focus (Part 2)

1. **Two members confirm the same coach for overlapping hours at once** → one confirmed, the other gets E9's message, nothing charged.
2. **Old app (v0.9.0) books With a coach on an amenity that had a coach fee** → charged the amenity price only, same as Solo; refunds of older bookings that did include a coach fee still return what was paid.
3. **Coach-included member's term ends while a hire is active** → hire ends; time requests and messages close; upcoming coached bookings already paid stay.
4. **Day pass with coach bought at 9 pm** → the coached hour must end by midnight; the coach add-on isn't sold if no coach has a shift left today (the buy screen says so).
5. **QR of a cancelled or refunded booking scanned** → refused with the reason, nothing recorded.

## Phase 8: No coach rate, no coach fee (E1, E2)

### Task 9: Remove the rate and the fee

**Files:** migration `0118_remove_coach_rate_and_fee.sql` (`book_amenity_for` stops adding `coach_fee`; then drop `coaches.hourly_rate` with `coaches_rate_positive`, and `amenities.coach_fee` with its grants; every function that reads either is recreated first; past payments keep their amounts), app: coach profile / settings (rate box out), Coaches page and coach cards (rate out, "Rate not set" out), amenity admin page (coach fee box out), Book an amenity and desk booking (price split without a coach line), `Models.cs` (`HourlyRate`, `CoachFee` out), demo mirror, tests `supabase/tests/local/coach-fee-removed.mjs`, `tests/GymMembership.Tests/CoachRateRemovedTests.cs`.

- [ ] Grep every reader of `hourly_rate` and `coach_fee` (migrations, Edge Functions, app, demo) before writing the migration.
- [ ] Failing tests: 2 hours With a coach costs the same as 2 hours Solo; an old-style call with a coach is charged the amenity price only (Review Focus 2); a coach is offered without any rate; refund of an older booking that included a coach fee returns its full paid amount.
- [ ] Migration, push (dry run first), live grants, screens with `impeccable`, commit.

## Phase 9: Coach-included packages and Day pass with a coach (E3, E4, E5)

### Task 10: Package switch and hours

**Files:** migration `01xx_package_includes_coach.sql` (`membership_packages.includes_coach boolean not null default false`, `coach_addon_price numeric(12,2)` for the Day pass only; `user_membership_packages.with_coach boolean` (the Day pass choice, true for any coach-included package); `avail_membership` / desk sale take `p_with_coach boolean` for a Day pass and add the add-on price), Packages admin page (switch, add-on on the Day pass), Packages (member: "Coach included" tag; Day pass: No coach / With coach (+₱x)), Cashier Day pass (same choice), demo mirror, tests `supabase/tests/local/coach-included.mjs`, `tests/GymMembership.Tests/CoachIncludedTests.cs`.

- [ ] Failing tests: coach-included package bought → `with_coach` true; Day pass With coach → price + add-on, can book with a coach that day; Day pass No coach → booking With a coach refused (`coach_not_included`); With coach refused when no coach has a shift left today (Review Focus 4); a non-Day-pass package ignores the add-on.
- [ ] Migration, push, grants, screens (`impeccable`), commit.

## Phase 10: Hiring only on coach-included (E6, E7)

### Task 11: Hire guard and hire end

**Files:** migration `01xx_hire_needs_coach_package.sql` (`request_hire` / accept refuse `coach_not_included`; a job or the term-end path sets hires `ended` when no coach-included term is left; existing hires per E7), Coaches page (Hire vs Book a session), `Foundation.cs` sentence, demo mirror, tests.

- [ ] Failing tests: plain-package member hiring → `coach_not_included`; coach-included member hires; term ends → hire ended, time request refused (Review Focus 3); existing hire on a plain package per E7.
- [ ] Migration, push, grants, screens, commit.

## Phase 11: Booking with a coach (E8, E9)

### Task 12: Available coaches, confirm

**Files:** `available_coaches(p_amenity, p_start, p_hours)` returning coach id, name and photo (reuses 0064's free-coach check), `book_amenity_for` (2-upcoming limit for non-included members per E4, no-show block per E8 item 8, Day pass needs With coach, `coach_taken` on the race), `gym_settings.max_coached_bookings`, `BookAmenityViewModel` and page (step 3 list after date/time/length; "Coach: free with your membership"; on `coach_taken` reload the list, keep the choices), desk booking same, demo mirror, tests `supabase/tests/local/coach-booking.mjs`, `tests/GymMembership.Tests/CoachBookingTests.cs`.

- [ ] Failing tests: coach with no shift or an overlapping booking not listed; two confirms for one coach → one wins, other `coach_taken`, no payment (Review Focus 1); third upcoming coached booking for a 1-month member refused, a coach-included member's allowed; 2 no-shows in 30 days → coached booking refused for 7 days, desk booking still works.
- [ ] Migration, push, grants, screens (member Android/Windows, desk Windows), commit.

## Phase 12: QR, arrival, session status (E10)

### Task 13: Booking code at the desk

**Files:** migration (`reservations.booking_code`, `session_status`, `checked_in_at`, `coach_arrived_at`; `scan_booking(p_code)` reads current state and refuses with a reason; `mark_coach_arrived(p_reservation)`; `complete_session` by the coach after start), My bookings (QR), desk scanner (reuse the member-QR scanner of 0034), coach calendar (Completed / No-show), demo mirror, tests.

- [ ] Failing tests: scanning a confirmed booking today → checked in; cancelled or not-paid → refused (Review Focus 5); session not In session until Coach is here; payment status unchanged by session changes.
- [ ] Migration, push, grants, screens, commit.

## Phase 13: Damage reports (E11)

### Task 14: Report and review

**Files:** migration (`damage_reports`, RLS: coach and desk insert own, admins read and update; `report_damage`, `review_damage`; audit rows; admin alert), coach's after-session prompt, admin Damage reports list (Windows), "Set amenity to Maintenance" reuse, demo mirror, tests.

- [ ] Failing tests: coach reports on own completed session; another coach can't; admin closes with a note; no payment or refund row made.
- [ ] Migration, push, grants, screens, commit.

## Phase 14: Coach hours report (E12)

### Task 15: Hours per coach

**Files:** `coach_hours_report(p_from, p_to)` (admins only; per coach: completed coached sessions, hours, no-shows, hires active in the range; no money), Revenue page section and CSV export, demo mirror, tests.

- [ ] Failing tests: counts completed sessions and their hours only; cancelled and no-show not in hours (no-shows counted apart); front desk can't call it.
- [ ] Migration, push, grants, screens, commit.

## Phase 15: Part 2 checks

- [ ] Whole suite and database tests green; live grants for 0118 onwards.
- [ ] `superpowers:requesting-code-review` on the Part 2 diff; fixes committed.
- [ ] Owner hand checks: coach, member (Android and Windows), desk and admin (Windows).

## Session log

- 2026-11-20: plan written from the owner's `docs (1).pdf`; branch `desk-customer-shifts-2026-11-20` cut from `origin/main` (`8c1555f`) and pushed. Waiting on D1-D8.
- 2026-11-20: owner: D1-D8 as recommended; don't build yet.
- 2026-11-20: implemented Phases 1-6 (`cb66143`..`25c3a8f`), verified 1216 unit tests, `desk-duplicate-sale.mjs` (3/3), `cash-shifts.mjs` (11/11), Windows & Android builds, and pushed `0117_cash_shift_discrepancies.sql` live to `avxcitoddadbblxuhiik`.
- 2026-11-20: owner asked for a coach revamp (coaches as staff, coach-included packages, Day pass with a coach, 10-step coach booking flow) and recommendations for per-session coaching of 1-month members: Part 2 added (Phases 8-15, E1-E12), not built. Part 1 Phases 1-6 were built by another session and are local only (not pushed; `0117` dry run only).
- 2026-11-20: owner: E1 remove the coach rate entirely, E2/E12 the gym pays coaches and members never pay for a coach. Part 2 rewritten: no rate or coach fee anywhere, Coach hours report instead of Coach fees; E4, E5 and the per-session limits reworked since coached bookings are now free. Not built.
