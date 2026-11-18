# Cash payments chosen in the app, verified by the front desk (2026-11-17)

## Why
Members want to pay cash for packages, not only online. Every cash payment a member picks in the app (packages and amenity
booking down payments) should wait in one place, **Payments to verify**, where the front desk approves or rejects it.
Reverses part of cashier plan Y7 (`a797e89`), which removed that page.

## Approaches
- **A. Pick the method when buying (chosen by owner).** Buy membership gets Pay online / Pay cash at the desk, like the booking sheet.
  Smallest change, same as bookings. Cost: switching methods means letting the payment expire or having the desk reject it, then buying again.
- B. Switch on the pending payment's Pay card. More flexible, but two-way switching against an open Xendit checkout is risky and bookings would differ.

## Design
- **Database (migration `0084`):** `avail_membership`, `extend_membership`, `renew_membership` take `p_method` (`'qr'` default, or `'cash'`;
  anything else `bad_input`), stored on the pending payment; expiry is the same `payment_expiry_hours`. Recreated functions
  revoke `public, anon, authenticated` explicitly, grant `authenticated`; live grants checked after push.
  `verify_payment` is unchanged: approving cash needs an open shift (trigger from 0050) and gives a receipt; reject needs a reason.
- **Member app:** the Buy membership confirm (Packages, and renew/extend on My membership) offers Pay online / Pay cash at the desk.
  Cash: "Pay ₱X in cash at the front desk; your membership starts once the desk verifies it." The Pay online card shows only for online payments;
  a pending cash payment shows "Waiting for the front desk" instead.
- **Front desk (Windows only):** `VerifyPaymentsPage` restored from `a797e89^`, trimmed to cash: pending cash payments (packages and
  amenity down payments), oldest first (a queue), each with Approve (cash into the open drawer) and Reject (reason required).
  Back in the staff menu and icon rail; Home's tile links to it. Cashier's "Bookings waiting for a down payment" moves here (removed from Cashier).
- **Unchanged:** booking balances on the day (Amenity Status), counter payments and walk-ins (Cashier), Xendit online flow.

## Non-goals
Proof uploads, GCash at the desk for app payments, switching method after buying.

## Left to the builder
Screen layout through `impeccable`; demo-mode gateway support for the new parameter; tests (member picks cash → desk sees it →
approve fills the drawer; reject frees it; no shift → `no_open_shift` message); `Test-WindowsAdminNavigation.ps1` visits the page again.
