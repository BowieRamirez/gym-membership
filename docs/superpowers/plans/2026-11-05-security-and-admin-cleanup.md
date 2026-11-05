# Security and admin cleanup — implementation and progress plan

Created: 2026-11-05
Last updated: 2026-11-05
Status: PLANNED. Nothing built yet. The full to-do list is in this plan; open decisions are in section 2.
Branch: `security-admin-cleanup-2026-11-05`, from `origin/main` at 3b56c65. All phases stay on it; the owner merges it to `main` when the plan is done.

**Goal:** Work through the team's to-do list for the gym system (admin side and QR check-in) in priority order: security and data integrity first, then CRUD, refunds and archiving, UI and audit logs, then trying to break the system.

## How to use this plan

This is the restartable checklist for the to-do list. The original item numbers are kept. Mark things here when you stop and read here when you continue.

1. Work on one phase at a time, in the order below. Before starting, read `CLAUDE.md` and the current code; the paths here are starting points.
2. Change the phase status in the table to `IN PROGRESS`, fill in "Current work", and tick a task only when it is actually finished (`- [ ]` to `- [x]`, with the date and the test or commit that proves it).
3. Keep code, automated tests, manual checks and live deployment separate. Code written is not the same as verified or deployed.
4. Before stopping, update "Current work" and the session log: exact failing tests, uncommitted files, and the next concrete action.
5. Before resuming, read this file, run `git status --short`, check the recorded branch and commit, and continue from the next action. Do not overwrite unfamiliar changes.
6. No live database migration, real account or payment operation, commit, push, or release is authorized merely by this plan. Schedule those separately with the owner.

Status vocabulary: `PLANNED`, `IN PROGRESS`, `BLOCKED`, `IMPLEMENTED`, `VERIFIED`, `DEPLOYED`. Use `N/A` for verification that genuinely doesn't apply, with a reason. A ticked task does not mean the phase is accepted.

**Rules**
- Business rules live in the database and are mirrored in `DemoGateway`; hiding something in the UI is not enough.
- New migrations continue from the latest number (0038 at the time of writing, so next is `0039`). Never rewrite an applied migration.
- Logic goes in Core with an xUnit test in `tests/GymMembership.Tests`; database rules get a check in `supabase/tests/local`.
- UI work: AutomationIds used by the UI scripts stay on the same controls; phones keep their layouts.
- **Design (owner, 2026-11-05):** every task that changes a screen uses the `impeccable` skill, so the system doesn't look like slop or AI-generated UI, alongside the `frontend-design` skill that `CLAUDE.md` requires. Main design phases: 7 and 8. Smaller screen changes: Phases 2, 3, 5 and 6, each marked below. Phases 1, 4 and 9 change no design unless a fix touches a screen.
- Completed changes get `ponytail:ponytail-review` before finishing (`CLAUDE.md`).
- Each new decision goes in section 2 with the owner's answer and the date.

### Current work / restart here

- **2026-11-05:** Phase 1 VERIFIED and committed. Branch is local, not pushed. xUnit 779/779; database suite passes (307 counted).
- Next concrete action: owner answers D8 (live email check) so Phase 2 can start. Other decisions are needed only before the phase that uses them (see the table in section 3).

## 1. Repository findings

From reading the code on 2026-11-05. Verify before relying on these; line numbers drift.

