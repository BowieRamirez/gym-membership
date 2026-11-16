# Xendit Online Payments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Created: 2026-11-16
Last updated: 2026-11-16 (marked done by the owner)
Status: DONE (owner, 2026-11-16). All five phases built, reviewed and live (`0075`-`0077`, `xendit-checkout`, `xendit-webhook`); branch pushed to GitHub at `2d8d5e6`, not merged. Follow-ups are listed under "Current work". The cashier/walk-in/logs plan continued on this branch from `0078` (see `2026-11-16-cashier-walkin-logs.md`).
Branch: `xendit-payments-2026-11-16`, new from `main` at `b0166b9` (v0.6.0). Every phase stays on this branch; the owner merges to `main` when the plan is done.

**Goal:** Members pay memberships, renewals, packages and amenity down payments online through Xendit's hosted checkout, and a paid checkout verifies and activates the purchase by itself, replacing the GCash-proof upload.

**Architecture:** The pending `qr` payment the app already creates stays the unit of work. A member-called Edge Function (`xendit-checkout`) makes or reuses a Xendit checkout for that payment; a public Edge Function (`xendit-webhook`) checks Xendit's token and calls one service-role SQL function (`confirm_online_payment`), which verifies the payment and runs the existing `activate_payment_target`. The app opens the checkout in the browser and asks the server to re-check when the member comes back.

**Tech Stack:** Supabase (Postgres migrations, Deno Edge Functions), .NET MAUI app with CommunityToolkit.Mvvm (`GymMembership.Core`, `GymMembership.App`), xUnit, Node database tests on embedded Postgres (`supabase/tests/local`).

**Spec:** `docs/superpowers/specs/2026-11-16-xendit-payments-design.md` (approved by the owner 2026-11-16).

## How to use this plan

