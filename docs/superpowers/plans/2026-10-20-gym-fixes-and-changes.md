# Gym management fixes and changes — implementation and progress plan

Created: 2026-10-20
Last updated: 2026-10-20
Status: Phase 1 IMPLEMENTED — owner reports database migrations 0023/0024 applied live; history confirmation, Edge Function deployment and live/device acceptance pending
Branch inspected: `main`

## How to use this plan

This is the restartable checklist for the owner's requested fixes. Original request numbers are preserved; there was no item 3. There are 14 requested items, numbered 1, 2, and 4–15.

1. Work on one phase at a time, in dependency order. Before starting, read `CLAUDE.md` and the current implementation; paths below are starting points, not a requirement to keep growing large files.
2. Change the phase status to `IN PROGRESS`, fill in the current-work block, and check tasks only after they are actually finished.
3. Keep implementation, automated tests, device verification, and live deployment separate. Code written is not the same as verified or deployed.
4. Before stopping, update the current-work block, checklist, and session log. Record exact failing tests, uncommitted files, and the next concrete action.
5. Before resuming, read this file, inspect `git status --short`, check the recorded branch/commit, and continue from the next action. Do not overwrite unfamiliar changes.
6. No live database migration, real account deletion/deactivation, real payment operation, commit, push, or release is authorized merely by this plan. Schedule those separately with the owner.

Status vocabulary: `PLANNED`, `IN PROGRESS`, `BLOCKED`, `IMPLEMENTED`, `VERIFIED`, `DEPLOYED`. Use `N/A` for genuinely inapplicable verification, with a reason. A checked implementation task does not imply phase acceptance.

### Current work / restart here

- Current phase: Phase 1 IMPLEMENTED; owner reports migrations 0023/0024 applied live successfully. Migration-history confirmation, reset-password Edge Function deployment, live app verification and final device/owner acceptance remain pending.
- Current task: Implementation complete for items 1, 2 and 4; review locally before accepting the phase or starting Phase 2.
- Working branch / original base HEAD: `phase1-archive-checkpoint-2026-10-20` / `9f6ecb097ded65b59e3c6427924dd90d288e908a`. Created from the prior local `phase1-archive-accounts` branch with the Phase 1 work intact; main is not being merged or pushed.
- Checkpoint authorization: Owner explicitly requested a NEW branch and a Git commit/push to save progress before continuing other phases tomorrow. This supersedes the earlier no-push instruction for this checkpoint only; it does not authorize a merge, app release, further live deployment or Phase 2 changes. The checkpoint commit is the commit containing this handoff (inspect `git log -1 --oneline` and upstream status for its actual hash/publication). Owner separately reports 0023/0024 applied live; reset-password deployment is still unconfirmed.
- Files changed so far: Account and catalog archive services/models/demo/ViewModels; Users native menu/deletion popup; Discounts & Archive UI; catalog actions/navigation; migrations 0023/0024; reset-password archived-target guard; regression tests and Windows smoke script. Use `git status --short` for the exact uncommitted file list.
- Last command/test and result: 39 focused Phase 1 tests pass. Full xUnit: 574 passed, same 2 pre-existing failures / 576. SQL: 110 rules + 31 Phase 7 + 26 session + 22 account + 16 catalog/archive checks pass; populated-history upgrade checks pass. Windows and Android Debug builds pass. Windows isolated-demo smoke passes.
- Database migrations added/applied for this plan: `0023_account_archive.sql` and `0024_archive_catalog.sql` passed disposable tests; owner now reports both APPLIED LIVE via successful `supabase db push --linked` output. Independently confirmed remote history after the push: pending. The reset-password Edge Function change is NOT yet reported deployed.
- Remaining verification: post-push linked migration list; separate reset-password function deployment to the confirmed project; live-backed app smoke checks using designated test records; Android touch/device interaction (adb not on PATH); Windows keyboard/screen-reader/narrow/light-dark checks and owner acceptance. No claim that old signed URLs or Supabase Auth sessions are revoked by profile deactivation.
- Next concrete action: Run `npx supabase migration list --linked` to confirm 0023/0024 in both columns; deploy `reset-password` to the confirmed project separately, then verify the updated live-backed app. Do not reapply migrations, start another phase, commit or Git-push automatically. Resolve D3 before Phase 2.
- Safe stopping point: Phase 1 implementation/evidence and this restart plan are included in the owner-authorized checkpoint on `phase1-archive-checkpoint-2026-10-20`. Verify `git status -sb` and `git log -1 --oneline` when resuming to confirm commit/upstream state. No further phase work is started. Optional project skill/review integrations were unavailable; review was author self-review only.

## 1. Repository findings and guardrails

The README is older than several current changes. Use the code, current migrations, and newer specs as the implementation baseline; do not use old README test counts as current evidence.

| Area | Existing baseline inspected | Planning consequence |
| --- | --- | --- |
| Architecture | .NET 10 MAUI/XAML with shared Core models, services, ViewModels; `ISupabaseGateway` selects live or demo behavior in `MauiProgram.cs` | Implement business rules in the database and mirror them in DemoGateway; UI hiding alone is insufficient. |
| Accounts | `UsersPage.xaml` has four inline actions; `AdminService` has `SetActiveAsync`, role management, password reset | Add the overflow menu and audited soft-delete lifecycle; do not delete Supabase Auth identities or physically relocate linked rows. |
| Archive/catalog | `DeletePackageAsync` and `DeleteDiscountAsync` currently call physical delete | Reconcile these paths with archive behavior so history is not lost. |
| Payments | `void_payment` already requires `payments:void` and stores actor, timestamp, reason. Initial role grants reserve it to admin | Verify current effective policies and latest function definitions, then fix capability-driven UI and any bypasses rather than assuming the server has no authorization. |
| Member cancellation | Migration `0019` allows withdrawal of one's own pending membership/renewal payment; Phase 7 adds cancellation/refunds | Clarify admin cancellation versus member withdrawal/refund before changing permissions. |
| Revenue | `RevenueAsync` returns grouped day/kind/method summaries, not individual receipts. `0020` deducts refunds on approval in Asia/Manila dates | A receipt-level PDF needs a matching authorized detail query and reconciliation, not just a screenshot of summary rows. |
| Amenities | Catalog/purchase services and `avail_amenity` exist; board/status/usage flows also exist | Diagnose UI discoverability, data, entitlements and RLS first. New scheduled reservations must not remain dependent on manual amenity usage confirmation. |
| Scheduling | Requests/sessions, shift posts, calendar, and open availability exist. Latest local migration is `0022_active_session_request_guard.sql` | Extend current workflows. New migration numbers must follow the actual latest migration when work starts; never rewrite applied migrations. |
| Booking guard | `2026-10-18-active-session-request-guard.md` protects one active scheduled booking per coach/member pair until explicitly resolved | Multiple time slots and automatic ending need an explicit compatibility decision before changing the guard. |
| Coach Overview | `2026-10-18-coach-overview.md` specifies a read-only Windows Overview; Android has different navigation | New calendar shift posting revises that design. Confirm the replacement and platform scope rather than silently mixing designs. |
| Coach profile | Hourly-rate value, hiring toggle, and a `Profile saved.` notice already exist | Correct parsing/formatting/validation and make existing feedback clear; do not duplicate profile storage. |
| Presence | Chat last-seen / online state exists | Physical gym check-in is a separate concept; never use chat presence or a posted shift as proof the coach is in the gym. |

