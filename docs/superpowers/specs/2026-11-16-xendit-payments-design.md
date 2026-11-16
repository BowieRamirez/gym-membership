# Xendit online payments — design

Date: 2026-11-16 · Branch: `xendit-payments-2026-11-16` · Status: waiting for owner approval

## Why

Today a member who buys online (a membership, a package, or an amenity down payment) pays the gym's GCash QR, uploads a
screenshot and a reference number, and the front desk checks it by hand (`submit_payment_proof`, `verify_payment`, 0005/0018).
The owner wants the money to come in through Xendit instead, so a payment is confirmed the moment it is made, with no proof
screenshots and no desk review.

## Goals

- Members pay online through Xendit for **memberships (new and renewal), packages, and amenity down payments**.
- A paid Xendit payment marks our payment **verified**, gives it a receipt number, and activates what was bought, through the
  same `activate_payment_target` the desk approval uses today. No staff step.
- The GCash-proof upload is **gone** for new payments.

## Non-goals

- Counter payments (cash or GCash at the desk, walk-ins, amenity balances) do not change.
- Refunds stay manual: staff pay them back by hand as today. Refunds through Xendit's API can come later.
- No saved cards, no automatic renewals, no convenience fee.

## Anti-goals (would be a failure even if it "works")

- The Xendit secret key in the app, in git, or in this chat.
- A membership activated without money actually received (a forged webhook, a wrong amount, a replayed call).
- Money received but the member gets nothing and nobody is told (paid after the payment expired or was cancelled).

## Decisions

| # | Decision | Who |
|---|----------|-----|
| X1 | Online payments go through Xendit for memberships, renewals, packages and amenity down payments. | Owner |
| X2 | The GCash-proof upload is replaced, not kept as a fallback. | Owner |
| X3 | The gym absorbs Xendit's fee. Members pay the listed price. | Owner |
| X4 | Refunds stay manual (staff pay back by hand). | Claude's call, owner can override |
| X5 | Use Xendit's **hosted checkout** (Payment Session in payment-link mode; Invoices v2 if the account doesn't have Sessions). The app opens it in the browser. Rejected: our own in-app GCash/Maya/card screens, which mean far more code and per-method handling. | Claude's call, owner agreed to hosted checkout |
| X6 | Which methods show (GCash, Maya, QR Ph, cards) is set in the Xendit dashboard, not in code. | Claude's call |
| X7 | The payment method stays `qr` in the database and is renamed **"Online"** in the app. Xendit's channel (GCASH, PAYMAYA, CARD, ...) goes in `reference_no`. No new enum value, so reports and refunds keep working. | Claude's call |
| X8 | The Xendit link is created when the member taps **Pay online**, not when the payment is created. It expires at the payment's own `expires_at` (the gym's payment-expiry hours, 48 by default). Tapping again reuses the same link while it is valid. | Claude's call |
| X9 | Money that arrives for a payment that is no longer pending (expired, cancelled, already paid) or for the wrong amount is **not** activated. It is recorded on the payment and staff are notified to refund it by hand. | Claude's call |
| X10 | Safety net for a lost webhook: when the member comes back to the app, it asks our server to check the link's status with Xendit, and a paid link is confirmed the same way the webhook would. | Claude's call |
| X11 | Proofs already sent before the switch can still be approved or rejected by the desk as today. New payments without a proof can't be approved by hand any more; the desk can still reject (cancel) a pending one. | Claude's call |
| X12 | Members come back from the browser by closing it; the page refreshes when it shows again. No app deep link. | Claude's call |
| X13 | Old proof screenshots stay in the `payment-proofs` bucket for history; nothing is deleted. | Claude's call |
| X14 | Built and tested in Xendit **Test mode** first; live keys go in only after the account is activated. | Claude's call |

## How it works

```
Member taps "Pay online"
  → app calls Edge Function xendit-checkout {payment_id}       (member's own login)
      → checks: the payment is theirs, pending, method qr, not expired
      → reuses the stored link, or creates a Xendit checkout for exactly payments.amount (PHP),
        reference = payment_code, expires = payments.expires_at
      → saves the Xendit id + URL on the payment, returns the URL
  → app opens the URL in the browser; member pays with GCash / Maya / QR Ph / card

Xendit → Edge Function xendit-webhook                          (public, no login; verify_jwt = false)
      → rejects unless x-callback-token equals XENDIT_WEBHOOK_TOKEN (constant-time compare)
      → on "completed/paid": rpc confirm_online_payment(payment_code, xendit_id, amount, channel)
      → anything else (expired, unknown event): 200 and ignore; our expire_payments cron handles expiry

confirm_online_payment (SQL, security definer, service_role only)
      → locks the payment by payment_code
      → already verified by this same Xendit id: do nothing (Xendit retries)
      → pending, method qr, amount matches, xendit id matches the one we stored:
           status verified, verified_at now, receipt_no, reference_no = channel,
           activate_payment_target, notify member "Payment received", audit 'payment.online'
      → otherwise (X9): store the Xendit id/amount, notify staff "Online payment needs a refund", audit; no activation

Member returns to the app → page refreshes → for a pending payment that has a link, app calls
xendit-checkout {payment_id, check: true} → server asks Xendit for the link's status → if paid, same confirm (X10)
```