1. **Decisions first.** A phase starts only when every decision it lists under "Needs" has an answer in section 2.
2. One phase at a time, in order. Before starting, read `CLAUDE.md` and the code named here; line numbers drift.
3. Tick a step only when it is done and proven (`- [x]`, with the date and the test or commit). Update "Current work" before stopping.
4. Keep code, tests, manual checks and live deployment apart. Written is not verified; verified is not deployed.
5. **Database (owner's standing rule):** when a phase changes the database or Edge Functions, push them live as part of finishing the phase once the dry run succeeds (`npx supabase db push --remote --dry-run`, then `--remote --yes`; `npx supabase functions deploy <name>`), without asking again. Then check live grants read-only with the anon key. `git push`, merging, releases, and switching Xendit to live keys need the owner's go-ahead.
6. **The Xendit secret key and webhook token never appear in chat, in git, or in the app.** The owner sets them in their own terminal.

Status vocabulary: `PLANNED`, `WAITING`, `IN PROGRESS`, `BLOCKED`, `IMPLEMENTED`, `VERIFIED`, `DEPLOYED`.

**Skills, every phase:** `superpowers:test-driven-development` (failing test first) for each database and Core rule; `superpowers:systematic-debugging` for every bug; `superpowers:verification-before-completion` before ticking; `ponytail:ponytail-review` on the phase diff before finishing; context-mode tools for large outputs.
**Skills, every screen:** `impeccable` together with `frontend-design:frontend-design`. A changed screen is captured first, then `impeccable critique`, fixes, `impeccable polish`; copy through `impeccable clarify`. Member screens are checked as real phone layouts on the Pixel 7 emulator, light and dark, at font scale 1.3, and on Windows; staff pages are Windows only.

### Current work / restart here

- **2026-11-16: DONE (owner).** Real Test-mode payments ran on the live project: payment 14 (member, online) and payment 15 (desk, GCash, Xendit "Successful") are verified with receipts and their memberships active. Owner set the Xendit keys, the webhook URL and the methods (Task 2 Step 5). Whole-branch review done; its Critical and Important findings are fixed (`0077`, later `0082`).
- **Follow-ups (not blocking "done"):** (1) **Xendit's paid notice did not confirm payment 15 on its own**: it was confirmed by the member's My membership check (X10). Cause not found yet; needs the Xendit webhook delivery log or the Supabase `xendit-webhook` log around 8:56 PM. Until then the desk taps Check payment and members' My membership confirms. (2) Pixel 7 phone check of the Pay online card (light/dark/1.3) not done. (3) Deferred minors in the ledger (unknown-session webhook only logs; cancelled/rejected payments leave the Xendit page open; Book an amenity doesn't re-check on return; check error banner on every 20 s tick; return page wording now neutral; seconds-left session reused). (4) Switching to Xendit live keys after account activation (owner, go-live list Task 6 Step 5).
- **2026-11-16: Phases 3 and 4 IMPLEMENTED (app only, no database change); Phase 5 partly done.** Core + demo + member screens (`a0be36f`, `81214bb`), staff Payments page + terms text (`7e1b7ae`), break attempts (`5b2486c`), dead `QrUrlAsync` removed (`4a425b2`). xUnit 1075/1075; DB 629 checks exit 0; Windows and Android builds succeed. Windows demo look at My membership's Pay online card done. UI scripts: Admin navigation PASS, Session requests PASS, **Archive FAILS at `ConfirmAccountDeletion` on `origin/main` too** (not this branch). Final whole-branch review running. **Waiting on the owner:** Xendit secrets + webhook URL + methods (Task 2 Step 5), then the Test-mode run (Task 6 Steps 1-2), Pixel 7 phone check (light/dark/1.3), and a decision on removing Gym settings' payment QR upload (now unused; its line "Members can't pay until you add one" is untrue).
- **2026-11-16: Phase 2 DEPLOYED, owner steps open.** `0076` live (replace an expired session only by naming it); `xendit-checkout` (JWT) and `xendit-webhook` (`--no-verify-jwt`) deployed. Live: webhook GET 200 with the return text, bad token 401, checkout without a member login 401, anon 42501 on `save_online_checkout`. `xendit.mjs` 14/14, `edge-rules.mjs` 8/8 (token check, mutation-tested). **Waiting on the owner:** secrets, webhook URL, payment methods (Task 2 Step 5); the live Test-mode payment (Step 6) moves to Phase 5. Next migration `0077`.
- Phase 2 rulings: (d) Xendit Payment Sessions are short-lived (30 min default, Xendit recommends short), so X8 becomes: one session per payment at a time, 30 minutes (never past the payment's own `expires_at`); on Pay online the server asks Xendit: ACTIVE → same link, COMPLETED → confirm, EXPIRED/CANCELED → new session swapped in only by naming the old one (0076). (e) Session `reference_id` = `payment_code-<ms>` (unique per session); no `customer` object sent (optional in the docs); description "Gym payment <code>". (f) Someone else's payment answers `not_found`, not `forbidden`, so ids can't be probed. (g) The webhook acts only on `payment_session.completed`; Invoices v2 not built (Sessions exist in the docs; confirm on the owner's account in Phase 5).
- **2026-11-16: Phase 1 DEPLOYED.** `0075` live (dry run listed only it); anon gets 42501 on `confirm_online_payment`, `save_online_checkout`, `submit_payment_proof`, `verify_payment_before_xendit`, `verify_payment`. `xendit.mjs` 13/13; full `npm test` exit 0 (621 checks). **Members on the current app can no longer send proofs** until Phase 3's app ships. Next migration `0076`. Next: Phase 2, Task 2.
- Rulings in Phase 1: (a) the desk's approve is blocked only once a Xendit checkout exists (`xendit_id` set, no proof), not for every proofless `qr` payment: 34 test files and today's database approve proofless payments, and the UI hides Verify anyway (Task 5); (b) `refund_requests_check` now allows a decided refund with no `decided_by` (system decision): a slot lost to an earlier payment, paid online, has no signed-in staff member, and the refund insert failed; the Refunds pages must show a blank decider as "Automatic" (Task 5); (c) the staff note says "PHP 1500.00", not "₱", because the local test database can't store ₱.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Database: 0075, `confirm_online_payment` | X1, X2, X7, X9, X11 | DEPLOYED |
| 2 | Edge Functions, secrets, webhook URL | X5, X8, X10, X14 | DEPLOYED |
| 3 | Core service, demo, member screens | X12 | DONE (phone check open) |
| 4 | Staff Payments page, wording, terms | X13, X15 | DONE (Payments to verify later removed, cashier plan Y7) |
| 5 | End-to-end Test-mode run, review, go-live list | X14 | DONE (live keys after activation) |

## 1. Repository findings (2026-11-16)