Primary existing files:

- `src/GymMembership.Core/Models/Models.cs`, `Models/SubscriptionModels.cs`
- `src/GymMembership.Core/Services/Services.cs`, `Services/Foundation.cs`, `Services/SubscriptionService.cs`, `Services/AttendanceCalendar.cs`
- `src/GymMembership.Core/ViewModels/StaffViewModels.cs`, `ViewModels/MemberViewModels.cs`, `ViewModels/CoachOverviewViewModel.cs`
- `src/GymMembership.Core/Backend/SupabaseGateway.cs`, `Demo/DemoGateway.cs`, `Demo/DemoSubscriptions.cs`
- `src/GymMembership.App/Views/`, role Shells, `MauiProgram.cs`, platform services
- `supabase/migrations/`, `supabase/functions/`, `supabase/tests/local/`
- `tests/GymMembership.Tests/`

Follow the project's minimal-diff/native-first workflow. Use available project skills for design, UI, debugging and review as directed by `CLAUDE.md`; if a named integration is unavailable, record that limitation instead of pretending it ran. Keep new services/models small and feature-specific; do not build a generic archive or scheduling framework.

## 2. Decisions to settle before dependent implementation

Recommendations are proposals, not owner-approved requirements. Record the answer and date beside each decision; block only phases affected by an unresolved answer.

- [x] **D1 — deletion reason and safeguards (owner approved 2026-10-20):** Admin-only deletion with a mandatory trimmed reason of 1–500 characters. Block self-deletion and loss of the last active admin. Block deletion while the target has any scheduled sessions (including overdue unresolved sessions), active coaching hires, or pending payments; staff must resolve these first. Preserve history; no automatic refunds/cancellations. Owner confirmed admin-only behavior and explicitly requested no push.
- [x] **D2 — archive location and types (owner approved 2026-10-20):** Use separate Discounts and Archive tabs in the admin area. Initial archive types: inactive/deleted accounts, discount codes, gym-plan catalog entries, and amenities. Expired member subscription terms remain in membership history, not this archive. Combined Archive UI and catalog lifecycle are now implemented locally. Future archive types are not invented; expired membership terms stay in membership history.
- [ ] **D3 — payment authority:** Recommend reuse of `payments:void`, plus a narrowly scoped admin-cancel capability only if needed. Existing member withdrawal of an unverified own payment and refund requests stay unless explicitly superseded. If all cancellation must become admin-only, record that policy change and update the existing flows/tests. Decide how permitted versus unpermitted admins are configured; no new higher role unless actually needed. Answer: ___
- [ ] **D4 — access-code meaning and activation:** Confirm these are existing discount codes, not a new access/invitation-code system. Recommend one successful verified redemption per distinct member; a pending discounted checkout temporarily reserves capacity, then consumes it on verification and releases it on rejection/expiry/cancellation. Decide refund/void effects; recommend consumed redemptions stay consumed to prevent reuse abuse. Alternative policies require corresponding tests. Answer: ___
- [ ] **D5 — limits and restoration:** Recommend positive integer caps, no lowering below already-consumed plus reserved capacity, and no normal reactivation of exhausted codes. Decide whether raising a cap can ever unlock an exhausted code; default to no, issue a new code. Decide how existing unlimited codes are backfilled without breaking pending payments. Answer: ___
- [ ] **D6 — slot and payment contract:** Agree slot increment (recommend 30 minutes), maximum booking duration, advance-booking window, operating hours, and whether overnight bookings are supported. With one unit per amenity, each grid column represents a different amenity, not invented extra courts. Decide entitlement/price/session-credit rules, and whether independent amenity bookings need a coach. Answer: ___
- [ ] **D7 — slot grouping and same-pair guard:** Recommend adjacent selected slots form one continuous reservation with cooldown only after its end; nonadjacent selections become separate reservations and must independently pass all checks. Decide all-or-nothing multi-selection confirmation. Explicitly retain or revise the `0022` active coach/member guard for future multiple bookings and automatic end behavior. Do not quietly remove its protection. Answer: ___
- [ ] **D8 — training lifecycle:** Decide proposal expiry, how Request Another Time is routed, whether coach/amenity changes require fresh approval from a different coach, and how auto-ended usage relates to training completion/no-show/attendance. Recommend separate reservation occupancy state and attendance outcome. Answer: ___
- [ ] **D9 — coach availability and presence:** Define who checks coaches in/out (coach self-service, front desk, or both), missed-checkout policy and freshness target. Recommend hiding absent coaches only from discovery/new booking, not erasing existing hires, chats or confirmed bookings. Decide whether physical presence is required only for listing or also for same-day confirmation; don't make future confirmed bookings vanish at checkout. Answer: ___
- [ ] **D10 — coach dashboard and interaction count:** Confirm Windows and Android scope and supersession of the read-only Overview. Define “three interactions”: recommend calendar slot click → prefilled form/review → Post for default duration; edits to hours or an optional note add interactions. Arbitrary entry of every field cannot truthfully be promised in three taps. Request actual mockup images before claiming visual parity. Answer: ___
- [ ] **D11 — revenue PDF detail:** Recommend individual receipt/payment entries plus refund-adjustment lines reconciling to the current net revenue total, with filters shared by screen/export. Confirm line-item versus grouped summary expectation, extra filters, destination/share behavior and PDF library license. Answer: ___
- [ ] **D12 — QR trust boundary:** Recommend a permanent random opaque identifier mapped to user ID, an authenticated staff scanner, online status lookup, and no embedded name/status/expiry/credentials. Confirm whether QR must literally encode user ID, who can scan, logging retention, expired/guest/pending precedence, and unknown-code behavior. Permanent QR is identification, not proof of identity or payment. Answer: ___

## 3. Phases and request coverage

| Phase | Scope | Original items | Dependencies | Status |
| --- | --- | --- | --- | --- |
| 0 | Decisions and verified baseline | All | None | IN PROGRESS — D1/D2 approved, baseline recorded; other phase decisions pending |
| 1 | Archive foundation, account deletion, overflow menu | 1, 2, 4 | 0; D1–D2 | IMPLEMENTED locally — automated checks + Windows demo smoke verified; remaining device/owner acceptance pending |
| 2 | Payment authorization and history | 5 | 0; D3 | PLANNED |
| 3 | Code activation caps and archive integration | 7 | 1; D4–D5 | PLANNED |
| 4 | Revenue PDF export | 6 | 2; D11 | PLANNED |
| 5 | Purchasable amenity catalog and coach availability | 10, 11 | 0; archive integration from 1; D6 | PLANNED |
| 6 | Reservation engine and booking grid | 8 | 5; D6–D8 | PLANNED |
| 7 | Structured training requests and rescheduling | 9 | 6; D7–D8 | PLANNED |
| 8 | Physical coach presence and discovery | 12 | 5; D9 | PLANNED |
| 9 | Coach dashboard, shifts and profile polish | 13, 14 | 5–8; D10 | PLANNED |
| 10 | Permanent member QR and scan logging | 15 | 1; D12 | PLANNED |
| 11 | Integrated regression, deployment and handoff | All | 1–10 | PLANNED |

