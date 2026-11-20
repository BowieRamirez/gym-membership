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

**Not built yet.** Waiting on the owner's answers to E1-E12. Same branch, same rules ("How to use this plan"). Migrations start at `0118`. Coach and member screens get Android and Windows (light, dark, 1.3 font scale); front desk and admin screens are Windows only.

**Source:** owner's message 2026-11-20: coaches become staff; the coach's own rate goes; a member can hire a coach only on a package with **coach included** (a switch when making a package); the Day pass is sold **with or without a coach** (extra fee); the 10-step booking flow pasted in chat (choose amenity → date, time, length → available coaches → pick one, see the fee → checked again on confirm, rate locked → booking saved → QR check-in at the desk with the coach's arrival confirmed → session → damage check → payment closed once verified, no commission).

## Phase table (Part 2)

| Phase | What | Needs | Status |
|---|---|---|---|
| 8 | Coach rate set by admin, not the coach; snapshot on the booking | E1, E2 | |
| 9 | "Coach included" switch on packages; Day pass with or without a coach | E3, E4, E5 | |
| 10 | Hiring only on a coach-included package; existing hires | E6, E7 | |
| 11 | Booking with a coach: available coaches, fee shown, checked again, rate locked | E8, E9 | |
| 12 | Booking QR at the desk; coach arrival; session status apart from payment | E10 | |
| 13 | Damage report after the session, reviewed by management | E11 | |
| 14 | Coach fee payment and closure, no commission; coach fee report | E12 | |
| 15 | Part 2 checks, review, owner hand checks | | |

## 3. Findings: coaches today

| Item | Today | Consequence |
|---|---|---|
| Rate | Each coach sets their own `coaches.hourly_rate` on their profile (0002, 0033: positive or empty, "Rate not set"). Members see it on the Coaches page; the money is between member and coach (0072). | Becomes a staff rate set by admins (E1). |
| Booking with a coach | `book_amenity_for` (0064 G14, 0090) has Solo / With a coach. A coach is free when they run the amenity (`coach_amenities`), have a posted shift over the whole time (0028), and have no confirmed booking or scheduled session then; an exclusion constraint on `reservations` (`coach_id` with `tstzrange`, confirmed only) stops two confirmed bookings of one coach overlapping. The fee is the **amenity's** `coach_fee` per hour (0090), added to the price, paid to the gym. | Steps 3 and 5 of the flow already exist; the fee moves from the amenity to the coach (E2) and is saved on the booking. |
| Hiring | Members send a hire request, the coach accepts (0073, 0074). An active hire unlocks time requests, sessions and messages (0021, 0031, 0051). Any member with an active membership can hire, any coach, any number (0071, 0072). | Hiring needs a coach-included package (E6). |
| Packages | `membership_packages` has name, price, `duration_days`, `is_day_pass` (0098), versions and publish; no coach flag. | New `includes_coach` switch (E3). |
| QR | Each member has a gym check-in QR (0034); bookings have no QR or code of their own. | Booking QR (E10). |
| Session status | `reservations.status` (requested, confirmed, cancelled…) mixes booking and payment; coach sessions have attendance (`record_session_attendance`, 0005, `too_soon` 0110). | Session status kept apart (E10). |
| Damage | Nothing. Amenities can be set to Maintenance on the status page. | New report (E11). |

## 4. Decisions (Part 2): recommendations, owner to answer

| # | Decision | Answer |
|---|---|---|
| E1 | **Coach rate.** "The coach rate will be removed" is read as: the coach no longer sets or edits their own rate. The booking flow still shows "the coach's hourly rate", so **admins set each coach's hourly rate** on the staff page (coaches see it, can't change it). `coaches.hourly_rate` stays as the column, writable by admins only. Recommended. Alternative: one gym-wide coach rate in Gym settings for every coach. | |
| E2 | **Which fee a booking charges:** the coach's hourly rate × hours. The amenity's `coach_fee` (0064/0090) goes (column left, no longer read). On confirm the booking saves `coach_rate` and `coach_fee` (rate × hours); later rate changes never touch it (flow step 6). Recommended. | |
| E3 | **Coach-included package:** a **Coach included** switch when creating or versioning a package (`membership_packages.includes_coach`). The package price already covers the coach. A member on such a package may hire a coach (E6) and **books coach sessions with no coach fee** during the term. Members see a "Coach included" tag on Packages. Recommended. | |
| E4 | **How much coaching a coach-included package covers:** (a) unlimited coach bookings during the term, still limited by coach availability; (b) a number of coached hours per term set on the package (`coach_hours`, for example 8), after which the normal coach fee applies. Recommended **(b)**: a coach is a staff cost, and unlimited sessions on one monthly price can't be planned around. | |
| E5 | **Day pass with a coach:** the Day pass package gets a **coach add-on price** (`coach_addon_price`, flat, set by admin). On buying (app or desk) the member picks **No coach** or **With coach (+₱x)**. With coach makes that Day pass coach-included for the day: **one coached booking that day up to the add-on's hours** (`coach_hours`, recommended 1) with no further coach fee. No coach: they can still book a coach per session and pay the fee (E8). Recommended. | |
| E6 | **Hiring:** Hire on the Coaches page shows only to members whose active package has `includes_coach`; the database refuses others (`coach_not_included`). Accept/decline (0074) stays. A hire ends when that package's term ends (no coach-included term left → hire becomes `ended`). Recommended. | |
| E7 | **Hires already active** for members without a coach-included package: (a) end today, with a notice to both; (b) **run until that member's current term ends**, no new ones. Recommended (b): nobody loses what they paid for. | |
| E8 | **Members on a plain package (for example 1 month) and coaches: per session.** See "Per-session coaching for 1-month members" below. Recommended. | |
| E9 | **Coach taken while confirming (step 5):** the confirm re-checks inside the same transaction (the exclusion constraint already makes it race-proof). If the coach was taken: nothing is charged or saved, the message "Coach Ana was just booked for that time. Choose another coach or time." shows, and the coach list reloads with the member's amenity, day and time kept. | |
| E10 | **Booking QR and arrival (steps 7-8):** every confirmed booking gets a booking code and QR (on My bookings and the receipt). The desk scans it: the app reads the booking's **current** status from the server (cancelled, wrong day, not paid → refused with the reason). For a coached booking the desk then taps **Coach is here** (`coach_arrived_at`, audit row); until then the session isn't started. Session status is its own column: **Scheduled → Checked in → In session → Completed / No-show**, apart from the payment's status. The coach marks Completed (after the start, `too_soon` rule of 0110). Recommended. | |
| E11 | **Damage report (step 9):** after a coached session, the coach answers "Anything broken or damaged?" No / Yes with a description (and optional photo). Yes makes a `damage_reports` row (booking, amenity, coach, description, status Open / Reviewed / Closed), alerts admins, and offers **Set amenity to Maintenance**. Admins review and close with a note. **Nothing is charged automatically** to coach or member. Solo bookings: the desk can file one too. Recommended. | |
| E12 | **Paying the coach fee (step 10), no commission:** the coach fee is paid **to the gym with the booking**, the same payment (online through Xendit, or cash at the desk), shown as its own line "Coach fee". The booking is closed only once the payment is verified (as today). The gym keeps nothing of it: a **Coach fees** report on Revenue lists each coach's fees from verified, completed sessions for a date range, for the gym to pay out in full. Refunds of a cancelled coached booking return the coach fee too. Recommended. Alternative: the member pays the coach directly and the desk only records it; harder to verify and refund, so not recommended now that coaches are staff. | |

