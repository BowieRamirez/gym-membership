# Cashier, walk-in and logs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the Xendit plan) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-16
Status: PLANNED, waiting for the owner's OK. Next migration `0078`.
Branch: `xendit-payments-2026-11-16` (this list builds on the Xendit work; same branch, owner merges to `main` when done).

**Goal:** The owner's 6-item list of 2026-11-16: the Cashier finds members, walk-ins follow the owner's process document, logs don't show members' IP addresses, Xendit payments log the method and who paid, desk e-wallet payments go through Xendit (GCash / QR Ph), and Payments to verify is removed.

**Architecture:** Reuse what exists: the Xendit checkout and webhook (0075-0077), `record_counter_payment`'s walk-in path, the `create-user` Edge Function, and the audit log (0045). The new database work is small: who may create members, desk payments that wait for Xendit, and two audit-log changes.

**Tech Stack:** Supabase (Postgres migrations, Deno Edge Functions), .NET MAUI (Core + App), xUnit, Node database tests.

**Source:** owner's message 2026-11-16 with three screenshots and `C:\Users\bowie\Downloads\gym_walkin_member_process.md` (Walk-In, Member Registration, and Package Management Process).

## How to use this plan

Same rules as `2026-11-16-xendit-payments.md`: decisions first; one phase at a time; TDD for every rule; push each phase's migrations and Edge Functions live when it is finished (dry run first) and check live grants; `impeccable` for every screen change (staff pages are Windows only); `ponytail:ponytail-review` per phase; nothing pushed to GitHub without the owner.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Cashier member search that shows its results | Y1 | PLANNED |
| 2 | Logs: no member IPs; Xendit rows say who paid and how | Y4, Y5 | PLANNED |
| 3 | Payments to verify removed; cash bookings taken in the Cashier | Y7 | PLANNED |
| 4 | Desk e-wallet payments through Xendit (GCash / QR Ph) | Y6, Y8 | PLANNED |
| 5 | Walk-in process: Day pass, register a new member at the desk | Y2, Y3 | PLANNED |
| 6 | Whole-branch checks, review, owner hand checks | | PLANNED |

## 1. Findings (2026-11-16)