| Area | Today | Consequence |
|---|---|---|
| Online purchases | `avail_membership`, `renew_membership`/`extend_membership`, `book_amenity` all insert a `payments` row: `method 'qr'`, `status 'pending'`, `expires_at` = now + gym's payment-expiry hours (48). | One confirm path covers all three kinds. |
| Proof | `submit_payment_proof` (`0005_functions.sql:206`) stores `reference_no`, `proof_path`; app `MembershipService.SubmitProofAsync` (`Services.cs:378`) uploads to bucket `payment-proofs`. | Revoke from members; remove from app. |
| Approval | `verify_payment` (`0018:272` wraps `0005:230`): verified, `verified_by`, `verified_at`, `next_receipt_no()`, `activate_payment_target`, `notify`, `audit('payment.verify', ...)`. | The confirm function repeats this minus the staff checks. |
| Constraint | `payments_verified_complete` (`0002_tables.sql:253`) needs `verified_by` not null for verified. | 0075 relaxes it to `verified_by is not null or xendit_id is not null`. |
| Reference | `payments_reference_no_key` (`0002:264`) is unique over pending/verified. | `reference_no` gets the Xendit id (unique); the channel goes in a new column `online_channel` (spec X7 adjusted). |
| Amenity hold | `settle_amenity_hold` trigger (`0064:133`) fails `payment_expired` when activated after expiry, and turns a lost slot into an approved refund. | Confirm must refuse expired payments itself (X9) before activating; the slot-taken refund path stays as is. |
| Expiry | `expire_payments()` (`0052:67`) expires pending `qr` payments past `expires_at`. | Unchanged; the Xendit link expires at the same moment. |
| Edge Functions | `_shared/admin.ts` gives `adminClient()`, `cors`, `reply()`; `verify-member-password` shows the "read caller from the JWT" pattern; `config.toml:418` sets `verify_jwt = false` for `check-email`. | Same patterns for the two new functions. |
| App calls | `ISupabaseGateway.InvokeFunctionAsync(name, body)` returns the JSON string; `DemoGateway.InvokeFunctionAsync` (`DemoGateway.cs:217`) fakes functions. | New calls go the same way. No browser-opening service exists yet. |

## 2. Decisions

From the approved design (X1 to X14); X9 and X7 are adjusted by section 1's findings, X15 is new.

| # | Decision | Answer |
|---|---|---|
| X1 | Xendit for memberships, renewals, packages and amenity down payments | Owner, 2026-11-16 |
| X2 | Replace the GCash-proof upload, no fallback | Owner, 2026-11-16 |
| X3 | Gym absorbs the fee | Owner, 2026-11-16 |
| X4 | Refunds stay manual | Approved with the design, 2026-11-16 |
| X5 | Xendit hosted checkout (Payment Sessions, `mode PAYMENT_LINK`; Invoices v2 only if the account lacks Sessions) | Approved with the design |
| X6 | Methods chosen in the Xendit dashboard | Approved with the design |
| X7 | Method stays `qr`, shown as "Online"; Xendit id in `reference_no`, channel in `online_channel` | Approved; storage adjusted 2026-11-16 (unique reference index) |
| X8 | Link made on "Pay online", expires at `payments.expires_at`, reused while valid | Approved with the design |
| X9 | Money for a payment that is not pending, is past `expires_at`, or has the wrong amount: nothing activates; the Xendit id and amount are saved on the payment, staff get a notification "Online payment to refund", and Admin refunds it from the Xendit dashboard | Approved; "badge" dropped for the existing notification (2026-11-16) |
| X10 | On return to the app, the server re-checks the link with Xendit | Approved with the design |
| X11 | Old proofs can still be approved; online payments without a proof can't be approved by hand, only rejected | Approved with the design |
| X12 | No deep link; the page refreshes when it shows again | Approved with the design |
| X13 | Old proof screenshots kept | Approved with the design |
| X14 | Test mode first; live keys after activation, on the owner's go-ahead | Approved with the design |
| X15 | Terms text updated, **no new terms version** (no re-acceptance) | Owner, 2026-11-16 |

## Global Constraints

- Next migration is `0075`; never edit an applied migration.
- Every new function: `revoke all ... from public, anon, authenticated` and grant only what is needed; after the push, check live grants with the anon key (new functions are open to anon by default on this project).
- `confirm_online_payment` and `save_online_checkout` are `service_role` only. The app never calls them.
- Business rules live in the database and are mirrored in `DemoGateway`.
- Money in PHP; amounts compared exactly against `payments.amount`.
- Xendit API base `https://api.xendit.co`, HTTP Basic auth with the secret key as user and empty password; secrets `XENDIT_SECRET_KEY`, `XENDIT_WEBHOOK_TOKEN`.
- Member wording says "Pay online", never "QR" or "proof". The method's display name is "Online".
- AutomationIds used by `scripts/Test-Windows*.ps1` stay on their controls.

## Review Focus