Dependency order matters more than numbering. After Phase 0, independent work can be scheduled separately, but keep one phase's changes and evidence identifiable. The coach rate correction can be pulled forward as a small independent slice if desired; record that move here.

## Phase 0 — Resolve policy and capture the baseline

- [x] Inspect account UI, admin services, payment authorization, revenue summary, scheduling specs and local migration list for planning.
- [x] Resolve decisions needed by the first implementation slice: D1/D2 approved. Other decisions remain pending for their dependent phases.
- [ ] Complete all design/reference checks. Current project instructions and relevant account/scheduling code were read; optional project skill loads failed and reference images were not supplied.
- [ ] Complete SDK/workload inventory. Feature branch `phase1-archive-accounts` created from HEAD `9f6ecb097ded65b59e3c6427924dd90d288e908a`; initial dirty state was only this untracked plan. Both platforms build with installed tooling; explicit SDK/workload listing still pending.
- [x] Run baseline full xUnit and disposable local database suites: 533 passed/2 failed of 535 xUnit; 110 rules + 31 Phase 7 + 26 session checks and populated-history migration checks passed.
- [x] Build baseline Windows and Android successfully; existing media-picker deprecation warning and unrelated xUnit1031 analyzer warning recorded.
- [ ] Audit current migration/deployment state with the owner. Local files up to `0022` do not by themselves prove live state.
- [ ] List migration backfills and compatibility risks: pending payments/codes, active accounts, existing reservations and sessions, existing catalog prices.

Exit: baseline evidence recorded, first-slice decisions approved, no accidental production changes.

## Phase 1 — Archive, soft-delete accounts and simplify account actions

Status: IMPLEMENTED locally for items 1, 2 and 4. Account deletion/menu and the complete four-type Archive/filter/restore lifecycle are implemented and locally tested. Windows demo smoke checks pass. This is not a deployment claim: Android interaction, full accessibility/layout checks and owner acceptance remain unchecked below.

Starting points: `UsersPage.xaml` and its code-behind; `UsersViewModel`; `IAdminService`/`AdminService`; Profile/catalog models; role Shell navigation; `DiscountsPage.xaml`; migrations for helpers, RLS and functions.

- [x] Add archive lifecycle metadata for all supported types. Deleted accounts retain deletion actor/time/reason; ordinary inactive accounts use existing deactivation audit evidence. Catalog records have server-stamped `archived_at`, `archived_by`, `archive_reason`. Canonical rows and foreign keys remain intact.
- [x] Implement authorized archive/restore for accounts, codes, plans and amenities. Admin-only list/restore RPCs recheck current state; expired codes cannot restore or have expiry edited to bypass Archive. Direct catalog deletion is revoked and stale edits/reactivation of archived rows are rejected.
- [x] Review protected archived-identity entry points: app sign-in, existing-session table/RPC denial, profile/role/coach writes, reset-password target guard, gym assets/storage policies and demo notification writes. Own profile status remains readable by design. No service-role credentials were added. Existing Auth identities/sessions and previously issued signed file URLs are retained, not falsely claimed revoked; live Edge Function/session behavior needs deployment verification.
- [x] Resolve account deletion commitments according to owner-approved D1: reject deletion while scheduled sessions, active coaching hires or pending payments exist. No automatic cancellation/refund. Test both payment/deletion race directions.
- [x] Replace the four account buttons with a row-scoped native three-dot dropdown on Windows/Android. Preserve existing role/password/activation actions; include Delete Account only for admin access.
- [x] Implement the confirmation popup with target account, multiline required reason, red Confirm and No, Cancel. Disable invalid/busy confirmation and preserve reason/target on server error. Physical device checks remain unchecked below.
- [x] Add Discounts and Archive tabs under admin Discounts & Archive. Users and catalog management show active records; inactive/deleted accounts and inactive/expired catalog records have a destination Archive.
- [x] Add combined type/status/date/reason filters, one-action Clear filters, no-results/loading/error states and bounded server-side pagination (30 rows + look-ahead). Date filtering uses Asia/Manila; edits to filters do not silently alter next/previous page queries until Apply.
- [x] Add read-only details and explicitly confirmed admin Restore UI. Records have labels only, not editable fields. Failed restore retains selection and error; successful restore refreshes results. Expired codes have no restore control and the server independently refuses them.
- [x] Replace package/code physical deletion with soft-archive RPCs; preserve used-plan and payment history. Catalog deactivation receives server metadata; active lists remove archived records. Labels no longer promise irreversible deletion.
- [x] Preserve honest legacy data. Existing inactive catalog records keep unknown dates/actors/reasons rather than invented timestamps. Existing deleted accounts use saved deletion metadata; ordinary deactivation uses known audit history where available. Expired codes derive archive status/date from actual expiry. New inactive records are server-stamped. Disposable pre-0023/pre-0024 data tests pass.
- [x] Complete demo parity and regression tests for all four types, immutable archived rows, permission loss, filters/pagination, restore confirmations/errors, history retention and RPC JSON mapping.

### Slice 1 implementation and evidence — 2026-10-20

- [x] Owner approved D1/D2 and confirmed admin-only deletion. Explicit no-push instruction recorded.
- [x] Added `AccountArchiveService`, `AccountDeletionViewModel`, `DeleteAccountPopup` and `DemoAccountArchive` using existing MAUI/toolkit/native menu patterns.
- [x] Added local migration `0023_account_archive.sql`: admin-plus-permission authorization; required trimmed reason; self/last-admin protection; commitment checks; audit-preserving soft delete/restore; ordinary reactivation cannot bypass restore.
- [x] Added profile-lock commitment guards so new pending payments, active hires and scheduled sessions cannot be attached after deletion wins a concurrent transaction. Serialized admin lifecycle operations recheck the caller after waiting.
- [x] Added 23 focused account/ViewModel tests, all passing. The initial 11 account RPC tests failed before implementation, then passed.
- [x] Added 22 disposable PostgreSQL checks, all passing, including populated pre-0023 inactive data, permission bypass attempts and separate-connection concurrency. Included the suite in `supabase/tests/local/package.json`.
- [x] Full xUnit run: 557 passed, 2 failed, 559 total. Both failing test names match the baseline failures recorded in the session log; no newly failing tests remain. New XAML also adds automatic binding coverage.
- [x] Existing SQL regression suites pass: 110 rules, 31 Phase 7, 26 request checks, plus the populated-history migration suite. The account suite also passes after the final whitespace-normalization change.
- [x] Windows and Android compile checks run; see evidence tracker/session log for final status and existing warnings.
- [ ] Perform physical Windows/Android interaction, keyboard, screen-reader, narrow-screen and light/dark checks. Builds and source assertions are not device verification.

### Remaining slice implementation and evidence — 2026-10-20

