# Desk customer picking, payment screen and cash shift discrepancies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-20
Last updated: 2026-11-20
Status: PLANNED. Waiting on the owner's answers to D1-D8 before Phase 1.
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
| 1 | Bug: can't choose a customer on Book for a customer | | |
| 2 | Customer box: suggestions while typing, no Find button (Cashier and Book for a customer) | D1 | |
| 3 | Status re-read on pick; Cashier table filtered by status, amenities show everyone; no duplicate sale | D2, D3, D4 | |
| 4 | Cashier payment screen: the customer is obvious | D5 | |
| 5 | Pay again: straight to paying, with a discount code | D6 | |
| 6 | Cash shift: shortage / overage, reason, supervisor notes, audit | D7, D8 | |
| 7 | Whole-branch checks, review, owner hand checks | | |

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
| D1 | **Customer box (Cashier and Book for a customer):** typing 2 or more letters shows up to 8 suggestions under the box (name, then email/phone in grey, and the status chip), 300 ms after the last key, newest search wins. Arrow keys / click choose one; choosing fills the box with the name and picks the customer. The **Find** button goes; Enter picks the top suggestion. The Cashier's table under it stays and follows the same filter (the owner's screenshot shows it); Book for a customer shows suggestions only, no table. The "dropdown beside customer" in the list is read as this suggestion list; a separate dropdown of all members would be hundreds of names. Recommended. | |
| D2 | **Cashier table filter, by kind:** **Membership** and **Day pass** hide anyone with an active membership or a valid Day pass (PDF §3). **Renewal** shows only people with a membership (the opposite: they're the ones who renew). **Amenity** and **Book for a customer** show everyone (PDF §3 Amenities). A hidden person typed by name gets one line: "Lilian Sabado already has a membership until 16 Dec 2026." so staff aren't left guessing. Recommended. | |
| D3 | **Status re-read on pick:** picking a customer (suggestion or row) re-reads that one member from the server (`member_status(p_member uuid)` or `FindMembersAsync` by id, whichever already returns it) and updates the chip; the pick stays. If the fresh status now makes them ineligible for the kind (bought a membership in the app a minute ago), the order says so and Complete payment is off; the pick is not cleared. Statuses: **Member until <date>**, **Day pass until midnight**, **No membership**. Expired terms are No membership. | |
| D4 | **No duplicate sale:** the database refuses a desk membership or Day pass sale to a member who already has an active membership or Day pass (`already_member`), whatever the app shows. Renewal unaffected. Only built if the Phase 3 test shows it isn't refused today. | |
| D5 | **Payment screen (Cashier order panel):** top of the panel is the customer: full name large (the page's biggest text after the total), status chip, email and phone under it, and a **Change** link. The chosen row in the table gets the selected background and a check. Before anyone is picked the panel says "Choose a customer". Order lines, method and code sit under a divider. The Record confirm already names the customer (`OrderWho`). | |
| D6 | **Pay again:** a small popup: what it is, the price, a **Discount code** box with the live check (same line as Packages: "Code works: 10% off. You pay ₱1,350.00."), and **Pay online**. Pay online makes the payment with the code and opens the Xendit checkout at once (the "auto redirect"), then checks it on return as Packages does. Cancel makes nothing. Recommended. Alternative: send the member to Packages with the Buy popup opened for that package. | |
| D7 | **Shift close reason ("options in the UI"):** when the count differs, the cashier picks a reason: **Wrong change given**, **Sale not recorded**, **Counting mistake**, **Float was wrong**, **Other**; the note box stays and is required for Other. Stored as `close_reason`. Recommended. | |
| D8 | **Supervisor investigation notes:** "supervisor" = admin and system admin (front desk can't). On the Revenue page's shift table, a shift that's short or over shows **Shortage ₱x** or **Overage ₱x** in its own column, the reason, the note, and **Add note**. Notes are append-only (`cash_shift_notes`: shift, author, note, time), never edited or deleted; each one writes an audit row `cash_shift.note`. A shift can be marked **Resolved** with a last note. Shortages and overages are totalled on their own line in Revenue and the PDF, never added to or taken from sales. Recommended. | |

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

- [ ] Reproduce (systematic-debugging): demo build, front desk, Amenity status → Book for a customer → search → Choose. Note whether `PickMember` runs and what clears `Member`. Same on the Cashier for comparison.
- [ ] Failing test for the root cause found (for example: `Booking.PickMemberCommand.Execute(hit)` then a status refresh → `Booking.Member == hit`).
- [ ] Fix at the root, tests pass, `ponytail:ponytail-review`, commit.

## Phase 2: Suggestions as you type (D1)

### Task 2: One search behaviour for both screens

**Files:** `StaffViewModels.cs` (`CashierViewModel` and `DeskBookingViewModel`: `OnSearchChanged` → debounced `SuggestAsync`, `Suggestions` collection, `ChooseSuggestion(MemberHit)`; `Find` command removed), a small shared helper only if both copies would be identical (`MemberSuggest` in `StaffViewModels.cs`), `CashierPage.xaml`, `AmenityStatusPage.xaml` (suggestion list under the box, Find button out), demo mirror untouched (search already demoed), test `tests/GymMembership.Tests/MemberSuggestTests.cs`.

- [ ] Failing tests: one letter → no search; "li" then "lil" within 300 ms → one search for "lil"; an old slow answer never replaces a newer one (Review Focus 4); choosing a suggestion picks the member and fills the box; Enter picks the top one.
- [ ] Build, screens with `impeccable` (Windows, light/dark), commit.

## Phase 3: Fresh status and the filter (D2, D3, D4)

### Task 3: Status on pick, filter by kind

**Files:** `Services.cs` (`MemberHit` gets `DayPassUntil` or a `StatusText`; `MemberStatusAsync(string memberId) -> AppResult<MemberHit>`; reuse the search query by id if it already returns these), `CashierViewModel` (`Visible(MemberHit, kind) -> bool` per D2; `PickMember` re-reads status, keeps `Member`; `HiddenHint`), `DeskBookingViewModel` (re-read on pick, no filter), `CashierPage.xaml` (status chip per row, hidden-hint line), tests `tests/GymMembership.Tests/CashierFilterTests.cs`.

- [ ] Failing tests: Membership and Day pass kinds hide an active member and a Day pass holder, show a lapsed one; Renewal shows only members; Amenity and Book for a customer show all; pick re-reads and keeps the pick through a refresh (Review Focus 1, 3); a stale "No membership" becomes "Member until …" on pick and Complete payment turns off.
- [ ] Code, tests pass, commit.

### Task 4: The database refuses a duplicate sale (D4)

**Files:** `supabase/tests/local/desk-duplicate-sale.mjs`; only if it fails: migration `0117_desk_no_duplicate_membership.sql` (the desk sale function refuses `already_member` for Membership / Day pass when `is_active_member`), `Foundation.cs` sentence if missing, demo mirror.

- [ ] Test (Review Focus 2): desk sells Membership to a member with an active term → `already_member`, nothing written; Renewal for the same member still works.
- [ ] If red: migration, push (dry run first), live grants, commit.

## Phase 4: Payment screen (D5)

### Task 5: The customer at the top of the order

**Files:** `CashierPage.xaml` (order panel header, chosen row style), `CashierViewModel` (`OrderWhoStatus`, `ChangeCustomer` command clearing the pick on purpose), test in `CashierFilterTests.cs` (Change clears; nothing else does).

- [ ] Test first, then the panel with `impeccable` (Windows, light/dark): name large, status chip, contact, Change; chosen row marked; "Choose a customer" empty state. Commit.

## Phase 5: Pay again (D6)

### Task 6: Code and straight to Xendit

**Files:** `Services.cs` (`PayAgainAsync(PaymentItem item, string? code)` passing the code to `ExtendAsync` / `AvailAsync` / `RenewAsync`), `MyPaymentsViewModel` (`PayAgainCode`, live check reusing `CheckCodeAsync` + `CodeCheckFeedback` as in `PackagesViewModel`, then `PayOnlineAsync(payment.Id)` and the return check), `PaymentsPage.xaml(.cs)` (popup, phone and Windows), demo mirror, test `tests/GymMembership.Tests/PayAgainTests.cs`.

- [ ] Failing tests: Pay again with a good code → payment made with the code, checkout opened once; a bad code → reason line, Pay online still allowed without it; cancel → nothing made; a 100% code → verified at once, no checkout (0111).
- [ ] Screens with `impeccable`: Android and Windows, light/dark, 1.3 font scale. Commit.

## Phase 6: Shortage, overage and notes (D7, D8)

### Task 7: Reason on close, notes table

**Files:** migration `01xx_cash_shift_discrepancies.sql`: `cash_shifts.close_reason text check (close_reason in ('wrong_change','unrecorded_sale','counting_mistake','float_wrong','other'))`; `close_cash_shift(p_counted, p_note, p_reason)` (recreated: a difference needs a reason; `other` needs the note); `cash_shift_notes(note_id, shift_id, author_id, note text check length 1-1000, created_at)` with RLS on and no direct grants; `add_cash_shift_note(p_shift bigint, p_note text, p_resolve boolean default false)` (admins only, writes audit `cash_shift.note`); `cash_shifts.resolved_at`; `cash_shifts_list` adds `shortage`, `overage`, `close_reason`, `notes jsonb`, `resolved_at`. `supabase/tests/local/cash-shifts.mjs`, demo `DemoCashShifts.cs`, `Foundation.cs` (`reason_required`).

- [ ] Failing tests: closing ₱0.01 short with no reason → `reason_required` (Review Focus 5); with a reason → shortage 0.01, overage 0; front desk calling `add_cash_shift_note` → refused; admin note → stored and audited, can't be updated or deleted; Revenue's sales total for the period doesn't change with the difference.
- [ ] Migration, push (dry run first), live grants, commit.

### Task 8: Screens

**Files:** `CashierPage.xaml` + `CashierViewModel` (reason chips under the count when it differs; `CloseReason`), `RevenuePage.xaml` + view model (Shortage and Overage columns, reason, notes, Add note / Resolved for admins), `RevenueReport.cs` (shortage and overage lines apart from sales), `Models.cs` (`CashShift` new columns), tests `tests/GymMembership.Tests/CashShiftTests.cs`.

- [ ] Tests first (reason needed only when off; Add note hidden for front desk; PDF totals keep sales apart), screens with `impeccable` (Windows, light/dark). Commit.

## Phase 7: Checks

- [ ] Whole test suite and database tests green; live grants checked for every migration on this branch.
- [ ] `superpowers:requesting-code-review` on the whole branch; fixes committed.
- [ ] Owner hand checks on the real project (staff pages Windows; Pay again Android and Windows).
- [ ] Branch pushed; merge only when the owner says so.

## Session log

- 2026-11-20: plan written from the owner's `docs (1).pdf`; branch `desk-customer-shifts-2026-11-20` cut from `origin/main` (`8c1555f`) and pushed. Waiting on D1-D8.