1. **Xendit sends the same "paid" webhook twice (it retries)** → the second call changes nothing and returns 200. Test: `xendit.mjs` "a repeated webhook does nothing".
2. **The member pays just after the payment expired, or after the desk rejected it or the member cancelled it** → nothing activates, the money is recorded, staff are notified. Tests: `xendit.mjs` "paid after expiry", "paid after reject".
3. **A webhook with a wrong token, or a right token and the wrong amount** → 401 for the token; the amount case is treated as X9. Tests: `xendit.mjs` "wrong amount"; Phase 5 curl with a bad token.
4. **The member taps Pay online twice, or on two phones** → the same Xendit link comes back; one payment can't be charged twice through us. Test: Phase 2 step "second call returns the same url"; demo xUnit "pay online twice reuses".
5. **The member comes back before the webhook arrives, or the webhook never arrives** → "Checking your payment…" then the server check confirms it. Test: xUnit `Returning_checks_the_link_and_activates`; Phase 5 run with the webhook URL blanked.

---

## Phase 1: Database

### Task 1: Migration 0075 and its tests

**Files:**
- Create: `supabase/migrations/0075_xendit_payments.sql`
- Create: `supabase/tests/local/xendit.mjs`
- Modify: `supabase/tests/local/package.json` (append `&& node xendit.mjs` to `test`)
- Modify: tests that send proofs as a member and now must not (`rules.mjs`, `payment-authority.mjs` and any other file `npm test` shows failing on `submit_payment_proof`): call it as the table owner, which stands in for "proof sent before the switch".

**Interfaces:**
- Produces (SQL, all `security definer`, `search_path ''`, `service_role` only):
  - `payments` columns `xendit_id text unique`, `xendit_url text`, `online_channel text`, `online_paid_amount numeric(10,2)`.
  - `save_online_checkout(p_payment_id bigint, p_xendit_id text, p_url text) returns void`: sets both only while the payment is pending `qr`.
  - `confirm_online_payment(p_xendit_id text, p_amount numeric, p_channel text default null) returns text`: returns `'confirmed'`, `'already'` or `'refund'`.
  - `verify_payment(...)`: approving a pending `qr` payment with `proof_path is null` fails `pay_online` (P0002).
  - `submit_payment_proof`: execute revoked from `authenticated`.

- [x] **Step 1: Write the failing tests** in `xendit.mjs`, in `prorated-refunds.mjs`'s style (`start()`, `as(uid, sql)`, `test(name, fn)`). Call the service functions with `set local role service_role`. Cases, each one a `test`:
  - "membership: confirm verifies, gives a receipt and starts the term": `avail_membership` → `save_online_checkout(id,'ps-1','https://x')` → `confirm_online_payment('ps-1', amount, 'GCASH')` = `'confirmed'`; payment `status 'verified'`, `receipt_no ~ '^OR-'`, `reference_no 'ps-1'`, `online_channel 'GCASH'`, `verified_by is null`; a `membership_terms` row exists.
  - "package (extend) and amenity down payment confirm the same way": amenity → reservation `confirmed`.
  - "a repeated webhook does nothing": second call = `'already'`, receipt unchanged.
  - "wrong amount is not activated": `amount + 1` → `'refund'`; still `pending`; `online_paid_amount` saved; a staff notification titled `Online payment to refund`.
  - "paid after expiry": set `expires_at = now() - interval '1 minute'` → `'refund'`, nothing activated.
  - "paid after reject": desk rejects with a reason, then confirm → `'refund'`, status stays `rejected`.
  - "unknown xendit id raises not_found" (nothing to attach the money to; the webhook logs it and answers 200).
  - "slot taken still refunds as before": two members hold the same slot, one confirms by desk, the other online → second reservation `cancelled 'Slot taken'` and an approved refund request.
  - "desk can't approve an online payment without proof": `verify_payment(id,true)` as desk → `pay_online`; reject still works.
  - "an old proof can still be approved": proof set by the table owner → desk approve works.
  - "members can't send proofs any more": member `submit_payment_proof` → permission denied (42501).
  - "nobody but service_role can call the new functions": member, desk, admin and anon → 42501 on both.

- [x] **Step 2: Run them and see them fail**
  Run: `cd supabase/tests/local; node xendit.mjs`
  Expected: FAIL, `function public.save_online_checkout ... does not exist`.

