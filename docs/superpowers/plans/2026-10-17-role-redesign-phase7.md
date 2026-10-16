# Phase 7: paid extensions and cancellation/refunds

## Approved rules

- Extensions only; no upgrades, downgrades or unused-time credit. Choose any active package for the next paid term. Charge its purchase-time price minus a valid discount.
- Verification/recording creates entitlement, not payment submission. Start at the later of verification and the end of the last uncancelled paid term. No automatic renewal charge.
- One **Cancel & request refund** action per paid term. Server accepts requests strictly before `verified_at + 24 hours`; exactly 24 hours is closed. Verification time, not QR-transfer time or Manila midnight, is the approved clock.
- Refund exactly the amount actually paid after discounts. No partial/prorated refund and no conversion of credits to cash.
- Pending/rejected requests leave access unchanged. Admin approval cancels only that term immediately. Other terms retain their original dates, including any resulting gap. A timely request can be approved after the submission deadline.
- Approval does not transfer money. Admin manually repays, then records method/reference/time in a separate paid transition. Keep requests, decisions and repayments auditable. No double request, approval, repayment or simultaneous void/refund.

## Technical design

`membership_terms` is the authoritative, immutable paid-window ledger, linked to the existing membership container and verified payment. Store package id/name/duration and actual paid amount as purchase snapshots; only cancellation metadata changes. Active access is a time-window query, so queued terms activate without a cron job or an open app. Existing `user_membership_packages` remains a compatibility projection for old renewal/cashier references, not the access authority.

`payments` gains nullable membership package/name/duration snapshots populated server-side when the payment is created. Existing verified history can only use the catalog data available during migration; label backfilled term snapshots as legacy, never claim historic catalog data was recovered. Backfill original and completed renewal windows; fail migration on ambiguous/overlapping history rather than silently repair paid dates. Legacy entitlement with no verified payment remains non-refundable.

`refund_requests` links a unique term and payment, stores the server-copied paid amount, member reason, pending/rejected/approved/paid state, admin decision and manual repayment details. Members read their own; admins read/manage the queue. Direct writes are forbidden. Security-definer RPCs use explicit ownership/active-account/admin checks and an empty search path. Member-row locks serialize term scheduling; payment-first locking serializes refund, verification and void. The amount is never accepted from the client.

Keep verified payment receipts intact. Report actual refund outflows on repayment date/method separately from voids, and include them in net revenue/cash totals. No actual transfer integration.

## Implementation sequence

### 1. Database foundation (first local milestone)
- [x] Add failing real PostgreSQL tests in `supabase/tests/local/phase7.mjs`: package snapshots, verification-time scheduling, different package durations, unpaid/expired checks, exact deadline, ownership/RLS, pending/rejected/approved/paid, gaps, duplication, void/refund interlocks and concurrent scheduling/decisions.
- [x] Resolve the embedded PostgreSQL Windows initialization issue without touching live Supabase or unrelated processes.
- [x] Add `supabase/migrations/0018_membership_terms_and_refunds.sql` with backfill, ledger, snapshots, protected extension/refund RPCs and compatible verification/counter/void behavior.
- [x] Run existing database rules and new Phase 7 tests; review both migration of populated history and fresh install.

### 2. Core and demo integration
- [x] Add `Models/SubscriptionModels.cs`, `Services/SubscriptionService.cs`, corresponding xUnit tests and DemoGateway support.
- [x] Make membership/access checks use canonical current terms, not aggregate end dates; show current, scheduled, expired/cancelled terms independently.
- [x] Preserve selected package snapshots in payment history and retry; never accidentally repurchase the old package for a different-package extension.
- [x] Reflect refund outflows in reports and mirror protected business rules in DemoGateway.

### 3. Member and admin screens
- [x] Update My membership: text-only package selection for an extension, original term dates/amount, combined cancellation/refund confirmation, deadline/pending/decision/repayment states and gap explanation.
- [x] Add admin-only refund queue: reason, paid amount, selected term/dates, approve/reject confirmation, separate manual repayment entry/confirmation. Do not label approval as money sent.
- [x] Use existing XAML tokens/row event handlers, accessible controls and phone/desktop layouts. Refresh access after approval and ordinary navigation.

### 4. Verification and release gate
- [x] Run full xUnit and local PostgreSQL suites; build Windows and Android (see verification evidence below).
- [ ] Inspect the actual Windows and Android screens, including light/dark, phone layout, cancellation gaps and role restrictions.
- [x] Self-review authorization, date boundaries, concurrency, historical migration, totals and duplicate-submit behavior.
- [ ] Explain migration/backfill and inspect pending migrations with owner before an approved live push. Do not run live financial tests by default.
- [ ] Obtain owner confirmation before marking Phase 7 done. Commit/push only when requested.

## Current boundary

Implementation is local only. No live database push, commit or branch push is approved for this Phase 7 work. Phase 7 remains in progress until device checks, migration review/deployment and owner confirmation are complete.

## Verification evidence

- Full xUnit suite: 441 passed, 0 failed. Includes canonical scheduling/snapshots, selected-package retry, strict 24-hour server boundary, pending/approval/manual repayment, admin queue behavior and HTTP wire contracts.
- Local `npm test`: 110 baseline database checks and 17 Phase 7 checks passed, plus populated-history migration checks (including rejection of ambiguous/overlapping history and legacy pending-payment provenance). These use temporary PostgreSQL, not live Supabase.
- Windows and Android Debug builds passed. Existing media-picker deprecation and chat-test analyzer warnings remain; no actual device/visual checks are claimed.
- Author security/correctness review and ponytail complexity review, not an independent review. Removed the unused member renewal command/discount field and unreachable old demo report/aggregate-void branches; retained the existing amenity/walk-in void path.
- Manual follow-up: verify discounted extensions, future activation without reopening the app, cancellation gaps, admin-only decisions, and separate cash/GCash/bank repayment in demo/staging; review live backfill before any production change. No real financial test is authorized by this implementation.
