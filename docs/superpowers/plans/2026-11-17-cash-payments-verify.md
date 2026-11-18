# Cash Payments Verified by the Desk Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Members can pick cash for a package; every cash payment picked in the app (packages and booking down payments) waits on a restored **Payments to verify** page where the desk approves (cash into the drawer) or rejects (with a reason).

**Architecture:** One migration adds `p_method` to the three package entry points; they call the unchanged `create_membership_extension` and switch the new payment to cash. `verify_payment` and the 0050 shift trigger already handle cash. The app restores `VerifyPaymentsPage`/`VerifyPaymentsViewModel` from `a797e89^`, trimmed to cash, and moves Cashier's desk-bookings list onto it.

**Tech Stack:** .NET MAUI (Windows staff, Android + Windows members), CommunityToolkit.Mvvm, Supabase Postgres, xUnit with `DemoGateway`.

**Spec:** `docs/superpowers/specs/2026-11-17-cash-payments-verify-design.md`

**Branch:** stay on `xendit-payments-2026-11-16` (owner: ask before new branches).

## Global Constraints

- Migration `0084`. Every recreated function revokes `public, anon, authenticated` explicitly, then grants `authenticated`; check live grants after the push. Push live when the phase is done (owner standing rule).
- `p_method` accepts only `'qr'` (default, online) or `'cash'`; anything else → `bad_input`.
- Staff pages are Windows only. Member Packages page: phone layout checked light/dark and at 1.3 font scale. Screen work through `impeccable`.
- No IP addresses in logs; no Xendit keys anywhere.

## Review Focus

1. **Approve with no open shift** → the `no_open_shift` message ("Start your cash shift on the Cashier page first…"), payment stays pending. Test in Task 3.
2. **A cash payment past its expiry** → desk cannot approve it (`payment_expired`), member can Pay again. Test in Task 1 (demo) for packages.
3. **Member already has a pending package payment and picks cash** → `renewal_pending`, nothing created. Covered by the existing server rule; Task 2 test asserts the message.
4. **An old v0.7.0 client calls `extend_membership` without `p_method`** → still online (`qr`). Task 1 SQL default; local SQL test.
5. **Desk member verifying their own cash** → `self_verify` refusal unchanged. No new test (existing).

---

### Task 1: Database and service: cash for packages

**Files:**
- Create: `supabase/migrations/0084_cash_for_packages.sql`
- Modify: `src/GymMembership.Core/Services/SubscriptionService.cs:110,151` (`ExtendAsync`), `src/GymMembership.Core/Services/Services.cs:315-316,403-421` (`AvailAsync`, `RenewAsync`, `PayAgainAsync`)
- Modify: `src/GymMembership.Core/Demo/DemoGateway.cs:804-829` and the `CreateExtension` helper
- Test: `tests/GymMembership.Tests/PaymentTests.cs`

**Interfaces:**
- Produces: `ISubscriptionService.ExtendAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr)`; `IMembershipService.AvailAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr)`, `RenewAsync(int membershipId, string? discountCode = null, string method = PayMethod.Qr)`. RPC arg `p_method` sent only when method is cash (old servers untouched otherwise). `PayAgainAsync` passes `item.Method`.

- [ ] **Step 1: Failing tests** in `PaymentTests.cs`:
  - `Extending_with_cash_creates_a_pending_cash_payment`: demo member `ExtendAsync(pkg, null, PayMethod.Cash)` → `Method == "cash"`, `Status == "pending"`, `ExpiresAt` set.
  - `Pay_again_keeps_cash`: a rejected cash membership payment, `PayAgainAsync(item)` → new payment `Method == "cash"`.
  - `Unknown_method_is_refused`: `ExtendAsync(pkg, null, "gcash")` → fails `bad_input`.