| Item | Today | Consequence |
|---|---|---|
| 1 Find | `CashierViewModel.FindAsync` (`StaffViewModels.cs:285`) fills `Hits`, which only feed a closed `Picker` ("Choose after Find", `CashierPage.xaml:127`). No list, no count, no "nothing found". `FindMembersAsync` (`Services.cs:1391`) matches the name only. Front desk can read `members`/`profiles` (0006), so it is not permissions. | The search works but looks dead. Show results as a list with a "no member found" state; also match phone and email. |
| 2 Walk-in | `record_counter_payment` kind `walk_in` (0005:297): name required, price = `gym_settings.walk_in_price`, records a `gym_check_ins` row, no account. Account creation: `create-user` Edge Function, admins only (`requireAdmin`). `profiles.phone` exists. | Scenario A mostly exists (name becomes optional). Scenario B needs the desk to create member accounts. There is no separate "member ID" number; email and phone serve for search. |
| 3 IPs | `audit()` stores `request_ip()` for every caller (0045:42); `audit_log_page` returns it. | Store no IP when the actor's main role is member; clear member IPs already stored. |
| 4 Xendit log | `confirm_online_payment` runs as service_role: `actor_id` null ("System"), role null. The Payment Session webhook has no `channel_code`, so "Channel:" is empty (seen live, payment 14). | Log the paying member as the actor (role Member), and look up the method from Xendit (the session's payment request) when confirming. |
| 5 Desk GCash | Cashier "GCash" takes a typed reference and is verified at once (`record_counter_payment`). | Desk e-wallet payments become a pending payment paid on Xendit's checkout on the desk PC, verified by the webhook. |
| 6 Payments to verify | `VerifyPaymentsPage` also takes cash for "pay at the desk" booking down payments (`ApproveText` "Take cash") and approves pre-Xendit proofs. | Move "Take cash" into the Cashier; clear any old proofs once; remove the page and its menu entry. |

## 2. Decisions

| # | Decision | Answer |
|---|---|---|
| Y1 | Cashier search: a visible results list (name, status, phone) under the search box; search by name, phone or email; "No member found" with "Register new member" (Phase 5) | Claude's call, from item 1 and the document |
| Y2 | The front desk may create **member** accounts at the Cashier (never staff): name, email, phone; a temporary password shown to hand over, changed at first sign-in | Owner, 2026-11-16 |
| Y3 | Keep today's walk-in price and check-in, renamed **Day pass**; the name becomes optional (anonymous); an existing member can be picked instead | Owner, 2026-11-16 |
| Y4 | Members' IP addresses are not stored in the log any more, and those already stored are cleared; staff IPs stay (D7) | Claude's call, from item 3 |
| Y5 | A Xendit payment is logged as the member who paid (role Member), with the method (GCash, Maya, QR Ph, card) in the details | Claude's call, from item 4 |
| Y6 | Desk e-wallet payments: Xendit's checkout opens in the desk browser; the customer scans its QR Ph (GCash, Maya, any bank app); the receipt appears when Xendit confirms | Owner, 2026-11-16 |
| Y7 | Payments to verify is removed; "pay at the desk" booking down payments are taken in the Cashier ("Bookings waiting for a down payment"); old proofs still waiting are approved or rejected once before removal | Owner, 2026-11-16 |
| Y8 | Gym settings' payment QR image and "Account name on the QR" are removed: neither members nor the desk use a static QR any more | Claude's call (asked 2026-11-16, implied by Y6) |

## Global Constraints

- Next migration `0078`. Every new or recreated function revokes `public, anon, authenticated` explicitly; check live grants after each push.
- The front desk can create **only** `member` accounts; `create-user` keeps refusing every other role for it.
- No Xendit key or token in chat, git or the app.
- Staff pages are Windows only; screen work through `impeccable`.

## Review Focus

1. **A desk e-wallet payment that is never paid** (customer walks away) → it expires like an online one; nothing is activated; the drawer is untouched.
2. **The desk registers someone who already has an account** (same email) → refused with "email already in use", and the search shows them.
3. **A Day pass with no name** → recorded, receipt printed, check-in recorded; reports show "Day pass".
4. **The member IP rule** → a member's action stores no IP; a staff member's still does.
5. **Xendit's method lookup fails** → the payment is still confirmed (the method is "unknown"), never left pending because of the log.

---

## Phase 1: Cashier member search (Y1)

### Task 1: Search results you can see

**Files:** `src/GymMembership.Core/Services/Services.cs` (`FindMembersAsync` matches name, phone, email; `MemberHit` gains `Phone`), `src/GymMembership.Core/ViewModels/StaffViewModels.cs` (`CashierViewModel`: `HasSearched`, `NoMatch`, `PickMember(MemberHit)`), `src/GymMembership.App/Views/CashierPage.xaml` (+ `.cs`), test `tests/GymMembership.Tests/CashierSearchTests.cs`. Demo mirror of `profiles.phone` if missing.

- [ ] Failing tests: `Find_by_part_of_a_name_lists_matches`, `Find_by_phone_lists_the_member`, `Find_by_email_lists_the_member`, `Find_with_no_match_says_so` (`NoMatch` true), `Picking_a_result_sets_the_member` (Order panel shows the name).
- [ ] Implement; Find also runs on Enter.
- [ ] Screen (impeccable): the Picker goes; results are a short list of rows (name, status chip, phone) under the search box, the picked row marked; "No member found" text. Windows check in demo.
- [ ] xUnit all green; Windows build; commit.

## Phase 2: Logs (Y4, Y5)

### Task 2: No member IPs (0078)

**Files:** `supabase/migrations/0078_audit_privacy_and_online_actor.sql`, `supabase/tests/local/audit-log.mjs` (extend).

- [ ] Failing tests: an action by a member stores `ip_address` null; by the desk stores it; after the migration no row whose role is member has an IP.
- [ ] `audit()` stores `request_ip()` only when `main_role(auth.uid())` is not `member`; one-off `update audit_log set ip_address = null where role = 'member'`.
- [ ] Suite green; push 0078 with Task 3.

### Task 3: Xendit rows say who paid and how

**Files:** same migration (`audit_as(p_actor uuid, ...)` used by `confirm_online_payment`, recreated), `supabase/functions/_shared/xendit.ts` (`paymentChannel(session)`: GET the session's `payment_request_id` and read its `channel_code`; null on any error), `xendit-webhook`, `xendit-checkout`, `src/GymMembership.Core/Services/AuditLabels.cs` (channel names: GCASH → GCash, PAYMAYA → Maya, QRPH → QR Ph, CARDS → Card), tests in `xendit.mjs` and `edge-rules.mjs`.

- [ ] Failing tests: the `payment.online` row's actor is the paying member, role `member`, no IP; details carry the channel; a failed channel lookup still confirms (`channel` null).
- [ ] Implement; push 0078, redeploy both functions, check grants; commit.

## Phase 3: Payments to verify removed (Y7)

### Task 4: Take cash for bookings in the Cashier; remove the page

**Files:** `CashierViewModel` + `CashierPage.xaml` ("Bookings waiting for a down payment" with Take cash, from `PaymentStaffService.PendingAsync` filtered to desk bookings), remove `VerifyPaymentsPage`, its view model, menu entry (`StaffShell`), Home tile links (`HomePage.xaml:11`), and `ProofUrlAsync`/`ViewProof`; tests.

- [ ] Before removing: count live pending payments with a proof (read-only); if any, tell the owner to approve or reject them first.
- [ ] Failing tests: the Cashier lists a pay-at-desk booking and Take cash verifies it into the open drawer; the staff menu has no Payments to verify.
- [ ] Implement; `Test-WindowsAdminNavigation.ps1` updated for the missing page (say so); Windows check; commit.

## Phase 4: Desk e-wallet through Xendit (Y6, Y8)

### Task 5: Desk checkout

**Files:** `supabase/migrations/0079_desk_online_payments.sql` (`start_desk_online_payment(p_kind, p_member_id, p_item_id, p_walk_in_name, p_discount_code)`: same checks as `record_counter_payment`, inserts a pending `qr` payment with `recorded_by` = desk and a short expiry (30 minutes)), `xendit-checkout` (the desk may open the checkout for a payment it recorded), Cashier "GCash / QR Ph" replaces "GCash" + reference box, waits and shows the receipt when paid; Gym settings QR fields removed (Y8); tests.

- [ ] Failing tests (DB): the desk's pending payment is confirmed by the webhook path and activates; never touches the cash drawer; expires unpaid; a member can't call it. (xUnit): Cashier e-wallet flow in demo pays at once and shows the receipt.
- [ ] Implement; push, deploy, grants; impeccable on the Cashier; commit.

## Phase 5: Walk-in process (Y2, Y3)

### Task 6: Day pass

- [ ] Failing tests: a Day pass with no name records and checks in ("Day pass" in reports); with a picked member it is recorded against them.
- [ ] `record_counter_payment` (new migration) accepts an empty walk-in name; the Cashier's "Walk-in" becomes "Day pass" with optional name or picked member; commit.

### Task 7: Register a new member at the desk

**Files:** `supabase/functions/create-user/index.ts` (+ `_shared/admin.ts`: the front desk may create role `member` only), Cashier "Register new member" (name, email, phone; shows the temporary password once), tests (`edge-rules.mjs` source check, xUnit demo, live check that the desk can't create a coach).

- [ ] Failing tests, implement, deploy, check live that a front desk token creating role `coach` is refused; impeccable; commit.

## Phase 6: Checks

- [ ] Full xUnit, full DB suite, Windows and Android builds, the three UI scripts (Archive already fails on `main`).
- [ ] Fresh whole-branch review; fix Critical/Important with tests.
- [ ] Owner hand checks on Windows: search, Day pass, register a member, desk GCash/QR Ph payment, cash booking, the log rows.

## Session log

- 2026-11-16: Owner's list received; Y2, Y3, Y6, Y7 answered by the owner; plan written.
