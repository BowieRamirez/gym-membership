# Desk amenity bookings, logs split and Buy popup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last two plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-17
Last updated: 2026-11-17
Status: BUILT 2026-11-17 (owner approved Z1 = a, native). Phases 1-6 done; `0086`-`0088` live; review fixes in `14d5350`. Next migration `0089`. Left: owner hand checks; push/PR waits on the owner.
Branch: `xendit-payments-2026-11-16` (same branch as the cash-payments plan; ask before a new one). Owner merges to `main`.

**Goal:** The owner's 6-item list of 2026-11-17: the front desk books amenities for members and for people with no account, the Day pass records the right name, the logs show the customer and how they paid, money moves get their own Transactions log apart from the audit log, and the discount code moves into a Buy popup that also asks Online or Cash at the desk.

**Architecture:** Reuse what exists: the Cashier's member search table and Register new member (`create-user`, cashier plan Y2), `amenity_grid` and `book_amenity`'s rules (0064), the cash drawer trigger (0050), the audit log page (0045, 0078, 0083), and the booking sheet + `PayOption` pattern on Book an amenity. New database work: one desk booking function and one change to the log page function.

**Tech Stack:** Supabase (Postgres migrations), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** owner's message 2026-11-17 and their sketch of the Buy popup (`Screenshot 2026-11-17 181726.png`: a popup over the Packages page with a codes box top-left, the package description, a Buy button and an X to close; after Buy, the Online / Cash choice).

## How to use this plan

Same rules as `2026-11-16-cashier-walkin-logs.md`:

- Decisions first. One phase at a time, in order; each phase ends working and committed.
- **Skills, every phase:**
  - `ponytail` full mode for the whole session (smallest diff, reuse before writing).
  - `superpowers:test-driven-development` for every rule: the failing test first, watched failing.
  - `superpowers:systematic-debugging` for the Day pass bug (Phase 1): reproduce before fixing.
  - `impeccable` (and `frontend-design` per CLAUDE.md) for every screen change. Staff pages are Windows only; member pages get Android and Windows, light and dark, and 1.3 font scale.
  - `ponytail:ponytail-review` on each phase's diff before its commit.
  - `superpowers:verification-before-completion` before any "done".
  - A fresh whole-branch review at the end (`superpowers:requesting-code-review`).
- Push each phase's migrations live when the phase is finished (dry run first) and check live grants. Nothing goes to GitHub without the owner.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Day pass records the name typed, not a member picked earlier | Z3 | DONE (`0971fd2`) |
| 2 | Packages: Buy popup with the code, the description, then Online / Cash | Z7, Z8 | DONE (`fccad21`) |
| 3 | Logs: Transactions apart from the audit log; Customer and Paid with columns | Z4, Z5, Z6 | DEPLOYED (`0086`, `2d478bb`) |
| 4 | Amenity status: book an amenity for a member at the desk | Z2 | DEPLOYED (`0087`, `3229c54`; cash only) |
| 5 | Amenity bookings for people with no account (register, then book) | Z1 | DONE (with Phase 4) |
| 6 | Whole-branch checks, review, owner hand checks | | DONE bar owner hand checks (`14d5350`, `0088`) |

## 1. Findings (2026-11-17)