| Item | What exists today | Consequence |
| --- | --- | --- |
| 1 Name lock | Settings has editable first and last name (`SettingsPage.xaml:39,43`, `SaveNameAsync` in `MemberViewModels.cs:927`). Owner decision D13 of the 2026-10-20 plan made names "editable in Settings". `0035` grants `update (first_name, last_name)` on `profiles` to signed-in users. | This reverses an earlier decision. Removing the fields is not enough: the grant lets a client update directly, so the lock must be in the database. |
| 2 Email check | No lookup exists. A duplicate is only found on submit (`SupabaseGateway.cs:99`). `rules.mjs` asserts that anonymous users reach no security definer function. | Someone registering is not signed in, so a plain database function would fail that check. Debouncing only slows a well-behaved client; real protection must be on the server. |
| 3 Parameterized queries | The app talks through the Supabase client, with one generic `Rpc` call (`SupabaseGateway.cs:232`). Dynamic SQL found: `0024` (table names from a fixed `case`, values passed with `using`) and `0006` (catalog signatures). | Likely an audit with little or no code change. String-built client filters and the two Edge Functions still need checking. |
| 4 Overview | `AdminOverviewPage.Windows.cs` builds the page; the owner kept the black "Open" buttons on purpose on 2026-11-01. | See D6. |
| 5, 14 Audit logs | `audit_log` table and `AuditPage.xaml` exist, shown as a list of rows (action, actor, entity, id, date). `c:TableBody` is the existing table control. The table has no IP address or role column. `AuditAsync` (`Services.cs:1323`) loads every row into the app, and raw codes like `user.archive` are shown as they are. | A UI table, not a new log table, but new data is needed (D7). 10 rows per page should be fetched per page from the server. Readable labels are one lookup in Core. |
| 6, 8, 9, 12 Archive | `archive_account` (`0023`) and `archive_catalog_record` for plans, amenities and codes (`0024`) exist, and deleting a code already archives it (`Services.cs:1275`). | Possibly already done. Verify each path first and build only the gaps. |
| 7 Amenities | Passes last days (`duration_days`), bought per amenity. Decisions D6 and D9 of the 2026-10-20 plan say a coach is optional and future bookings don't need the coach present. | Reverses earlier decisions and changes the pricing model. The largest item on the list. See D11. |
| 10 QR offline | Scanning needs the server. Decision D12 of the 2026-10-20 plan: offline shows "can't check right now", never Active. | Reverses an earlier safety rule. See D12. |
| Forgot phone | Check-in page already has name search with a Check in button (`StaffViewModels.cs:300-308`), and the server enforces membership. | Probably a small change: show membership status before check-in and record that it was manual. |
| 11, refund formula | Approving a refund already cancels that term (`0025`). Requests are only allowed within 24 hours of payment verification (`0018:319`), full amount only. There are no reason categories or manual review routes. | Item 11 may need only a test. The formula is new logic (D9, D10). |
| 13 CRUD | 37 tables, including append-only logs and money records, and archive is used in place of physical delete. | "Delete for every entity" cannot be literal (D14). Start with a table of what each entity allows. |
| 15 Status labels | Accounts have `deleted_at` and `is_active`; catalog records have `is_active`. Memberships have their own statuses (Active, Pending, Expired). | Scope to be confirmed, as the list asks (D13). |
| Testing | `record_check_in` already refuses `no_active_membership` (`0005_functions.sql:704`). Passwords are length-only, 6 to 64 (`Services.cs:57`). The database allows payment `amount >= 0` (`0002_tables.sql:207`). | Check-in may only need a test. Password and zero-payment rules need decisions (D5, D4). |

## 2. Decisions to settle

Recommendations are proposals, not approved requirements. Record the answer and date beside each. Block only the phases affected.