- [x] **Step 3: Write `0075_xendit_payments.sql`.** Columns; drop and re-add `payments_verified_complete` as `status not in ('verified','voided') or ((verified_by is not null or xendit_id is not null) and verified_at is not null and receipt_no is not null)`. Then `confirm_online_payment`, in this order:
  - lock the row `where xendit_id = p_xendit_id for update`; not found → `fail('not_found')`;
  - `status 'verified'` and `reference_no = p_xendit_id` → `'already'`;
  - `status <> 'pending'` or `expires_at <= now()` or `p_amount <> amount` → set `online_paid_amount = p_amount`, `online_channel = p_channel`; `notify_staff('payment','Online payment to refund', payment_code || ' · ₱' || p_amount, jsonb_build_object('payment_id', id))`; `audit('payment.online_refund', ...)`; return `'refund'`;
  - otherwise set `status 'verified'`, `verified_at now()`, `receipt_no next_receipt_no()`, `reference_no p_xendit_id`, `online_channel`, `online_paid_amount`; `activate_payment_target`; `notify(member,'payment','Payment received', payment_code, ...)`; `audit('payment.online', ...)`; return `'confirmed'`.
  `verify_payment` gets the `pay_online` guard by rename-and-wrap, as `0018` did. `revoke execute on function public.submit_payment_proof(bigint,text,text) from authenticated;`. Add the `pay_online` text to the app's error list in Task 3.

- [x] **Step 4: Run the tests until they pass, then the whole suite**
  Run: `node xendit.mjs` → all ok. Then `npm test` → every file ok, exit 0 (fix the older proof tests as listed under Files).

- [x] **Step 5: Push live and check grants**
  Run: `npx supabase db push --remote --dry-run` (lists only `0075`), then `npx supabase db push --remote --yes`. Then with the anon key: `rpc/confirm_online_payment`, `rpc/save_online_checkout`, `rpc/submit_payment_proof` → 401/42501.

- [x] **Step 6: Commit**
  `git add supabase/migrations/0075_xendit_payments.sql supabase/tests/local` → `git commit -m "0075: Xendit confirms online payments; proofs end"`

**Note for the owner after Phase 1:** from this push, members on the current app can no longer send proofs (permission refused) until the new app ships in Phase 3. Pending proofs already sent can still be approved. If that gap matters, hold the push until Phase 3 is ready, and push Phases 1 to 3 together.

---

## Phase 2: Edge Functions and Xendit setup

### Task 2: `xendit-checkout` and `xendit-webhook`

**Files:**
- Create: `supabase/functions/xendit-checkout/index.ts`
- Create: `supabase/functions/xendit-webhook/index.ts`
- Create: `supabase/functions/_shared/xendit.ts`: `xendit(path: string, init?: RequestInit): Promise<any>` (Basic auth, JSON, throws on non-2xx).
- Modify: `supabase/config.toml` (add `[functions.xendit-webhook]` `verify_jwt = false`, next to `check-email`).