| Item | Today | Consequence |
|---|---|---|
| 1 Amenity, no account | `reservations.member_id` is `not null` (0029). `book_amenity` (0064) books for `auth.uid()` only: there is no desk booking. The desk's `amenities` route is **Amenity status** (`AmenityStatusPage`: balances today, refund claims, availability). The Cashier sells kind "Amenity" to a picked member, without a time slot. | The desk needs its own booking function that takes the member. Someone with no account needs either an account first or a booking that holds just a name (a schema change). |
| 2 Day pass name | `CashierViewModel.OrderWho` and `DayPassName` (`StaffViewModels.cs:192-193`) use `Member?.Username` **before** the typed `WalkInName`. A member picked in the search table stays picked after switching to Day pass, so their name wins over what the desk typed. Also, `RecordAsync` sends the member id as `null` for a Day pass, so a picked member's Day pass is recorded under their name only, not against their account (cashier plan Task 6 meant it to be against them). | Reproduce first. The typed name must win, and a picked member must really be recorded as theirs. |
| 3 Customer in logs | `audit_log` rows (0045) hold the actor, action, entity and id, and details. A payment row's actor is the staff member (or the member, for Xendit, 0078). The customer is not shown. | Look up the customer from the row's payment (`payments.member_id` → name, or `walk_in_name`) in the log page function. |
| 4 Payment method | `payments.method` exists (`qr`, `cash`, `gcash`, ...), and Xendit rows carry `channel` in details (0078). The log shows neither as a column. | Add a "Paid with" column from the payment's method, with the Xendit channel when there is one (`AuditLabels` channel names already exist). |
| 5 Separate logs | `AuditPage` has two segments, Audit log and Archive. Money rows (`payment.*`, `refund.*`, `cash_shift.*`) are mixed with account, role and catalog changes. | A third segment, Transactions, shows only money rows; the Audit log shows the rest. |
| 6 Discount on page | `PackagesPage.xaml:64-65` has the discount code box on the page, `:95-100` the "Codes you can use" grid, and (0084) a "How you'll pay" section. Buying asks through `Confirm`. | Move the code box, the codes and the pay choice into one popup that opens when a package's Buy is tapped. |

## 2. Decisions