- [ ] **D1 — screenshots:** The full list arrived 2026-11-05. The three screenshots it mentions (admin overview, Audit Logs, refund guidance) have not; the plan works from their text descriptions. Owner to supply them if they show more than that.
- [x] **D2 — branch (owner, 2026-11-05):** One new branch for the whole plan, `security-admin-cleanup-2026-11-05`, merged to `main` by the owner when the plan is done. No branch per phase.
- [ ] **D3 — name lock details:** Does this reverse D13 of the 2026-10-20 plan for everyone (members, coaches, staff, admins), and how is a typo fixed? Recommend: everyone is locked, and only a System admin can correct a name through an audited function with a reason. Without a correction path, a typo at registration is permanent.
- [ ] **D4 — paying 0:** Can a payment of 0 ever be valid (for example a 100% discount)? Recommend: refuse 0 everywhere unless a discount brought the price to 0. Check first whether discounts can reach 100%.
- [ ] **D5 — password rules:** Today 6 to 64 characters, length only. Recommend: 8 to 64 with at least one letter and one number, for new passwords only (registration, change, reset, temporary); existing passwords keep working. The Supabase Auth dashboard setting must match, or the rule is only client-side. Owner to change that setting.
- [ ] **D6 — overview layout:** Does simplifying replace the eight cards and the black "Open" buttons the owner kept on 2026-11-01? Recommend: a mockup first in `docs/mockups/`, as with the full-page layout, showing a number strip plus a compact list of links.
- [ ] **D7 — audit table data:** Recommend: show the existing `audit_log` in a table, adding `ip_address` and `role` columns filled from now on, with older rows blank rather than guessed. IP comes from the request headers Supabase passes through, and is personal data, so the owner should decide how long it is kept. Filters: date range, user, action type, object type. Export: CSV of the filtered rows.
- [ ] **D8 — email check exposure (item 2):** Showing "already in use" tells anyone whether an address has an account, however well it is rate limited. Recommend: accept that for this gym, and build an Edge Function `check-email` with a per-IP limit on the server. Alternative: skip the live check and keep the error on submit, which reveals the same answer more slowly and with less to abuse.
- [ ] **D9 — refund formula (blocks Phase 5):** What counts as "activated", and does the 24-hour rule stay? Recommend: activated = the term's start date has passed or the member has checked in during it, whichever is first. Before that, a full refund with no 24-hour limit. After that, the request goes to staff for manual review with a required reason: gym failed to provide the service, member was misled, contract allows it, illness or relocation, or change of mind (not automatic). Staff decide; the app only surfaces the case. The wording should be checked by someone qualified, since the guidance is not legal advice.
- [ ] **D10 — what a refund cancels (blocks Phase 5):** Approval cancels the refunded term today. If the member also has a later paid renewal, is that cancelled too? Recommend: leave it, since it has its own payment, and show staff a warning when approving.
- [ ] **D11 — amenities per session (blocks Phase 6):** Reverses D6 and D9 of the 2026-10-20 plan. To settle: (a) what a "session" is: one booking of 30 minutes to 2 hours, or one check-in; (b) price per session, and what happens to passes already sold (recommend: they stay valid until they expire); (c) does solo use end; (d) "coach present" means any coach checked in (`coach_visits`), or the member's own coach; (e) does the coach need to be present when booking or only at the time of use. Recommend any checked-in coach, checked at time of use, because coaches can't be checked in for a future booking.
- [ ] **D12 — offline check-in (blocks Phase 6):** Reverses "offline is never Active" in the 2026-10-20 plan. Does offline mean the gym's internet is down, and how old may the data be? Recommend: the desk keeps a local copy of member codes and statuses, refreshed while online. Offline scans show "Last updated at ..." rather than Active, and check-ins are queued with their time, flagged as offline, then sent on reconnect and rechecked by the server. Copies older than 24 hours show "can't check". Known risk: a member who expired since the last refresh can get in.
- [ ] **D13 — status labels (blocks Phase 7):** The list asks to confirm scope. Recommend: Active, Inactive and Deleted for accounts, packages, amenities and discount codes only. Memberships, payments and refunds keep their own statuses, because "Pending" or "Expired" would lose meaning.
- [ ] **D14 — what CRUD means per entity (blocks Phase 4):** Recommend: master data (accounts, packages, amenities, codes) gets full create, read and update, with delete as archive. Money and history (payments, refunds, terms, check-ins, audit log) can't be edited or deleted, only reversed by the existing actions. Phase 4 produces the table for the owner to approve.
- [ ] **D15 — items without a place in the priority order:** The list's order doesn't mention amenities (7), QR offline (10), the forgot-phone fallback or status labels (15). Recommend: 7, 10 and forgot-phone after refunds and archiving (Phase 6), and 15 with the UI (Phase 7). This plan assumes that until the owner says otherwise.
- [ ] **D16 — "every function" in the testing item:** Recommend: every database function callable from the app gets at least one attempt to break it, enforced by a `rules.mjs` check that fails when a function has no test. UI screens are covered by the listed cases only.
- [x] **D17 — role names (owner, 2026-11-05):** Super admin is now called **System admin**, and Sub-admin is now called **Admin**. Implementer's call, not asked: this changes the names people see only, and permissions stay the same. The internal codes stay as they are (`admin` = System admin, `subadmin` = Admin), because renaming them would touch applied migrations, permissions and existing accounts. From here on, "admin" in this plan's code references means the code; on screen it means the former Sub-admin.