### Database (migration 0075)

- `payments`: add `xendit_id text unique`, `xendit_url text`, `online_paid_amount numeric` (for X9 records).
- `confirm_online_payment(...)`: as above. `revoke all ... from public, anon, authenticated`; grant execute to `service_role` only.
  Check live grants after the push (live default privileges open new functions to anon).
- A small `set_payment_checkout(payment_id, xendit_id, url)` for the checkout function to store the link, also service_role only.
  (Or the function writes the row directly with the admin client; the builder picks the smaller one.)
- `verify_payment`: approving a pending `qr` payment with no `proof_path` fails with `pay_online` (X11).
- `submit_payment_proof`: revoke execute from `authenticated` so new proofs can't be sent. Keep the function for history.

### Edge Functions

- `supabase/functions/xendit-checkout/index.ts`: needs the member's login; uses `_shared/admin.ts` like the existing functions.
- `supabase/functions/xendit-webhook/index.ts`: `verify_jwt = false` in `config.toml`.
- Secrets (owner runs these in their own terminal, never in chat):
  `supabase secrets set XENDIT_SECRET_KEY=... XENDIT_WEBHOOK_TOKEN=...`
- Owner pastes the webhook URL `https://<project>.supabase.co/functions/v1/xendit-webhook` into the Xendit dashboard.

### App

- Core: `IMembershipService.PayOnlineAsync(paymentId)` returns the URL, and `CheckOnlineAsync(paymentId)` handles X10. Both go
  through `InvokeFunctionAsync` like `verify-member-password`. Remove `SubmitProofAsync`, `IProofPicker` use on My membership,
  `NeedsProof`, and `CanSendProof`, replaced by `NeedsPayment` / `CanPayOnline`.
- Member screens (Android, phone layouts, light/dark, 1.3 font scale; **impeccable** for the screen work):
  - My membership: the "Pay by QR and send proof" card becomes a "Pay online" card showing the amount, the deadline and one
    button, plus a "Checking your payment..." state after returning.
  - Book amenity: the "GCash · Send proof now" choice becomes "Pay online now"; a booking's "Send proof" becomes "Pay online".
  - Packages page rules text, and the booking confirmation texts (`BookingViewModels.cs:238,254`).
- Staff screens (Windows only): Payments page shows "Waiting for online payment" instead of "No proof yet", hides Approve for
  online rows without a proof, and shows a "Needs refund" badge for X9 records. Method name "QR" becomes "Online".
- `terms.md`: replace the GCash-proof wording with Xendit online payment (the terms version bump that triggers re-acceptance
  only if the owner wants it).
- Demo mode: `DemoGateway` fakes both functions; "Pay online" in demo marks the payment paid at once.

## Testing

- `supabase/tests/local/xendit.mjs`: confirm_online_payment activates the right thing for each kind, is idempotent, refuses a
  wrong amount, flags paid-after-expiry, and anon/authenticated can't call it.
- C# tests for the new view-model states and the demo flow.
- End to end in Xendit Test mode: pay a membership, a package and an amenity down payment with Xendit's test GCash; let one
  expire; replay a webhook.

## Phases

Each phase ends with its migrations and functions pushed live (standing owner rule) and a commit on this branch.

1. **Database** (0075): columns, `confirm_online_payment`, `verify_payment` guard, grants; local tests; push live.
2. **Server functions**: `xendit-checkout`, `xendit-webhook`; owner sets the two secrets and the webhook URL; test with a Test
   mode payment by calling the function directly.
3. **Member app** (impeccable): Pay online on My membership, Book amenity, Packages texts; demo mode; C# tests.
4. **Staff app and wording**: Payments page labels and refund badge, "Online" method name, `terms.md`.
5. **End-to-end check and go-live list**: full Test mode run, review, then the owner's switch to live keys after activation.

## Left to the builder

- Exact Xendit API (Payment Sessions vs Invoices v2) after checking what the account offers; field names from Xendit's docs.
- Whether the checkout function stores the link through an RPC or the admin client.
- Copy wording on each screen, within the impeccable pass.