### Per-session coaching for 1-month members (E8, recommendation)

1. **No hire.** A member on a package without a coach (1 month, 3 months, Day pass with no coach) can't hire; the Coaches page shows each coach's rate, shifts and the amenities they run, with **Book a session** instead of Hire.
2. **Book a session = book an amenity With a coach** (flow steps 1-6). There is no coach session without an amenity and a time; the amenity booking is the coach session. This reuses `book_amenity_for` instead of building a second booking system.
3. **Fee per session**, rate × hours, shown before confirming, paid with the booking (online in the app, or cash/online at the desk). Coach-included members see "Coach included (3 of 8 hours left)" instead of a fee (E4).
4. **Same rules as amenity bookings:** whole hours, inside the coach's shift and gym hours, the member's membership covers the time (Day pass: ends by midnight, Q10 of 2026-11-19), cancel 24 hours or more ahead to get the whole payment (amenity + coach fee) back; later, the gym keeps it.
5. **Limit:** at most **2 upcoming coached bookings** per member at a time without a coach-included package, so one member can't reserve a coach's whole week. Owner may change the number.
6. **Time requests and messages** (today only for hires) stay for hires only. A per-session member talks to the coach through the booking: the booking shows the coach's name, and the coach sees the member's name and notes on their calendar.
7. **The desk** books the same way for a member at the counter (Book for a customer, With a coach), with the same fee and limit.

## Global Constraints (Part 2)

- Everything in Part 1's Global Constraints, plus: migrations from `0118`; the rate and fee saved on a booking never change after confirm; no automatic charge from a damage report; the gym takes no commission from a coach fee.
- Staff pages Windows only; member and coach pages Android and Windows.

## Review Focus (Part 2)