- [x] Implemented `ArchiveRecord`, `ArchiveService`, `ArchiveViewModel`, `ArchivePanel`, Discounts/Archive tabs and admin navigation/overview labels.
- [x] Added `0024_archive_catalog.sql`: immutable archived catalog, no physical authenticated catalog deletes, server-stamped lifecycle metadata, admin restore/list, combined server filters/pagination, expired-code restrictions, archived role/profile guards and restrictive active-account storage policy.
- [x] Replaced package/code hard-delete service paths; active catalog/user lists exclude archived records. Historical entitlement/payment rows remain intact. Mirrored behavior in `DemoArchive` and DemoGateway.
- [x] Added archived-target guard to reset-password Edge Function; code saved but not deployed. Auth password reset is an external Auth operation, not claimed transactionally atomic with an independent concurrent archive.
- [x] 39 focused account/archive/ViewModel/transport tests pass. Full xUnit: 574 passed / 576, only the same two baseline failures below. Updated former hard-delete expectations to retained soft-archive behavior.
- [x] Final complete SQL chain passes: 110 rules, 31 Phase 7, populated-history migration checks, 26 session request, 22 account archive and 16 catalog archive checks.
- [x] Final Windows/Android Debug builds pass. Existing PickPhotoAsync deprecation and chat-test xUnit1031 warnings remain.
- [x] `scripts/Test-WindowsArchive.ps1` passes in an isolated copied demo without backend configuration: native row menu; blank-reason disabled Confirm; entered-reason enabled Confirm; Cancel; catalog archive; archive no-results and Clear; read-only details/confirmed restore/removal from Archive; hidden front-desk archive navigation.
- [ ] Android actual UI and broader accessibility/keyboard/narrow/light-dark verification. No adb was found on PATH; a successful Android build is not a touch/device test.
- [ ] Independent review and owner acceptance; optional project skill integrations remain unavailable.

Acceptance / verification for the COMPLETE phase:
- [x] Windows smoke confirms Cancel closes without executing deletion, and required reason controls Confirm. Unit tests cover busy/error retention and command safeguards.
- [x] Reviewed current protected access paths and tested historical retention. Keep explicit limitations: retained Auth identities/sessions, previously issued signed URLs and undeployed Edge Function behavior are not instantaneous credential revocation.
- [x] Account invalid restore, self-deletion and loss-of-last-admin attempts fail server-side; restore bypass is denied.
- [x] Combined type/status/date/reason behavior verified in SQL/demo tests; Clear/empty/restore verified in Windows demo UI.
- [ ] Repeat combined-filter/dropdown interaction on Android and keyboard/touch/narrow/accessibility checks on both platforms.
- [ ] Full-phase owner acceptance recorded; live deployment remains separately authorized.

## Phase 2 — Permission-gated payment void/cancel

Starting points: `VoidPaymentsPage.xaml`, payment pages/Shell routes, `VoidPaymentsViewModel`, `PaymentStaffService`, subscription cancellation/refund code, latest definitions of `void_payment` and `cancel_pending_payment`, permission helpers/grants.

- [ ] Inventory all void, cancellation, withdrawal and refund entry points, including direct table writes, RPCs, deep links and demo behavior.
- [ ] Reuse effective `payments:void` capability for permitted admins; implement the agreed capability for administrative cancellation if distinct. Do not equate every admin label with permission if selective grants are required.
- [ ] Hide unauthorized controls and navigation entries entirely; handle permission revocation while a page is open. Direct commands/API calls still enforce server authorization.
- [ ] Preserve or explicitly revise member pending-payment withdrawal and refund requests according to D3; do not inadvertently disable valid member workflows.
- [ ] Retain and verify required reason, actor, server timestamp, audit event and voided history. Ensure admin cancellation has equivalent audit data.
- [ ] Keep receipt history visible with voided status. Preserve existing entitlement reversal and refund-vs-void race protection; no double reversal.
- [ ] Add permission matrix and concurrency tests for allowed admin, unpermitted admin, employee, coach, member and archived account.

Acceptance / verification:
- [ ] Unauthorized controls are absent, not greyed out; forged requests are rejected.
- [ ] Each permitted operation records who/when/why and remains visible in history.
- [ ] Repeating/racing void, refund and cancellation cannot reverse a payment twice.
- [ ] Member cancellation/refund behavior matches the explicitly approved policy.

## Phase 3 — Capped access/discount-code activation

Starting points: Discount model, `DiscountsViewModel`, `DiscountsPage.xaml`, checkout/payment RPCs, DemoGateway, Phase 1 archive.

- [ ] Implement the D4 activation definition with a durable redemption ledger and positive maximum count. Use existing discount infrastructure if these are discount codes.
- [ ] Add create/edit cap input, name/code, and usage display such as `1/100`. Clearly distinguish confirmed usage from reserved pending capacity where applicable.
- [ ] Enforce cap and once-per-member behavior transactionally with locking/constraints. Repeated callbacks/verification/retries must be idempotent; concurrent requests cannot claim the last place twice.
- [ ] Reserve/release capacity for pending payments if using the recommended policy, including expiry jobs and cancellation/rejection paths. Never sell a discounted payment that cannot later be honored.
- [ ] On final consumption, mark inactive and archive atomically with `limit reached` reason; expiry and manual deactivation use distinct reasons.
- [ ] Permit manual deactivate/restore only for nonexhausted, otherwise-valid codes. Exhaustion cannot be bypassed through edit/reactivate or direct row updates; cap-change policy follows D5.
- [ ] Backfill existing codes/redemptions, decide pending-checkout treatment and avoid retroactively changing paid amounts.
- [ ] Widen or wrap the Deactivate label, or use an approved shorter label, on narrow screens.
- [ ] Add demo/unit/SQL tests for caps, distinct users, release/retry paths and archive integration.

Acceptance / verification:
- [ ] Last-place race yields only one success and no count above cap.
- [ ] Usage matches the ledger after verification, cancellation, expiry, retry, void and refund under the chosen policy.
- [ ] Exhausted codes are inactive, archived and not normally restorable; manual nonexhausted deactivation is reversible.
- [ ] Admin cap editing validates correctly and button text is legible on both platforms.

## Phase 4 — Revenue export as PDF

Starting points: `RevenuePage.xaml`, `RevenueViewModel`, `AdminService.RevenueAsync`, `RevenueRow`, latest `revenue_summary` (`0020`), platform save/share services.

- [ ] Define one report request/snapshot shared by screen and export: loaded date range, all active filters, timezone and generation timestamp. Do not export stale rows under newly edited filter labels.
- [ ] Add a permission-protected, fully paginated line-item query using the same accounting semantics as revenue_summary; include refund adjustments and reconcile totals. No raw-table client workaround or silent query-limit truncation.
- [ ] Select a maintained PDF library compatible with .NET 10, Android/Windows, packaging and acceptable licensing. Record selection before adding a dependency.
- [ ] Add `Export PDF` and a focused report-generation/save/share service; keep platform file handling out of shared business logic.
- [ ] Render title, date range, filter description, generated date/time with timezone, totals and line-item table. Repeat headers, paginate long tables and include receipt/status details appropriate to D11.
- [ ] Use decimal money and explicit PHP formatting (`₱1,234.56`) with an embedded font supporting the peso glyph; format refund adjustments consistently.
- [ ] Handle empty reports, invalid ranges, busy state, export failures, permission denial and cancelled share/save without a false success message.

Acceptance / verification:
- [ ] PDF totals reconcile to the filtered on-screen report, including refund approval dates and void treatment.
- [ ] Empty, one-page and multi-page exports have readable peso symbols and no clipped rows.
- [ ] Export/save/open or share works on Windows and Android; verify filters, timezone boundaries and full result coverage.

## Phase 5 — Purchasable amenities and coach-opened availability

Starting points: `AmenitiesPage.xaml`, `CatalogAdminPage.xaml`, `CoachesPage.xaml`, amenity/catalog ViewModels and services, `avail_amenity`, `ShiftPost`, request/open-slot code, catalog/RLS policies.