## 3. Phases and item coverage

Order set by the owner: 3, 2, 1, then CRUD (13), refunds and archiving, UI and audit logs, testing. Phase 6 and item 15 are placed per D15. The list uses the number 2 twice; the refund formula is called "R2" here.

| Phase | Scope | Items | Needs | Status |
| --- | --- | --- | --- | --- |
| 1 | Parameterized query audit | 3 | D2 | VERIFIED |
| 2 | Live email-in-use check | 2 | D2, D8 | PLANNED |
| 3 | Lock names after creation | 1 | D2, D3 | PLANNED |
| 4 | CRUD | 13 | D14 | PLANNED |
| 5 | Refunds and archiving | 11, R2, 6, 8, 9, 12 | D9, D10 | PLANNED |
| 6 | Amenities and check-in fallbacks | 7, 10, forgot phone | D11, D12, D15 | PLANNED |
| 7 | Simplify admin overview, status labels, role names | 4, 15, role names | D6, D13, D17 | PLANNED |
| 8 | Audit log table and labels | 5, 14 | D7 | PLANNED |
| 9 | Try to break the system | Testing | D4, D5, D16 | PLANNED |
| 10 | Final check and report | All | Phases 1-9 | PLANNED |

### Phase 1: parameterized queries (item 3)
- [x] Baseline (2026-11-05, before any change): xUnit 774/774; database suite exit 0, 306 counted, 0 failed.
- [x] Check every query in `src/GymMembership.Core/Backend/` and `Services/` for filters or search text built with string interpolation. (2026-11-05: no SQL strings or hand-built filters. All 61 filter calls use ids, dates or fixed words; member search filters in the app, not in a query.)
- [x] Check `supabase/functions/` (`create-user`, `reset-password`, `_shared`). (2026-11-05: read in full; all use the query builder with values passed separately.)
- [x] Check every dynamic `execute` in the migrations, using the latest definition of each function. Confirm no user input reaches an identifier without a fixed list. (2026-11-05: only `0024` and `0006`. `0024` takes table and column names from a fixed `case` and passes values with `using`; `0006` only uses function signatures from the catalog. `chat_search` in `0015` is static SQL and escapes `\`, `%` and `_`.)
- [x] Add checks that put injection strings through inputs and confirm they are treated as plain text. (2026-11-05: `rules.mjs` search with `%%`, `__` and a `drop table` string; `archive-catalog.mjs` type, id and reason; new `QueryInjectionTests.cs` for filter values. Removing the search escaping made the `rules.mjs` test fail, then the migration was restored unchanged.)
- [x] Fix anything found. (2026-11-05: no injection path found in the app, so no behaviour changed.)
- **Result:** item 3 was already met. Supabase queries go through the library or server functions, which keep values separate from the query. The checks above now guard that.
- **Known limit:** the Supabase library does not quote values inside a grouped filter (`a && b`), so a comma in a value starts a new condition. Not reachable today, because those filters hold only ids and fixed words, and row level security still limits the results. Typed text must go to a server function. Pinned by `QueryInjectionTests` and a `ponytail:` comment on `SupabaseGateway.ListAsync`. Upgrade path: move any future typed-text filter to a server function.
- `ponytail:ponytail-review` (2026-11-05): nothing to cut. Manual check: N/A, no screens changed. Deployment: N/A, no migration or Edge Function changed.

### Phase 2: email already in use (item 2)
- [ ] Edge Function `check-email`: validates the address, applies the per-IP limit on the server, returns only `in_use` true or false. No anonymous database access, so the `rules.mjs` anonymous check stays as it is.
- [ ] `IAuthService` method for the lookup, with the same behaviour in `DemoGateway`.
- [ ] `RegisterViewModel`: 400 ms debounce with cancellation of the previous lookup; no lookup until the email shape is valid (`EmailShape`, `Services.cs:44`). Test: fast typing sends one lookup, and a slow old answer never overwrites a newer one.
- [ ] (`impeccable`) `RegisterPage.xaml`: message under the email field, announced for screen readers. Lookup failure or rate limit shows nothing and does not block registering.
- [ ] The duplicate error on submit stays (`SupabaseGateway.cs:99`); it is the real guard.
- [ ] Function test: over-limit calls are refused. Deploying the function is a separate step with the owner.

### Phase 3: lock names (item 1)
- [ ] Migration `0039`: a trigger on `profiles` refuses changes to `first_name`, `last_name` and `full_name` after creation, including writers that only set `full_name` (`0035:31-39`). Revoke the `0035` update grant and retire its name-saving function as D3 requires.
- [ ] Check that archive, restore and the `create-user` function still work with the trigger on.
- [ ] Correction path per D3, audited, with a reason.
- [ ] (`impeccable`) Settings: names shown read-only; `SaveNameAsync` and its command removed. Same refusal in `DemoGateway`.
- [ ] Tests: update `names-email.mjs` (it covers editing names today) and the xUnit name tests; add one check that a direct update as a signed-in user is refused.

### Phase 4: CRUD (item 13, marked very important)
- [ ] Table in this plan: one row per entity, with what create, read, update and delete should do, who may do it, and where it is in the app. Owner approves it (D14).
- [ ] For each row, test the service and the database function, and record pass or gap in the table.
- [ ] Fix the gaps, each with a test and the same behaviour in `DemoGateway`.

### Phase 5: refunds and archiving (items 11, R2, 6, 8, 9, 12)
- [ ] Item 11: test that approving a refund cancels that term and updates the member's status, and leaves other members untouched (`0025`). Later renewals per D10.
- [ ] R2: migration changing `request_membership_refund` per D9, keeping `amount` equal to the payment. Not-yet-activated gets a full refund; anything else goes to manual review with a reason category.
- [ ] (`impeccable`) `RefundsPage`: show activated or not and the reason category so staff can see which cases need review. Member screen says what happens before they submit, with no promise of a refund after activation.
- [ ] Tests at the edges: just before and after activation, already checked in, second request, 0 amount, voided payment.
- [ ] Item 6: verify archiving a deleted user keeps their payments, terms and check-ins, and they show in the Archive; fix any gaps.
- [ ] Items 8 and 9: verify an inactive code can be deleted and lands in the Archive, and that an active one follows the rule you confirm here. The UI label says what happens.
- [ ] Item 12: verify deleting an amenity archives it, no path removes the row, and existing passes and bookings still display.

### Phase 6: amenities and check-in fallbacks (items 7, 10, forgot phone)
- [ ] (`impeccable` for the screens) Item 7: database change per D11 (session-based tracking, coach-present rule on the server), demo parity, booking and pass screens updated. Write tasks in detail once D11 is answered, because it changes pricing.
- [ ] (`impeccable` for the offline states) Item 10: local copy and queue per D12, with the sync rechecked by the server. Tests: stale copy, expired member, replayed queue doesn't double check in, and offline result never says plain Active.
- [ ] (`impeccable`) Forgot phone: on the Check-in page's name search, show membership status and expiry before Check in; expired or inactive members can't be checked in; the check-in records that it was manual and who did it. Test: refused for an expired member by a direct call.

### Phase 7: admin overview, status labels and role names (items 4, 15, D17)
- [ ] (`impeccable`, main design phase) Mockup in `docs/mockups/`; owner approves before building (D6).
- [ ] Build it, keeping every destination reachable: accounts, packages, discounts, cancellations and refunds, revenue, payments, membership sync, logs and archive.
- [ ] Update the Overview checks in `scripts/Test-WindowsAdminNavigation.ps1` if the shortcuts change.
- [ ] Check in demo mode at about 900 px and maximised, in light and dark.
- [ ] Item 15: one function in Core turning a record into Active, Inactive or Deleted, with a test, used on the screens in scope per D13. Meaning is in the words, not only colour.
- [ ] Role names (D17): one place for the on-screen name. `StaffShell.xaml.cs:23` and `Services.cs:1158` each have their own copy today; make the shell use the `Services.cs` one. Then replace the wording on screen, including error messages (`Foundation.cs:108`, `UsersPage.xaml.cs:34`, `StaffViewModels.cs:1056,1188`, `AdminOverviewViewModel.cs:113`), the Users role picker, and the demo sign-in buttons (`LoginPage.xaml:128,130`, which keep their AutomationIds). About 12 files.
- [ ] **Security trap in the role picker:** `StaffViewModels.cs:950` turns the chosen name into a role with `NewRole == "Sub-admin" ? "subadmin" : NewRole.ToLowerInvariant()`. If only the wording changes, choosing "Admin" asks for `admin`, the System admin role. Map names to codes explicitly. Test: choosing "Admin" creates `subadmin`, and "System admin" is not offered. Also confirm the server refuses a request for `admin` from this form (`DemoGateway.cs:203` does; check `create-user`).
- [ ] Decide with the mockup whether the "Admin overview" title stays, since both roles see it. The Audit Log role column (Phase 8) uses the new names, including for older rows.

### Phase 8: audit log table and labels (items 5, 14)
- [ ] Migration: `ip_address` and `role` on `audit_log`, filled inside `audit()` per D7.
- [ ] Server-side paging, 10 rows per page, with filters (D7). `AuditAsync` stops loading the whole table.
- [ ] Item 14: map of action code to readable label in Core (`user.archive` becomes "Account deleted"). A test fails if any code used in the migrations or `DemoGateway` has no label; an unknown code shows as itself, never blank.
- [ ] (`impeccable`, main design phase, including filters and export) Swap the rows in `AuditPage.xaml` for a table with `TableHead` and `c:TableBody`, with a `WideFrom` value (`PageLayoutTests` checks each `TableHead` has one). Columns: date/time, user, role, IP address, action type, action, object type, object, change details.
- [ ] Export button: CSV of the filtered rows, not just the visible page. Cells starting with `=`, `+`, `-` or `@` are escaped so a spreadsheet can't run them.
- [ ] Check in demo mode, wide and narrow.

### Phase 9: try to break the system (testing)
Each attempt gets a recorded result: refused correctly, or a bug with its fix. Test both the app's validation and the server, because the app's checks can be bypassed.
- [ ] Regex validation: bad names, emails and money values against `Services.cs:43-57,617` and the database constraints, which should agree.
- [ ] Check-in without a valid membership: expired, pending, archived and unknown QR code, by scan and by name search. The server must refuse `record_check_in` directly.
- [ ] Account with an existing email: differing case and surrounding spaces, at registration and through staff `create-user`.
- [ ] Password rules per D5: too short, too long, missing a letter or number, on register, change, reset and temporary password.
- [ ] Paying 0 per D4, plus negative amounts, at the cashier, member payment and through a direct function call.
- [ ] Every other database function per D16, with the `rules.mjs` check that fails when one has no test.

### Phase 10: final check and report
- [ ] xUnit and database suites pass; Windows and Android builds pass; `scripts/Test-WindowsAdminNavigation.ps1` passes.
- [ ] `ponytail:ponytail-review` on the changes; findings recorded.
- [ ] `impeccable` review of every screen changed by this plan; fixes made and findings recorded.
- [ ] Report to the owner: which items are done, which need clarification, which to check manually.
- [ ] With the owner, separately: apply migrations, deploy Edge Functions, set the Auth password rule, open the PR, release.

## How to check (demo mode)
- Real config `src/GymMembership.App/Resources/Raw/supabase.json` must be moved out of the project before a demo build and put back right after (never commit it, never leave a `.bak` in the repo). Android demo builds also need `--no-incremental`.
- Demo sign-in buttons on the login page: Member, Coach, Front desk, Admin, Sub-admin.
- The owner often runs the app from Visual Studio, which locks `bin\Debug`. Ask before closing it.

## To-do list as received

Complete version, received 2026-11-05. Numbers and order are as sent; the list uses 2 twice.

**Account and data rules**
1. Users can't change their name once it's set. Treat it like Master Data, which is locked after creation.
2. On the registration screen, show right away if an email is already in use. Add proper debouncing so the lookup query can't be abused.
3. All database queries must be parameterized to prevent injection.

**UI**
4. The admin overview dashboard is cluttered ("UI slop"). It has too many cards and too much on screen. Simplify the layout. Reference: the admin overview screenshot (cards for accounts, packages, discounts, cancellations and refunds, revenue, payments, membership sync, and logs/archive, each with an "Open ..." button).

**Audit logs**
5. Add a table for the audit logs. Reference: the Audit Logs screenshot, with columns for date/time, user, role, IP address, action type, action, object type, object, and change details, plus filters and an export button.
14. Fix the audit log labeling. When an account is deleted, the action currently shows as `user.archive`. Map raw action codes to plain, readable labels that match the table. Limit the audit log to 10 rows per page.

**Archiving and deletion**
6. Archive accounts of deleted users so their records are kept.
9. Deleted inactive codes should move to the archive.
8. Allow deletion of inactive codes.
12. Amenities can be deleted, but deleted amenities should go to the archive instead of being removed.

**Amenities**
7. Amenities are tracked per session, not per day. They should only be available while a coach is present.

**Refunds and memberships**
11. A refund automatically cancels that specific member's membership.
2. (R2) Refund formula: a member gets a full refund if their membership has not been activated.
(See the refund guidance screenshot. It summarizes Philippine consumer-law rules. A gym can't have a blanket "no refund" policy. A refund may be owed when the gym fails to provide the agreed service, when the member was misled, when the contract allows cancellation or refund, or when there's illness or relocation. A "change of mind" refund is not automatic. Use this to shape the refund logic and surface the cases that need manual review. Don't treat it as legal advice.)

**Operations**
10. QR code scanner: add a fallback for when the facility is offline.
13. CRUD operations (marked very important). Make sure create, read, update and delete work consistently for every entity in the system.
Inconvenience fix: a customer forgot their phone and can't check in. Add a fallback where staff can verify the member's membership from the admin side and check them in.

**Labeling**
15. Label every record's status as one of: Deleted, Inactive, or Active. (This item was blank in the source, so confirm the intended scope with me.)

**Role names (added by the owner, 2026-11-05)**
The name for super admin is System admin, and sub-admin is Admin.

**Testing**
Create test scripts for every function that try to break the system. Cover at least:
- regex validation
- checking in a member without a valid membership
- creating an account with an email that already exists
- password regex rules
- paying with a value of 0

Work in priority order: items 3, 2 and 1 first (security and data integrity), then CRUD (13), then refunds and archiving, then UI and audit logs, then testing. Report at the end which items are done, which need clarification, and which I should check manually.

## Session log

- **2026-11-05:** Plan created from the to-do list and a read of the code. No code, database or git changes.
- **2026-11-05:** Complete to-do list received; added items 5 (rest), 6-15, R2, the forgot-phone fallback and decisions D9-D16. Phases renumbered to 10. No code, database or git changes.
- **2026-11-05:** Owner renamed the roles: Super admin to System admin, Sub-admin to Admin (D17, Phase 7). No code, database or git changes.
- **2026-11-05:** Branch `security-admin-cleanup-2026-11-05` created from `origin/main` (D2); plan committed there.
- **2026-11-05:** Phase 1 done: query audit found no injection path, tests added, one library limit recorded. xUnit 779/779; database suite exit 0, 307 counted. Owner added the `impeccable` rule for screen work.