1. **Two members confirm the same coach for overlapping hours at once** → one confirmed, the other gets E9's message, nothing charged.
2. **Admin raises a coach's rate after a booking is confirmed** → the booking, its payment, refund and the Coach fees report keep the old rate.
3. **Coach-included member's term ends while a hire is active** → hire ends; time requests and messages close; upcoming coached bookings already paid stay.
4. **Day pass with coach bought at 9 pm** → the coached hour must end by midnight; the coach add-on isn't sold if no coach has a shift left today (the buy screen says so).
5. **QR of a cancelled or refunded booking scanned** → refused with the reason, nothing recorded.

## Phase 8: Coach rate (E1, E2)

### Task 9: Admin sets the rate, booking keeps it

**Files:** migration `0118_coach_rate_staff.sql` (coaches can't update `hourly_rate`; admins can, through the staff page; `reservations.coach_rate numeric(12,2)`, `reservations.coach_fee numeric(12,2)`, filled in `book_amenity_for` from the coach's rate × hours; amenity `coach_fee` no longer read), coach profile page (rate read-only), staff/coaches admin page (rate box), `Models.cs`, demo mirror, tests `supabase/tests/local/coach-rate.mjs`, `tests/GymMembership.Tests/CoachRateTests.cs`.

- [ ] Failing tests: coach updating own rate → refused; admin sets it; booking 2 hours with a ₱300 coach → `coach_fee` 600; rate changed after → booking still 300/600 (Review Focus 2); a coach with no rate isn't offered.
- [ ] Migration, push (dry run first), live grants, screens with `impeccable`, commit.

## Phase 9: Coach-included packages and Day pass with a coach (E3, E4, E5)

### Task 10: Package switch and hours

**Files:** migration `01xx_package_includes_coach.sql` (`membership_packages.includes_coach boolean not null default false`, `coach_hours int` (E4 b), `coach_addon_price numeric(12,2)` for the Day pass only; `user_membership_packages.coach_hours_left`; `avail_membership` / desk sale take `p_with_coach boolean` for a Day pass and add the add-on price), Packages admin page (switch, hours, add-on on the Day pass), Packages (member: "Coach included" tag; Day pass: No coach / With coach (+₱x)), Cashier Day pass (same choice), demo mirror, tests `supabase/tests/local/coach-included.mjs`, `tests/GymMembership.Tests/CoachIncludedTests.cs`.

- [ ] Failing tests: coach-included package bought → hours left = package hours; Day pass With coach → price + add-on, 1 coached hour; With coach refused when no coach has a shift left today (Review Focus 4); a non-Day-pass package ignores the add-on.
- [ ] Migration, push, grants, screens (`impeccable`), commit.

## Phase 10: Hiring only on coach-included (E6, E7)

### Task 11: Hire guard and hire end

**Files:** migration `01xx_hire_needs_coach_package.sql` (`request_hire` / accept refuse `coach_not_included`; a job or the term-end path sets hires `ended` when no coach-included term is left; existing hires per E7), Coaches page (Hire vs Book a session), `Foundation.cs` sentence, demo mirror, tests.

- [ ] Failing tests: plain-package member hiring → `coach_not_included`; coach-included member hires; term ends → hire ended, time request refused (Review Focus 3); existing hire on a plain package per E7.
- [ ] Migration, push, grants, screens, commit.

## Phase 11: Booking with a coach (E8, E9)

### Task 12: Available coaches, fee, confirm

**Files:** `available_coaches(p_amenity, p_start, p_hours)` returning coach, name, rate, fee (reuses 0064's free-coach check), `book_amenity_for` (coach fee or included hours, 2-upcoming limit for non-included members, `coach_taken` on the race), `BookAmenityViewModel` and page (step 3 list after date/time/length; fee or "Coach included (n of m hours left)"; on `coach_taken` reload the list, keep the choices), desk booking same, demo mirror, tests `supabase/tests/local/coach-booking.mjs`, `tests/GymMembership.Tests/CoachBookingTests.cs`.

- [ ] Failing tests: coach with no shift or an overlapping booking not listed; fee = rate × hours shown before confirm; two confirms for one coach → one wins, other `coach_taken`, no payment (Review Focus 1); included member uses hours, no fee; third upcoming coached booking for a 1-month member refused.
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

## Phase 14: Coach fee payment and report (E12)

### Task 15: Fee line, refund, report

**Files:** payment/receipt "Coach fee" line, refunds include the coach fee, `coach_fees_report(p_from, p_to)` (admins; verified payments of completed coached sessions, by coach, rate snapshot), Revenue page section and PDF line kept apart from sales, demo mirror, tests.

- [ ] Failing tests: report sums snapshot fees for completed sessions only; cancelled-with-refund excluded; no commission taken (fee in = fee listed).
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