- [ ] Reproduce why members currently cannot buy amenities: inspect active catalog data, member/guest gates, navigation, entitlement mapping and RLS. Do not duplicate the existing purchase service.
- [ ] Support admin creation/editing of purchasable amenity products with price, description, active state and relevant service/coach mapping. Define the difference between a product entitlement and its physical amenity resource.
- [ ] Display purchasable items to eligible users with explicit Buy action and existing payment/proof/verification workflow. Show an explanatory empty state, not a blank page.
- [ ] Define booking rights after purchase; pending/rejected/voided payments must not grant usable entitlement. Refund/archive rules must not silently erase prior purchases.
- [ ] Add coach availability blocks associated with the applicable amenity/service. Define how shifts and bookable availability relate; don't maintain two conflicting calendars.
- [ ] Support authorized add/edit/remove within operating hours. Bookings must be wholly inside valid availability; overlapping blocks are rejected or normalized under a documented rule.
- [ ] Reject or safely constrain availability edits/removals that would invalidate confirmed bookings; explain affected bookings. A coach cannot silently cancel members by deleting an availability block.
- [ ] Expose only safe availability details to members; do not leak other members' information through grid reads.
- [ ] Mirror behavior in demo mode and add purchase/entitlement/availability authorization tests.

Acceptance / verification:
- [ ] Admin creates a product, user sees and buys it, verified payment grants the agreed entitlement.
- [ ] Zero items shows a useful empty state, while load failure shows an error rather than pretending the catalog is empty.
- [ ] Users cannot book unopened hours or manufacture coach availability; changes preserve confirmed bookings.

## Phase 6 — Atomic reservations, cooldowns and slot grid

Starting points: Phase 5 contracts, training/amenity models and RPCs, member amenity UI, existing cron approach. Schema/service names below are design responsibilities, not preexisting files.

- [ ] Add a reservation model linked to member, amenity, coach/service when applicable and entitlement, with start/end, cancellation and audit metadata. Keep occupancy lifecycle distinct from training attendance as agreed in D8.
- [ ] Implement one capacity unit per amenity. Different amenities have independent occupancy; member/coach overlap rules still apply. “Different amenities can run together” does not permit the same coach/member to be in two places at once.
- [ ] Use UTC storage and server time; display/grid by agreed gym timezone and operating hours. Validate past times, ordering, increments, maximum duration and booking horizon server-side.
- [ ] Enforce overlap/cooldown and entitlement checks atomically at confirmation using a suitable exclusion/locking strategy, not only a pre-check in UI. Include direct-write protections and a consistent lock order.
- [ ] For each amenity, protect `[start, end + 30 minutes)` for confirmed usage. First use has no pre-cooldown; idle time of at least 30 minutes needs no extra delay. Exact-boundary booking at prior end + 30 minutes is allowed.
- [ ] Cancellation before use starts removes that reservation's occupancy and cooldown, without removing cooldown owed by an earlier session. Agree treatment of in-progress early cancellation; preserve cooldown after actual usage ends.
- [ ] Derive in-use/ended availability from server timestamps so it is correct even if all apps are closed or a scheduled job runs late. Use idempotent jobs for persisted transitions/events and preserve the distinction from attendance marks.
- [ ] Add coach-recorded extension operation: authorize owning coach, validate positive extension and coach/member/amenity availability, shift cooldown, and reject any collision with the next reservation plus required buffer. Make extensions atomic against simultaneous booking.
- [ ] Build the dated time-slot grid with one column per amenity, selectable open cells, BOOKED cells and visually distinct nonselectable cooldown blocks. Support the approved adjacent/nonadjacent selection rules and `Book Selected Slots`.
- [ ] Recheck everything at confirmation; refresh after conflict and retain a useful selection/error state without claiming success.
- [ ] Update both schedules and issue transactional notification events on confirmed changes. Refresh visible grids on relevant updates or fallback refresh/reconnect.

Acceptance / verification:
- [ ] Two clients confirming the same amenity/time produce one booking; different amenities can be reserved concurrently by nonconflicting participants.
- [ ] No reservation can be inserted during another's cooldown; exact 30-minute boundary succeeds.
- [ ] First-use, long-idle and pre-start cancellation cases impose no phantom cooldown.
- [ ] Start/end occurs without manual check-in/confirmation and without an open client; delayed jobs don't expose occupied slots as free.
- [ ] Extension-vs-booking races cannot overwrite the next reservation; coach/member conflicts are rejected.
- [ ] Booked/cooldown cells cannot be selected; multi-slot submission follows D7 without partial surprise bookings.

## Phase 7 — Structured training requests, proposals and reschedules

Starting points: `RequestsPage.xaml`, `SessionsPage.xaml`, related ViewModels, `ISessionService`, `TimeRequest`/`TrainingSession`, migrations `0021`/`0022`, Phase 6 reservation operations.

- [ ] Add structured request fields: training service, coach, amenity, date, start and end. Pending requests do not consume confirmed reservation capacity; any temporary hold policy must be separately approved.
- [ ] Implement server-enforced state transitions and actor authority:
  - Pending Coach Approval → Confirmed when the coach approves the original request and confirmation checks pass.
  - Pending Coach Approval → Awaiting User Response when the coach proposes a structured alternative with a required reason.
  - Awaiting User Response → Confirmed only on explicit acceptance of the current proposal by the target user.
  - Awaiting User Response → Pending Coach Approval when the user submits Request Another Time with the agreed structured fields.
  - Pending/Awaiting → Declined or Cancelled by the authorized actor, with required reasons for declines.
  - Confirmed cancellation/rescheduling uses the reservation lifecycle, not silent request-field mutation.
- [ ] Store proposal version and complete date/start/end/coach/amenity/reason. A stale Accept or duplicate response must not confirm a different or already-resolved proposal.
- [ ] Chat remains communication only: sending “yes” or discussing another time does not mutate schedule/status.
- [ ] At each relevant review/confirmation, check active account/membership/hire/entitlement, posted coach availability, operating hours, member and coach overlap, amenity occupancy and cooldowns.
- [ ] At final approval/acceptance, recheck atomically through Phase 6. If unavailable, keep the request unresolved with a clear conflict message; never show a false Confirmed state.
- [ ] Reschedule by linking a proposal to the existing confirmed booking. Retain the original reservation until replacement approval and perform the swap atomically. Failure/decline/cancellation of the proposal leaves the original untouched.
- [ ] Enforce the D7 decision on `0022` same-pair guard without allowing duplicate new bookings or blocking legitimate replacement proposals.
- [ ] Append every transition/proposal/confirmation failure that affects workflow to an authorized history record with actor, server timestamp, from/to state and reason. Keep history append-only.
- [ ] Notify both parties and refresh both schedules from committed state; failed transactions send no success notification. Retried operations do not duplicate booking or event history.

Acceptance / verification:
- [ ] Original approval, counterproposal acceptance, Request Another Time, decline, cancellation and stale acceptance paths work as specified.
- [ ] No pending request or chat message alone becomes a reservation.
- [ ] Competing acceptances or a newly taken slot leave one valid booking and unresolved conflicting requests.
- [ ] Rescheduling retains the original until a successful atomic replacement.
- [ ] Transition history and notifications identify the correct actor/time on both clients.