| # | Decision | Answer |
|---|---|---|
| Z1 | **Someone with no account books an amenity at the desk.** (a) The desk registers them as a member on Amenity status (the Cashier's Register new member: name, email, phone, temporary password), then books for them. No schema change; they can use the app later. (b) A guest booking: the desk types a name only; `reservations.member_id` becomes optional with a `guest_name`; no account is made. Recommended: (a), it reuses `create-user` and every booking rule, and the owner's words compare it to "creating an account for membership in cashier". | Owner, 2026-11-17: (a) |
| Z2 | Amenity status gets **Book for a customer**: search members (the Cashier's search table, 10 a page), pick the amenity, day and time from `amenity_grid`, optional coach and code. The desk takes the **full price** at once: cash into the open drawer (needs an open shift) or GCash / QR Ph through the desk Xendit checkout (cashier plan Y6). No down payment: they pay in person. | Claude's call |
| Z3 | Day pass: the name box is the one source. Picking a member fills the box with their full name (still editable) and records the Day pass against their account; typing over it, or switching to Day pass with an old pick, drops the pick. Empty stays "Day pass, no name". | Claude's call, from item 2 |
| Z4 | The logs show the **customer**: the payment's member (full name) or Day pass name; for a booking or refund row, the booking's member. Blank when there is none. | Claude's call, from item 3 |
| Z5 | A **Paid with** column: Cash, GCash, Maya, QR Ph, Card (Xendit's channel when known, otherwise the payment's method). Blank for rows that are not payments. | Claude's call, from item 4 |
| Z6 | **Transactions** is its own segment beside Audit log and Archive, same admins can open it. Transactions = `payment.*`, `refund.*`, `cash_shift.*`, columns: Date and time, Customer, What, Amount, Paid with, Recorded by, Action. The Audit log keeps its columns and no longer lists those rows. Each tab has its own filters and Export CSV. | Claude's call, from item 5 |
| Z7 | Packages: each package's **Buy** opens a popup (the sketch): X to close, the discount code box top-left with the member's codes as tappable chips under it, the package's description and price, and Buy. Buy turns the popup to its second step: **Online** or **Cash at the front desk** (the 0084 choice, Online first), then Pay. The code box, codes grid and "How you'll pay" leave the page. | Owner, 2026-11-17 (sketch) |
| Z8 | The popup is the booking sheet's pattern from Book an amenity (an overlay in the page), not a new library. On a phone it is a bottom sheet; on Windows a centred card. My membership's Renew and Extend open the same popup. | Claude's call |

## Global Constraints

- Next migration `0086`. Every new or recreated function revokes `public, anon, authenticated` explicitly and grants only who needs it; check live grants after each push.
- Only the front desk (and admins) may book for another member; a member calling the desk function gets `not_allowed`.
- The front desk still creates **only** member accounts (cashier plan Y2).
- Cash bookings at the desk need an open shift (`no_open_shift`, 0050).
- No IP addresses in any log (0083).
- `src/GymMembership.App/Resources/Raw/supabase.json` points at the real project: move it aside only for demo builds and put it back at once.

## Review Focus

1. **Desk books a slot someone else just took** → refused with the same clash message `book_amenity` gives; no payment is left behind.
2. **Desk books with no open shift and picks cash** → "Start a cash shift first"; nothing is booked.
3. **A log row whose payment was later voided or refunded** → Customer and Paid with still show (the original payment's), the void row is in Transactions.
4. **Member closes the Buy popup halfway** (code typed, step 2 shown) → nothing is bought; opening another package starts clean, without the old code.
5. **Day pass: pick a member, then clear the name box** → recorded as "Day pass, no name" and not against that member.

---

## Phase 1: Day pass name (Z3)

### Task 1: The typed name wins

**Files:** `src/GymMembership.Core/ViewModels/StaffViewModels.cs` (`CashierViewModel`: `OrderWho`, `DayPassName`, `RecordAsync`, `StartDeskOnlineAsync` path, `OnMemberChanged`, `OnWalkInNameChanged`), test `tests/GymMembership.Tests/DayPassNameTests.cs`. If a picked member must be recorded against them: `record_counter_payment` / `start_desk_online_payment` already take `p_member_id`; check the `walk_in` branch keeps it (database test in `supabase/tests/local/`, migration only if it drops it).

- [ ] Reproduce (systematic-debugging): in demo, pick a member, switch to Day pass, type "Juan Cruz", Record. Write down what the receipt and the log say.
- [ ] Failing tests: `Typed_name_wins_over_an_old_pick`, `Picking_a_member_fills_the_name_box`, `A_picked_member_day_pass_is_recorded_against_them`, `Clearing_the_name_box_drops_the_pick`.
- [ ] Implement; xUnit green; Windows check of the Cashier in demo; ponytail-review; commit.

## Phase 2: Buy popup (Z7, Z8)

### Task 2: The popup's state

**Files:** `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`PackagesViewModel`: `Buying` (the package or null), `BuyStep` (1 = code and description, 2 = Online / Cash), `OpenBuyCommand(package)`, `NextCommand`, `CloseBuyCommand`, `PayCommand` replacing `AvailCommand`'s confirm; `DiscountCode` and `Method` reset on open and close), `MyMembershipViewModel` Renew / Extend open the same popup, test `tests/GymMembership.Tests/BuyPopupTests.cs` (and update `CashPackageTests`, `ConfirmationTests`).

- [ ] Failing tests: `Buy_opens_the_popup_on_step_1_with_no_code`, `Next_shows_online_and_cash_online_first`, `Pay_with_cash_creates_a_desk_payment_and_closes`, `Pay_online_shows_the_pay_card`, `Closing_buys_nothing_and_clears_the_code`, `A_bad_code_keeps_the_popup_open_with_the_error`, `Tapping_a_code_chip_fills_the_box`.
- [ ] Implement; xUnit green.

### Task 3: The popup on screen

**Files:** `src/GymMembership.App/Views/PackagesPage.xaml` (+ `.cs`), `MyMembershipPage.xaml` if Renew lives there.

- [ ] impeccable on the popup, from the sketch and the booking sheet: X top-right, code box top-left with chips, description, price, Buy; step 2 the two `PayOption`s and Pay; Back on step 2. Remove the page's code box, codes grid and "How you'll pay".
- [ ] Android emulator (`-p:EmbedAssembliesIntoApk=true`) and Windows, light and dark, 1.3 font scale; ponytail-review; commit.

## Phase 3: Logs (Z4, Z5, Z6)

### Task 4: The log page function (0086)

**Files:** `supabase/migrations/0086_transactions_log.sql` (drop and recreate `audit_log_page` and the export function with `p_money boolean default false`: true lists only `payment.*`, `refund.*`, `cash_shift.*`, false only the rest; each row also returns `customer`, `amount`, `paid_with` from the row's payment, or the reservation's member), `supabase/tests/local/audit-log.mjs` (extend).

- [ ] Failing tests: a cash payment row has the member's name, amount and `cash`; a Day pass row has its typed name; a Xendit row has `paid_with` = its channel; `p_money` true lists no role change, false lists no payment; a member or anon can't call it.
- [ ] Implement; DB suite green; push 0086 (dry run first); check live grants.

### Task 5: Transactions tab

**Files:** `src/GymMembership.Core/Services/Services.cs` (`AuditRecord` gains `Customer`, `Amount`, `PaidWith`; `AuditPageAsync(filter, page, money)` and export), `AuditLabels.cs` (method names), `AuditViewModel` (`ShowMoney`, `OpenMoneyCommand`; filters per tab; CSV file `transactions-<date>.csv` with the new columns), `AuditPage.xaml`, demo gateway mirror, test `tests/GymMembership.Tests/TransactionsLogTests.cs`.

- [ ] Failing tests: `Transactions_lists_only_money_rows`, `Audit_log_no_longer_lists_payments`, `A_payment_row_shows_customer_and_paid_with`, `Export_has_the_customer_and_paid_with_columns`.
- [ ] Implement; impeccable on the Transactions table (Windows only); `Test-WindowsAdminNavigation.ps1` visits the new tab; ponytail-review; commit.

## Phase 4: Desk books amenities for members (Z2)

### Task 6: `desk_book_amenity` (0087)

**Files:** `supabase/migrations/0087_desk_book_amenity.sql` (`desk_book_amenity(p_member uuid, p_amenity bigint, p_start timestamptz, p_minutes integer, p_method payment_method, p_coach uuid default null, p_code text default null)`: front desk and admins only; the same slot, hours, clash, coach and code checks as `book_amenity`, done by sharing its body rather than copying it; full price; `cash` verified at once into the open drawer with a receipt, `qr` a pending desk Xendit payment like `start_desk_online_payment`), `supabase/tests/local/desk-booking.mjs`.

- [ ] Failing tests: the desk books for a member and the drawer grows by the price; a clash is refused; no shift + cash gives `no_open_shift` and books nothing; a member calling it gets `not_allowed`; anon has no access.
- [ ] Implement; DB suite green; push 0087; check live grants.

### Task 7: Book for a customer on Amenity status

**Files:** `ReservationService` (`DeskBookAsync(...)`), demo gateway mirror, `AmenityStatusViewModel` (search reusing `FindMembersAsync` and the Cashier's paging; picked member; amenity, day, time from `GridAsync`; method; `BookCommand`), `AmenityStatusPage.xaml`, test `tests/GymMembership.Tests/DeskBookingTests.cs`.

- [ ] Failing tests: `Desk_finds_a_member_and_books_a_slot`, `Booked_slot_shows_as_taken_on_the_grid`, `Cash_without_a_shift_says_start_one`, `GCash_opens_the_desk_checkout`.
- [ ] Implement; impeccable (Windows only); ponytail-review; commit.

## Phase 5: No account (Z1, waits on the owner)

### Task 8 (if Z1 = a): Register, then book

- [ ] Failing test: `Register_new_member_from_amenity_status_then_book` (the new member is picked after registering; the temporary password is shown once).
- [ ] Reuse the Cashier's Register new member panel on Amenity status (move it to a shared control if both pages use it); impeccable; commit.

### Task 8 (if Z1 = b): Guest booking

- [ ] New migration: `reservations.member_id` optional, `guest_name text`, check that exactly one is set; `desk_book_amenity` takes `p_guest_name`; every reader of `reservations.member_id` (grid "yours", refunds, reminders, reports) checked for null. Plan this task in detail once the owner picks it.

## Phase 6: Checks

- [ ] Full xUnit, full DB suite, Windows and Android builds, `Test-WindowsAdminNavigation.ps1`.
- [ ] Fresh whole-branch review; fix Critical / Important with a failing test first; minors to the owner.
- [ ] FOLLOW-UP (owner): hand checks: a Day pass with a typed name; buy a package through the popup with a code and cash; the Transactions tab; book an amenity for a member at the desk; a no-account booking.

## Session log

- 2026-11-17: Owner's list and Buy popup sketch received; plan written. Z1 waits on the owner.
- 2026-11-17: Owner: Z1 = (a), native. Built Phases 1-5; final review found a desk booking for a customer holding a pass booked for the desk user (fixed, `0088`); also fixed: new search drops the old pick, typed Day pass name kept. Desk bookings are cash only for now.