- [ ] **Step 2:** `dotnet test --filter PaymentTests` → FAIL (no such parameter).
- [ ] **Step 3: Migration.** Drop and recreate `extend_membership`, `avail_membership`, `renew_membership` with `p_method public.payment_method default 'qr'` as last parameter; bodies as in 0018/0061 (renew keeps 0061's `newest_package_version`), then: `if p_method not in ('qr','cash') then fail('bad_input','22023')`; after `create_membership_extension`, `if p_method = 'cash' then update public.payments set method = 'cash' where payment_id = v_pay.payment_id returning * into v_pay; end if;`. Revoke/grant per constraints.
- [ ] **Step 4:** Services pass `p_method` when cash; demo gateway reads `Opt("p_method") ?? "qr"`, rejects other values with `bad_input`, sets `Method` on the created payment.
- [ ] **Step 5:** `dotnet test` → all PASS.
- [ ] **Step 6:** Commit `Packages can be paid in cash at the desk (0084)`.

### Task 2: Member Packages page: Online / Cash at the desk

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs:210-300` (`PackagesViewModel`), `src/GymMembership.Core/Services/Services.cs:285-305` (`PaymentItem`)
- Modify: `src/GymMembership.App/Views/PackagesPage.xaml` (method chips by the discount code; desk card beside the Pay online card; rules text)
- Test: `tests/GymMembership.Tests/ConfirmationTests.cs`, `OnlinePaymentTests.cs`

**Interfaces:**
- Consumes: Task 1 `ExtendAsync(..., method)`.
- Produces: `PackagesViewModel.Method` (string, default `PayMethod.Qr`), `PickMethodCommand(string)`, `PayCash` bool, `PayAtDesk` (`PaymentItem?`, pending cash membership/renewal); `PaymentItem.PaysAtDesk => Status == Pending && Method == Cash`; `Detail` for it: `"Pay at the front desk"`.

- [ ] **Step 1: Failing tests:**
  - `Buying_with_cash_asks_with_desk_wording`: `Method = Cash`, `AvailCommand` → confirm text contains `"in cash at the front desk"` and button `"Buy membership"`; on yes the created payment's method is cash, `PayNow` null, `PayAtDesk` not null, Notice `"Payment created. Pay ₱X in cash at the front desk; your membership starts once the desk verifies it."`.
  - `Online_stays_the_default`: no pick → `ExtendAsync` called with `PayMethod.Qr`.
- [ ] **Step 2:** run → FAIL.
- [ ] **Step 3:** Implement; chips use the existing `ChoiceChip` like the booking sheet; desk card: title `Pay ₱X at the front desk`, line `Show payment code {code}. Your membership starts once the desk verifies it.`, deadline line as Pay online card, Cancel as today. Rules text: "Pay online on this page by GCash or QR Ph, or in cash at the front desk."
- [ ] **Step 4:** `dotnet test` PASS; Windows and Android emulator check light/dark, 1.3 font scale (screenshots).
- [ ] **Step 5:** Commit `Packages: pay online or in cash at the front desk`.

### Task 3: Payments to verify comes back (Windows)

**Files:**
- Create (restore from `a797e89^`, trimmed): `src/GymMembership.App/Views/VerifyPaymentsPage.xaml(.cs)`, `VerifyPaymentsViewModel` in `StaffViewModels.cs` (no proof viewer, no claims)
- Modify: `StaffShell.xaml` (route `verify`, after Home), `StaffShell.xaml.cs` (short name `Verify`), `Controls/IconRail.Windows.cs` (`badge-check`), `HomePage.xaml(.cs)` (title/button "Verify", opens `verify`), `StaffViewModels.cs:76` (tile back to route `verify`, "Payments to verify", caption "N payments are waiting"), `Services.cs` `MemberCheck.NextStep` pending → `("Open Payments to verify","verify")`
- Remove: Cashier's "Bookings waiting for a down payment" (`CashierPage.xaml:129-150`, `CashierPage.xaml.cs` handler, `CashierViewModel.DeskBookings/TakeBookingCash`, `StaffViewModels.cs:170-186`)
- Modify: `scripts/Test-WindowsAdminNavigation.ps1` (add `verify` back), tests `ConfirmationTests.cs`, `ReservationTests.cs`, `DeskHomeTests.cs`, `MembershipCheckingTests.cs`, `WindowsAdminNavigationTests.cs` (count +1), `CashierTests.cs`

**Interfaces:**
- Consumes: `IPaymentStaffService.PendingAsync()` (already pending cash only), `VerifyAsync(id, approve, reason)`.
- Produces: `VerifyPaymentsViewModel(IPaymentStaffService svc)`: `Pending`, `HasPending`, `ApproveCommand(PaymentRow)`, `RejectCommand`, `ConfirmRejectCommand`, `KeepPendingCommand`, `Rejecting`, `RejectReason`.

- [ ] **Step 1: Failing tests** (move the Cashier ones to the new view model):
  - `Approving_cash_asks_with_payer_and_amount` (yes/no): confirm `"Take {payer}'s cash?"`, `"…₱X into your drawer."`, button `"Approve"`; yes → verified, receipt, row gone, drawer cash in += amount.
  - `Approving_without_a_shift_says_start_one`: no open shift → `ErrorMessage` equals the `no_open_shift` text, row stays.
  - `Rejecting_needs_a_reason`: empty reason refused; with reason → `Rejected`, member notified, row gone.
  - `Lists_package_and_booking_cash_together`: a cash package and a cash booking both in `Pending`; an online pending payment not.
  - Staff menu has `Payments to verify`; Cashier has no `DeskBookings`.
- [ ] **Step 2:** run → FAIL.
- [ ] **Step 3:** Implement; page layout through `impeccable` (table like Home's, row: payer, item, booking when any, code, amount, Approve / Reject; reject reason inline as before).
- [ ] **Step 4:** `dotnet test` PASS; `scripts/Test-WindowsAdminNavigation.ps1` PASS; Windows check light/dark.
- [ ] **Step 5:** Commit `Payments to verify is back: all cash members chose in the app, approve or reject`.

### Task 4: Push and finish

- [ ] **Step 1:** `supabase db push` (0084); check live grants on the three functions (`anon` has none).
- [ ] **Step 2:** Live check: member buys with cash → desk approves → term starts; old-client call without `p_method` → `qr`.
- [ ] **Step 3:** `ponytail:ponytail-review` on the branch diff; fix; commit. Update memory/plan status; push branch when owner says.