## Phase 8 — Physical coach check-in and live discovery

Starting points: `CheckInPage.xaml`, `IAttendanceService`, `CoachService.ListCoachesAsync`, coach discovery/request pickers, existing realtime lifecycle patterns. Member check-in and messenger presence are not automatically coach attendance.

- [ ] Add or extend explicit coach gym-visit records with server check-in/out timestamps, actor and one-open-visit constraint. Define authorized front-desk/self-service controls according to D9.
- [ ] Make check-in/out operations idempotent, audit changes and handle stale/missed checkout under the approved policy.
- [ ] Filter new-discovery/new-booking coach lists to active accounts with an open physical visit; absent coaches are hidden, not greyed out.
- [ ] Keep physical presence, taking-new-members flag and future availability separate. Follow D9 for current hires, historical records and future-booking rules.
- [ ] Refresh on check-in/out events with scoped subscription cleanup, reconnection reload and a fallback refresh. State the agreed freshness target; an unknown/offline presence state must not invent a check-in.
- [ ] Recheck any required presence condition server-side for new hire/booking, so stale clients cannot bypass it.

Acceptance / verification:
- [ ] A coach appears after check-in and disappears after checkout within the agreed freshness target on a second client.
- [ ] Chat online/last-seen and posted shifts do not make an absent coach appear.
- [ ] Existing conversations and confirmed bookings remain accessible; reconnect and duplicate check-in/out behave safely.

## Phase 9 — Coach dashboard, shift posting and profile rate

Starting points: `CoachOverviewViewModel`, coach Overview view/navigation, `AttendancePage.xaml` and calendar helpers, `ShiftsPage.xaml`, `ShiftsViewModel`, `CoachProfilePage.xaml`, `CoachProfileViewModel`, `CoachService`.

- [ ] Apply the approved redesign replacing/supplementing the earlier read-only Overview, with Sessions this month, Attendance rate and Upcoming cards, month/week calendar and session list. Reuse established attendance denominator rules; no data is not 0%.
- [ ] Allow calendar-based shift posting with prefilled Date/Start, Hours dropdown, optional Note (`What you will be running`) and `Post shift`.
- [ ] Validate operating hours, duration, past times, overlap and availability consistency on both client/server. Calendar shift edits must not invalidate confirmed reservations.
- [ ] Preserve the exact `No shifts posted. Add one above.` empty state when appropriate, and distinguish load errors from empty data.
- [ ] Meet D10's documented default three-interaction path; manually count the interactions on Windows and Android rather than promising arbitrary full form entry in three taps.
- [ ] Standardize coach rate display/input to peso with two decimals (`₱30.00`) and clear `Hourly rate`/`per hour` label. Separate editable numeric value from decorated display and never rely on ambient decimal separators.
- [ ] Validate positive decimal rate in ViewModel/service/database; decide backfill of existing null/zero rates. Reject invalid, negative, zero and overprecision values under the agreed money rules.
- [ ] Make Taking new members state explicitly On/Off and keep it independent of gym presence. Preserve user edits on validation failure and show save success only after successful persistence.
- [ ] Verify light/dark, keyboard accessibility, narrow Windows and Android layouts, and regression of current navigation/calendar behavior.

Acceptance / verification:
- [ ] Cards, month/week toggle, session list and calendar shift posting work under the approved platform scope.
- [ ] Default shift posting meets the counted interaction target and rejects invalid times/overlap.
- [ ] Rates render and persist correctly under en-PH/en-US and comma-decimal device cultures, with a visible peso symbol and positive-value validation.
- [ ] Profile On/Off and save confirmation are unambiguous, including server-failure handling.

## Phase 10 — Fixed member identity QR and audited scan result

Starting points: member profile/membership views, check-in UI/services, Profile and membership-term models, Phase 1 archive, database RLS/RPCs, platform camera integration.

- [ ] Create a permanent per-user scan identity according to D12; backfill existing users and assign it on signup without regenerating it on renewal/status changes. Keep the mapping through archive/restore.
- [ ] Render the same QR payload on every device for that user. This is separate from the gym's e-wallet payment QR; never replace payment QR behavior.
- [ ] Implement an authenticated authorized scanner/lookup endpoint that derives current status from current membership data, not cached data embedded in the QR.
- [ ] Define deterministic status precedence: archived/deleted → Inactive; otherwise active term → Active; otherwise eligible pending purchase → Pending; otherwise Expired/no active membership under the approved guest label. Show expiry from the relevant term or a meaningful not-applicable value.
- [ ] Show permitted staff the name, status and expiry date. Handle archived identities as Inactive, malformed/unknown code as a clear not-found result, and offline failure as unavailable rather than Active.
- [ ] Log authorized scans with server timestamp, scanner actor, stable subject identity and result; apply audit read permissions/retention. QR possession does not authorize arbitrary profile lookup.
- [ ] Add scanning camera permission/error handling and a manual lookup fallback if approved. Scanning should not silently create a gym check-in unless separately specified.
- [ ] Test copied QR privacy/authorization limits, fixed identity across renewal/expiry/pending/archive/restore and scan-log completeness.

Acceptance / verification:
- [ ] QR content stays identical while its live lookup changes Active → Expired/Pending and archive → Inactive.
- [ ] Scan result includes name/status/expiry appropriately; archived accounts do not throw an error.
- [ ] Unauthorized lookup is denied; successful/recognized inactive scans are auditable.
- [ ] Camera scanning works on supported Android/Windows devices; offline/denied-camera/unknown-code paths have clear messages.

## Phase 11 — Integrated regression and staged rollout

- [ ] Run all xUnit tests, including existing account, subscription/refund, payment, request guard, calendar, chat, navigation and XAML-binding checks.
- [ ] Run the full disposable local PostgreSQL suite, populated-history migration tests, permission/RLS denial tests and real separate-connection concurrency tests.
- [ ] Test fresh schema and upgrade from populated current data. Review migration lock duration/backfills and privileges; no destructive cleanup of history.
- [ ] Build Android and Windows; run demo and staging/live-configured smoke checks without leaking project secrets or modifying the owner's config.
- [ ] Walk through admin archive/restore, capped-code checkout, purchase-to-entitlement, coach availability, simultaneous booking, structured rescheduling, presence filtering, PDF export and QR lifecycle end to end.
- [ ] Verify app-closed timing transitions, network failure/retry, duplicate taps, reconnect, multiple devices and timezone/day-boundary cases with an injectable/fixed clock where appropriate.
- [ ] Review security and native-first/minimal-complexity implementation. Record actual reviewer/tool used; don't label self-review as independent review.
- [ ] Update PRD, technical/database specs, diagrams and README to match shipped behavior and new permissions/dependencies.
- [ ] Review backups, staging rehearsal, server-before-client compatibility, safe rollout and rollback/forward-fix plan. Apply live migrations only with separate authorization and record actual versions/results.
- [ ] Owner verifies Windows and Android behavior; record issues as new unchecked tasks rather than declaring the entire plan done prematurely.

Suggested baseline commands (run individually, from repository root):

```powershell
git status --short
```

```powershell
dotnet test tests/GymMembership.Tests
```

```powershell
npm --prefix supabase/tests/local test
```

```powershell
dotnet build src/GymMembership.App/GymMembership.App.csproj -f net10.0-windows10.0.19041.0 -p:PlatformOnly=windows
```

