# Membership refunds: only the unused days (short design)

> **Changed the same day by G23 (0068):** the halfway cutoff is replaced by a **7-day window from the term's start** (owner: "so it won't
> hurt the business"); a term bought ahead can be cancelled any time before it starts. The amount rule below is unchanged.

Date: 2026-11-15. Owner decision G22 in `docs/superpowers/plans/2026-11-12-owner-fixes.md`. Changes D9/D21 (2026-11-05): a refund is no longer
always the full payment.

## Why
A member who cancels early was refunded the whole payment (₱1,500 back with 28 of 30 days left, on 2026-11-15). The gym should keep the
days already given, and nothing once half the term has gone.

## The rule (owner, 2026-11-15)
- **Refund = what was paid × days left ÷ days in the term**, counted **when staff approve** (access ends then). 30 days for ₱1,500, 7 used:
  23 × ₱50 = ₱1,150. A term that hasn't started gives everything back.
- **Half used = no refund.** A member can ask only while fewer than half the days have passed (15 of 30 is already too late).
- **Asking in time is what counts.** A request made before the halfway point can still be approved after it; the refund is the days left at
  approval.
- Unchanged: a term with a check-in still needs a reason and staff review (D21); the app sends no money; requests already approved or repaid
  keep their amounts.

## Approaches
1. **In the database (chosen by the owner).** `request_membership_refund` refuses at or after half (`refund_half_used`) and saves an
   estimate; `decide_membership_refund` sets the final amount on approval; `record_membership_refund_paid` pays the saved amount. Demo mirrors.
2. In the app only: rejected, an old app or a direct call would still refund in full; rules live in the database here.
3. A separate "refund due" column: not needed, the request's `amount` becomes the refund; the payment keeps its price (voids, revenue,
   receipts unchanged).

## Builder's calls (owner can correct)
- **A started day counts as used:** days used = whole days since the term started, rounded up; days left = term length − days used.
  Amount rounded to centavos. "What was paid" is the payment amount (after any discount code).
- **Half:** refused when days used × 2 ≥ term length.
- **Approval after the term has fully ended** (₱0 left): refused (`nothing_to_refund`); staff reject it instead.
- The two checks that required amount = payment become amount between ₱0.01 and the payment.
- **Screens:** the member's Cancel & request refund says the rough amount and the last day to ask; staff see "Estimated refund" until they
  approve, then the final amount. Copy through `impeccable clarify`.
- Migration `0067`, tests first (database file + xUnit), pushed live when done, live grants checked.