**Interfaces:**
- Consumes: `save_online_checkout`, `confirm_online_payment` (Task 1).
- Produces: `POST xendit-checkout {payment_id: number, check?: boolean}` (needs the member's JWT) →
  `{url: string}` | `{paid: true}` | `{paid: false}` | `{error: 'forbidden'|'not_found'|'already_processed'|'payment_expired'|'unavailable'}`.
  `POST xendit-webhook` (Xendit only) → 200 `{ok:true}`, 401 on a bad token. `GET xendit-webhook` → plain text "Payment done. You can close this page and go back to the gym app." (used as the return URL).

- [x] **Step 1: Confirm the API against the account.** In the Xendit Test-mode dashboard and the API docs, check that Payment Sessions (`POST /sessions`, `GET /sessions/{id}`, webhook `payment_session.completed`) are available and which body fields are required for `session_type 'PAY'`, `mode 'PAYMENT_LINK'`, `country 'PH'`, `currency 'PHP'` (a `customer` object may be required: use the member's name and email from `profiles`). If Sessions aren't available, use Invoices v2 (`POST /v2/invoices` with `external_id`, `GET /v2/invoices/{id}`, webhook status `PAID`). Write the choice and the exact fields under "Current work".

- [x] **Step 2: Write `xendit-checkout`.** Read the caller from the JWT as `verify-member-password` does. Load the payment with the admin client and check that it is the caller's, `status 'pending'`, `method 'qr'`, `expires_at > now`; otherwise reply with the matching error.
  - **With `check: true`:** if `xendit_id` is null, reply `{paid:false}`. Otherwise `GET` the session; if it is completed/paid, call `confirm_online_payment(id, amount, channel)` and reply `{paid: result === 'confirmed' || result === 'already'}`.
  - **Without it:** if `xendit_url` is set, reply with it (X8). Otherwise create the session with `reference_id = payment_code`, `amount = payments.amount`, `expires_at = payments.expires_at`, `description` = the item name, and success/cancel return URL = `${SUPABASE_URL}/functions/v1/xendit-webhook`. Then call `save_online_checkout`. To be safe from a double tap, re-read the row and reply with whichever URL was stored first.

- [x] **Step 3: Write `xendit-webhook`.**
  - **GET:** reply with the return text.
  - **POST:** compare the `x-callback-token` header with `XENDIT_WEBHOOK_TOKEN` in constant time (`crypto.subtle.timingSafeEqual`, or compare SHA-256 digests); a mismatch gets 401. On the completed/paid event, call `confirm_online_payment(session or invoice id, amount, channel ?? null)`. Every other event, and `not_found`, gets 200 `{ok:true}` with a `console.log`. A database error gets 500, so Xendit retries.

- [x] **Step 4: Deploy**
  Run: `npx supabase functions deploy xendit-checkout`, then `npx supabase functions deploy xendit-webhook --no-verify-jwt`.

- [x] **Step 5: Owner steps (the owner does these; Claude waits).**
  - In their own terminal: `npx supabase secrets set XENDIT_SECRET_KEY=<test secret key> XENDIT_WEBHOOK_TOKEN=<verification token>`.
  - In Xendit (Test mode), Settings → Webhooks: set the Payment Session (or Invoice paid) URL to `https://<project-ref>.supabase.co/functions/v1/xendit-webhook`.
  - Settings → Payment methods: turn on GCash, Maya, QR Ph and cards for Test mode.

- [x] **Step 6: Check it live (Test mode).** Use a test member's token (demo account on the real backend, made by the owner) and a pending membership payment:
  - `xendit-checkout` returns a URL, and **a second call returns the same URL**.
  - Pay it in Xendit's test checkout: the payment becomes `verified` with a receipt within seconds.
  - `xendit-webhook` POSTed with a wrong token → 401.
  - Another user's `payment_id` → `forbidden`. Anon → 401.

- [x] **Step 7: Commit**
  `git add supabase/functions supabase/config.toml` → `git commit -m "Edge Functions: Xendit checkout and webhook"`

---

## Phase 3: App, member side

### Task 3: Core service, error text and demo

**Files:**
- Modify: `src/GymMembership.Core/Models/Models.cs` (Payment: `[Column("xendit_id")] XenditId`, `[Column("online_channel")] OnlineChannel`; `PayMethod.Name`: `Qr => "Online"`)
- Modify: `src/GymMembership.Core/Services/Services.cs` (`PaymentItem`, `IMembershipService`, `MembershipService`)
- Modify: `src/GymMembership.Core/Services/Foundation.cs` (new `IUrlOpener`; error text `pay_online` → "This payment is paid online through Xendit. It verifies itself once the member pays."; `payment_expired` already exists or add "This payment ran out. Make a new one.")
- Modify: `src/GymMembership.Core/Demo/DemoGateway.cs` (`xendit-checkout` case before the admin check; `submit_payment_proof` refused for members; `verify_payment` `pay_online` guard)
- Test: `tests/GymMembership.Tests/OnlinePaymentTests.cs` (new); update `PaymentTests.cs` and any other test using `SubmitProofAsync`/`NeedsProof`.

**Interfaces:**
- Produces:
  - `public interface IUrlOpener { Task OpenAsync(string url); }` (Foundation.cs)
  - `public sealed record OnlineCheckout(string? Url, bool Paid);`
  - `IMembershipService.PayOnlineAsync(int paymentId) : Task<AppResult<OnlineCheckout>>`: `xendit-checkout {payment_id}`; the demo answers `{paid:true}` and marks it paid.
  - `IMembershipService.CheckOnlineAsync(int paymentId) : Task<AppResult<bool>>`: `xendit-checkout {payment_id, check:true}`.
  - `PaymentItem`: `NeedsProof` renamed `NeedsPayment` (same rule); new last parameter `bool HasCheckout = false`; `AwaitingReview` unchanged (old proofs).
  - Removed: `IMembershipService.SubmitProofAsync`.

- [x] **Step 1: Write the failing xUnit tests** in `OnlinePaymentTests.cs`, against `DemoGateway`:
  - `Pay_online_verifies_and_starts_the_membership`: register Ana → `AvailAsync(Quarterly)` → `NeedsPayment` → `PayOnlineAsync` = `Paid true` → payment `verified`, receipt `^OR-\d{4}-\d{6}$`, plan `active`.
  - `Pay_online_twice_reuses`: second `PayOnlineAsync` on a paid one → `Conflict` (`already_processed`).
  - `Expired_payment_cannot_be_paid_online`: move the demo clock past `ExpiresAt` → `PayOnlineAsync` fails `payment_expired`.
  - `Desk_cannot_approve_an_online_payment`: desk `VerifyAsync(id,true)` → error `pay_online`; reject works.
  - `Amenity_down_payment_paid_online_confirms_the_booking`.
  - `Returning_checks_the_link_and_activates`: the demo's `check:true` answers `paid:false` before `PayOnlineAsync` and `true` after.
  - `PayMethod_Qr_reads_Online`: `PayMethod.Name(PayMethod.Qr) == "Online"`.

- [x] **Step 2: Run them and see them fail**
  Run: `dotnet test tests/GymMembership.Tests --filter OnlinePaymentTests`. Expected: build errors for `PayOnlineAsync` and `NeedsPayment`.

- [x] **Step 3: Implement the Interfaces above.** Parse the function's JSON with `System.Text.Json`, as `VerifyPasswordAsync` does (`Services.cs:1379`). In the demo, the `xendit-checkout` case follows the same rules as the server (owner, pending, `qr`, not expired), then marks the payment verified through the same path as the demo `verify_payment` approve, without a staff member.

- [x] **Step 4: Run the whole xUnit suite**
  Run: `dotnet test tests/GymMembership.Tests` → all pass (update the old proof tests: `QR_payment_end_to_end...` becomes the online flow; `A_reference_number_cannot_be_used_on_two_payments` is deleted, since members no longer type references).

- [x] **Step 5: Commit** → `git commit -m "Core: pay online through Xendit; proofs gone"`

### Task 4: Member screens (impeccable)

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`MyMembershipViewModel`: drop `IProofPicker`, `ReferenceNo`, `QrUrl`, `AccountName`, `SendProofAsync`; add `IUrlOpener opener`, `PayOnlineCommand`, `[ObservableProperty] bool checking`; on load, if `PayNow?.HasCheckout`, call `CheckOnlineAsync` once and reload when paid; hero caption "Pay online to start your membership."; `AvailAsync` confirm text "You pay online on My membership."; `PayAgainAsync` notice "New payment created. Pay online below.")
- Modify: `src/GymMembership.Core/ViewModels/BookingViewModels.cs` (lines 238 and 254: "Pay the ₱… down payment online now." / "Requested. Pay the down payment online from your bookings. It's yours once it's paid."; `SendProof` becomes `PayOnline(BookingRow)`, which calls `membership.PayOnlineAsync` and opens the URL; add `IUrlOpener`)
- Modify: `src/GymMembership.Core/Services/ReservationService.cs:134,158-159` (`CanSendProof` → `CanPayOnline` = `IsWaiting && Payment is { Method: PayMethod.Qr, ProofPath: null }`; detail "Down payment {Price} online. Pay by {At(...)}.")
- Modify: `src/GymMembership.App/Views/MyMembershipPage.xaml` (+ `.cs`), `BookAmenityPage.xaml` (+ `.cs`, line 68 button and line 282 choice "Pay online now"), `PackagesPage.xaml:54` rules text ("Pay online on My membership. ...")
- Modify: `src/GymMembership.App/Services/AppServices.cs` (`MauiUrlOpener : IUrlOpener` using `Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred)`) and `MauiProgram.cs` (register it; demo registers a no-op opener)
- Test: `tests/GymMembership.Tests/OnlinePaymentTests.cs` (view-model cases)

**Interfaces:**
- Consumes: `PayOnlineAsync`, `CheckOnlineAsync`, `IUrlOpener`, `PaymentItem.NeedsPayment/HasCheckout` (Task 3).

- [x] **Step 1: Capture the current screens.** (Windows done 2026-11-16; Pixel 7 is the Step 5 follow-up) On Windows and the Pixel 7, light and dark: My membership with a payment to make, the Book an amenity sheet with GCash picked, and a booking waiting for its down payment. Save to `.impeccable/critique/`.
- [x] **Step 2: Failing view-model tests.**
  - `MyMembership_pay_online_opens_the_link`: a fake `IUrlOpener` records the URL. In demo the payment is paid, so the opener isn't called and the hero shows days left after the reload.
  - `MyMembership_shows_checking_while_the_server_checks`: `Checking` is true during `CheckOnlineAsync`.
  - `Booking_pay_online_from_the_row`.
  Run: `dotnet test --filter OnlinePaymentTests` → FAIL.
- [x] **Step 3: Implement the view models** until the tests pass.
- [x] **Step 4: Screens through impeccable.** Run `impeccable critique` on the captures, then lay out the new "Pay online" card: the amount, "Pay by <date, Manila>", one primary **Pay online** button, a line naming GCash, Maya, QR Ph and cards, and a quiet "Checking your payment…" state. Use the existing styles (`SmallButton`, `FilterChip`, `Caption`) and the sage/lime tokens. Copy goes through `impeccable clarify`, then `impeccable polish`.
- [ ] **FOLLOW-UP (not done): Step 5: Check on devices.** Windows done; Pixel 7 not run (no emulator). On the Pixel 7, light and dark, at font scale 1.3, and on Windows at about 1100 px light and 1600 px dark. Demo flow: buy Quarterly → Pay online → active. Book an amenity → Pay online now → confirmed.
- [x] **Step 6: Builds and tests.** Run `dotnet test tests/GymMembership.Tests` → all pass. Then the Windows build and the Android build (`--no-incremental`) must succeed.
- [x] **Step 7: Commit** → `git commit -m "Member screens: Pay online replaces send proof"`

---

## Phase 4: Staff side and wording

### Task 5: Payments page, method name, terms

**Files:**
- Modify: `src/GymMembership.Core/Services/Services.cs:412-422` (`PaymentRow`: `ProofText` → `HasProof ? ReferenceNo : PaysAtDesk ? "Pays at the desk" : "Paying online"`; `WaitText` → "Waiting for the member to pay online"; new `public bool CanApprove => HasProof || PaysAtDesk;`)
- Modify: the Payments-to-verify page XAML (bind the Verify button's `IsVisible` to `CanApprove`; keep its AutomationId)
- Modify: `src/GymMembership.App/Resources/Raw/terms.md` (payment paragraph: pay online through Xendit by GCash, Maya, QR Ph or card; confirmed automatically; no proof; **the terms version is not changed**, X15)
- Test: `tests/GymMembership.Tests/OnlinePaymentTests.cs`

- [x] **Step 1: Failing test.** `Staff_row_for_an_online_payment_has_no_approve`: an online pending row has `CanApprove false` and `ProofText "Paying online"`; an old proof row has `CanApprove true`. Run → FAIL.
- [x] **Step 2: Implement it; run `dotnet test`** → all pass.
- [x] **Step 3: Windows check (staff pages Windows only).** Payments to verify shows "Paying online", with no Verify on online rows and Reject still there. `scripts/Test-WindowsAdminNavigation.ps1`, `Test-WindowsArchive.ps1` and `Test-WindowsSessionRequests.ps1` still PASS.
- [x] **Step 4: Terms text through `impeccable clarify`;** confirm the terms version constant is unchanged (`git diff` shows only the text).
- [x] **Step 5: Commit** → `git commit -m "Staff payments say Paying online; terms text"`

---

## Phase 5: End to end and go-live

### Task 6: Test-mode run, break attempts, review

- [x] **Step 1: Test-mode run on the real backend with the new Windows and Android builds.** For each of a new membership, a package (extend), and an amenity down payment: Pay online → pay with Xendit's test GCash → back in the app it shows active or confirmed, with a receipt.
- [x] **Step 2: Failure runs.**
  - Let a link expire: the payment expires and the link no longer pays.
  - Desk rejects a payment, then the member pays the old link: no activation, and staff get "Online payment to refund".
  - Blank the webhook URL in Xendit, pay, and return to the app: X10 confirms it. Then restore the URL.
  - Replay a webhook from the Xendit dashboard: nothing changes.
  - POST to the webhook with a bad token: 401.
- [x] **Step 3: Break attempts.** Add cases to `supabase/tests/local/owner-fixes-break.mjs`'s pattern in `xendit.mjs` (member/desk/coach can't call service functions, can't write `xendit_id`/`xendit_url` directly, can't update their payment to `verified`). Run the full `npm test` → exit 0.
- [x] **Step 4: Reviews.** Run `ponytail:ponytail-review` and `/code-review` on the branch diff, fix what holds up, and run `superpowers:verification-before-completion`.
- [x] **Step 5: Go-live list for the owner (written into "Current work").**
  1. The Xendit account is activated.
  2. The owner sets the live secret key and token: `npx supabase secrets set ...`.
  3. The owner sets the live webhook URL in Xendit (Live mode).
  4. One real ₱ small payment, then refund it from the Xendit dashboard.
  5. Ship the new app (v0.7.0) to members and desks.
  6. The owner merges the PR.
- [x] **Step 6: Commit the plan's final state;** ask the owner before `git push` and the PR.

## Session log

- 2026-11-16: Design approved (no new terms version). Branch made. Plan written.