```powershell
dotnet build src/GymMembership.App/GymMembership.App.csproj -f net10.0-android -p:PlatformOnly=android
```

Check local test prerequisites/package scripts before running the database suite. Use disposable/local data, never production tests. These commands are planned checks, not evidence that they have run.

## 4. Phase evidence tracker

Fill cells with date, command/test, result and evidence location. `—` means not done, not passed.

| Phase | Implemented | Unit tests | SQL/RLS/race tests | Windows/device | Android/device | Migration/deployment | Review/owner acceptance |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 Baseline | D1/D2 approved 2026-10-20 | 533 pass, 2 pre-existing failures / 535 | 110 + 31 + 26 pass; populated history pass | Build passed; device N/A for baseline | Build passed; device N/A for baseline | No live changes | Other decisions pending |
| 1 Archive/accounts | IMPLEMENTED: items 1/2/4, four-type archive and restore | 39 focused pass; full 574 pass, same 2 fail / 576 | 22 account + 16 catalog pass; 110/31/26 + populated history pass | Build + isolated Windows demo smoke pass; full accessibility/layout checks pending | Build passes; actual device interaction pending (adb not on PATH) | 0023/0024 live push succeeded per owner output; post-push history confirmation pending; Edge Function deployment pending | Author self-review only; owner/device acceptance pending; NO commit/push |
| 2 Payment authority | — | — | — | — | — | — | — |
| 3 Code caps | — | — | — | — | — | — | — |
| 4 Revenue PDF | — | — | — | — | — | — | — |
| 5 Products/availability | — | — | — | — | — | — | — |
| 6 Reservations/grid | — | — | — | — | — | — | — |
| 7 Request workflow | — | — | — | — | — | — | — |
| 8 Coach presence | — | — | — | — | — | — | — |
| 9 Coach UX/profile | — | — | — | — | — | — | — |
| 10 Permanent QR | — | — | — | — | — | — | — |
| 11 Integration/rollout | — | — | — | — | — | — | — |

## 5. Session log — append whenever work pauses

### 2026-10-20 — Planning

- Completed: inspected current repository entry points and existing related rules; wrote phased implementation/acceptance checklists and restart tracking.
- Application changes: none. Tests/builds/live database operations: none.
- Important findings: payment void authorization already exists; member withdrawal/refund rules and `0022` booking guard must be reconciled; referenced mockup images not supplied.
- Next: resolve Phase 0 decisions with the owner, establish baseline, then begin Phase 1.

### 2026-10-20 — Phase 1, account deletion/menu slice (local only)

- Owner decisions: D1/D2 approved through the question tool; admin-only deletion reaffirmed in chat. Owner explicitly instructed not to push. No commit, push, release or live migration was performed.
- Branch / base HEAD: `phase1-archive-accounts` / `9f6ecb097ded65b59e3c6427924dd90d288e908a`. All work remains uncommitted. Initial untracked plan was preserved.
- Implemented: native Windows/Android account overflow menu, required-reason deletion popup, admin-only soft-delete/restore service/RPCs, deletion metadata and preserved history, active-list filtering, commitment/last-admin/self-deletion checks, ordinary-reactivation restore bypass prevention, and demo parity for this account slice.
- Added files: `Controls/DeleteAccountPopup.xaml` and `.xaml.cs`, `Services/AccountArchiveService.cs`, `ViewModels/AccountDeletionViewModel.cs`, `Demo/DemoAccountArchive.cs`, `supabase/migrations/0023_account_archive.sql`, `supabase/tests/local/account-archive.mjs`, `AccountArchiveTests.cs`, and `AccountDeletionViewModelTests.cs` (under their existing App/Core/test project roots).
- Updated existing files: app DI, UsersPage XAML/code-behind, UsersViewModel, Profile model, AdminService active-user listing, error messages, DemoGateway RPC routing/active checks, local SQL test script list and this plan.
- Baseline: 535 xUnit tests, 533 pass and 2 fail before changes; SQL rules 110, Phase 7 31, requests 26 and populated-history checks pass. Windows and Android baseline builds pass.
- Test-first evidence: 11 new account RPC tests initially failed because archive operations did not exist. Final focused account/ViewModel run: 23 pass, 0 fail. Full final xUnit: 559 total, 557 pass, 2 existing failures (new popup adds automatic XAML binding coverage too).
- Existing failures deliberately left untouched: `SubscriptionTests.The_membership_page_drops_the_pay_by_QR_panel_after_a_cancel` at line 211 (null PaymentItem), and `SupabaseGatewayTests.Setting_a_status_calls_the_function_and_reads_back_who_was_alerted` at line 222 (NullReferenceException).
- SQL evidence: 22 new account checks pass, including admin-only enforcement even with delegated users:manage, required whitespace-trimmed reason, populated pre-0023 inactive data, inactive identity denial, direct-write/helper denial, preserved history/audit evidence, restore constraints, payment-vs-delete in both lock orders and competing admins. Existing full SQL suites also pass. Final account suite re-run after whitespace normalization passes.
- Logs: `C:\Users\bowie\AppData\Local\Temp\opencode\gym-phase1-db-baseline.log`, `gym-phase1-db-regression.log`, and `gym-account-archive-tests.log` in the same directory. These logs include expected permission-denial SQL errors; use suite pass/fail summaries, not raw ERROR-line counts.
- Builds: final Windows and Android Debug compile checks passed. Existing obsolete PickPhotoAsync warning and unrelated chat-test xUnit1031 warning remain. `git diff --check` passed.
- Migration: 0023 tested on disposable local Postgres only. NO live schema/Auth/account/payment changes. Existing inactive profiles retain unknown deletion metadata rather than invented archive dates.
- Review: author security/correctness/complexity review only, not independent. Named ponytail, frontend-design and superpowers skills could not load via this connection; no claim those reviews ran.
- Not verified: physical device UI, keyboard/accessibility/light/dark/narrow layouts, full archived-user storage/session-surface audit. Existing signed file URLs are not claimed to be revoked by profile deactivation.
- NOT implemented yet: combined Discounts & Archive tab, all-type filters/pagination/read-only details/restore UI, catalog/code soft-archive paths and legacy archive backfill. Do not deploy or count Phase 1 complete yet; deleted accounts have a backend restore path but no destination Archive screen yet.
- Next concrete action: build the agreed combined Archive screen and remaining account/catalog/code archive lifecycle, keeping its records read-only and restore admin-only; start with current archive model/service contracts and server-side pagination/filter design. Then run actual device interaction checks.

### 2026-10-20 — Phase 1 remaining Archive implementation (local only)

- Request: finish the remaining Phase 1 work. Preserved prior slice and the owner's no-push instruction. No other phase started.
- Completed: admin Discounts/Archive tabs; all four record types; combined type/status/Asia-Manila date/reason filters; one-click clear; bounded server pagination; loading/error/empty states; read-only details and explicit restore confirmation; state/expiry rechecks and error retention.
- Lifecycle: used and unused plans/codes are retained instead of physically deleted; inactive catalogs leave active management lists. Expired codes appear automatically and cannot be edited/restored into validity. Archive metadata is server-stamped and legacy unknowns stay unknown. Member subscription expiration remains membership history.
- Access review: existing inactive-session RLS/RPC checks retained; inactive admin storage gap closed with a restrictive policy; inactive account role/coach/profile edits blocked; archived-target password reset blocked before the external Auth call; demo mirrors catalog immutability, notifications and restore checks. No promise of revoking old signed URLs/Auth tokens or atomicity across the Auth API and a concurrent database archive.
- New files: `Models/ArchiveModels.cs`, `Services/ArchiveService.cs`, `ViewModels/ArchiveViewModel.cs`, `Demo/DemoArchive.cs`, `Controls/ArchivePanel.xaml` and code-behind, `0024_archive_catalog.sql`, `archive-catalog.mjs`, `ArchiveTests.cs`, `ArchiveViewModelTests.cs`, `scripts/Test-WindowsArchive.ps1` (within their existing project folders).
- Updated: catalog and Users active lists; archive-backed package/code actions; Discounts UI; shell/overview labels; demo guards; archived-target reset-password check; RPC transport test; intentional old hard-delete test expectations; this checklist/evidence tracker.
- Test-first evidence: the 8 new archive behavior cases initially failed against the prior slice and passed after implementation. Final focused Phase 1 suite: 39 passed. Full xUnit: 576 total / 574 passed / 2 failed, both pre-existing: `SubscriptionTests.The_membership_page_drops_the_pay_by_QR_panel_after_a_cancel` and `SupabaseGatewayTests.Setting_a_status_calls_the_function_and_reads_back_who_was_alerted`. Transport JSON mapping/filter/paging/restore test passes.
- Final disposable SQL chain: 110 rules + 31 Phase 7 + populated-history migration suite + 26 session requests + 22 account checks + 16 archive/catalog checks all pass. Tests cover nonadmin and inactive-admin denial, legacy metadata, all-type combined filters, retained history, stale edit/restore bypasses, direct deletes, metadata spoofing, expired codes, archived-role/coach edits and storage writes. Account concurrency tests from the first slice still pass.
- Builds: Windows and Android Debug pass, with existing media-picker deprecation warning; unrelated xUnit1031 warning remains. `git diff --check` passes (only a Git CRLF normalization warning for the Edge Function file). A transient DLL-copy retry in an intermediate build resolved; the final Windows build had no copy failure.
- Windows actual demo smoke: `Test-WindowsArchive.ps1` passes. Confirm disabled for blank reason, enabled for entered reason, cancellation closes popup; archive filter empty/Clear works; a catalog plan is archived then restored from read-only details and no longer matches Archive; front desk has no archive navigation. Test uses a separate build copy with only its copied supabase.json removed; original backend configuration was untouched. Intermediate automation checks failed on an offscreen filter after restore and on a native menu lookup in the main-window tree; the script now handles offscreen filter access and includes a process-scoped popup lookup fallback. The smoke test passes again on the final rebuilt isolated demo.
- Evidence logs: `C:\Users\bowie\AppData\Local\Temp\opencode\gym-phase1-db-regression.log`, `gym-catalog-db.log`, `gym-phase1-final-tests.log`, `gym-archive-transport.log`. SQL logs intentionally contain permission-denial ERROR lines; use pass/fail summaries.
- Unverified: Android actual UI (adb absent from PATH), screen-reader/keyboard/narrow/light-dark matrix, independent review and owner acceptance. Existing optional project skills could not be loaded; do not claim they ran.
- Branch/base HEAD unchanged: `phase1-archive-accounts` / `9f6ecb097ded65b59e3c6427924dd90d288e908a`. All changes remain local/uncommitted. No push, commit, deployment, live migration or real-account operation performed.
- Restart: implementation is complete; review local UI and finish unchecked device acceptance. For any next phase, resolve its decisions first (Phase 2: D3). Do not deploy merely because local checks pass.

### Live database migration — owner-reported success after Phase 1

- Owner supplied linked migration history showing 0001–0022 applied remotely and only 0023/0024 pending.
- Owner supplied dry-run output listing exactly `0023_account_archive.sql` and `0024_archive_catalog.sql`; no other pending migrations were listed.
- Owner then approved and ran the live database push; shared output reports both migrations applied and `Finished supabase db push.` This supersedes earlier session notes stating neither migration was live.
- [x] Owner-reported live database application of 0023 and 0024 recorded.
- [ ] Confirm post-push `npx supabase migration list --linked` shows both versions in Local and Remote columns.
- [ ] Deploy the changed `reset-password` Edge Function separately to the confirmed gym project.
- [ ] Verify the updated app against the live project with designated test records; retain outstanding Android/accessibility/owner acceptance checks.
- Backup status was not provided; do not claim one was created. Do not rerun or repair successfully applied migrations automatically.
- Agent action this turn: progress documentation only. No remote commands, Git commit/push, function deployment, app release or real-account test performed by the agent. Owner's no-Git-push instruction remains active.

### Owner-authorized checkpoint — pause before other phases

- Owner explicitly requested a new branch and push to save the current Phase 1 work, with other phases to continue tomorrow. The earlier no-push restriction is superseded only for this checkpoint.
- Created `phase1-archive-checkpoint-2026-10-20` from `phase1-archive-accounts`, preserving all 38 reviewed Phase 1 files. No unrelated working-tree files were found; no tracked backend configuration or credentials are being added.
- Intended checkpoint commit message: `feat: complete phase 1 account and catalog archive`. Resolve its actual hash from `git log`; confirm publication using the upstream branch and clean status rather than assuming a push succeeded from this prepared handoff alone.
- Last implementation checks remain: 39 focused pass; full xUnit 574/576 with the same 2 baseline failures; full SQL chain 110 + 31 + populated-history + 26 + 22 + 16 pass; Windows/Android builds and Windows isolated-demo smoke pass. Checkpoint preparation changes only this handoff, not application behavior. `git diff --check` and a limited common-credential-pattern scan passed; the scan is not a comprehensive security audit.
- Live state: owner reported 0023 and 0024 applied successfully. Post-push migration-list confirmation, `reset-password` Edge Function deployment, live-backed test records and Android/accessibility/owner acceptance remain pending. No deployment or real-record tests are part of this Git checkpoint.
- Tomorrow: read this current-work section, inspect branch/upstream/dirty state, confirm outstanding Phase 1 rollout checks, then settle D3 before starting Phase 2 payment authorization. Other phases remain planned; no automatic merge, app release or further live operations are authorized.

### YYYY-MM-DD — Phase __ / session __

- Start state (branch / commit / dirty files):
- Tasks completed (phase and checklist labels):
- Files changed:
- Tests/builds run and exact results:
- Device checks actually performed:
- Migrations added / locally tested / live-applied (separate each):
- Decisions made and owner approval:
- Remaining failures / blockers:
- Last known working point:
- Next concrete action (file, function, test or command):
- Commit / PR reference, if separately requested:

## 6. Copyable resume prompt

> Read `CLAUDE.md` and `docs/superpowers/plans/2026-10-20-gym-fixes-and-changes.md`. Inspect the current-work block, latest session log, git status and relevant code. Summarize what is actually done versus pending, then continue only the next approved phase/task. Preserve existing user work and previously approved payment/refund/session rules unless this plan records an explicit replacement decision. Add regression tests and verify each implementation slice. Before stopping, update the current-work block, phase status, task checkboxes, evidence tracker and session log. Do not deploy migrations, operate on real accounts/payments, commit, push or release without separate authorization.
