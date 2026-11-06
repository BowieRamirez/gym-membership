# Security and admin cleanup — implementation and progress plan

Created: 2026-11-05
Last updated: 2026-11-06
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
6. **Database changes (owner, 2026-11-05):** when a phase changes the database, push its migrations and Edge Functions to the live project as part of finishing the phase, then move on; no need to ask each time. Dry-run first, and push only that phase's own changes. Real account or payment operations, data edits, destructive changes to existing data, `git push`, merging and releases still need the owner's go-ahead.

Status vocabulary: `PLANNED`, `IN PROGRESS`, `BLOCKED`, `IMPLEMENTED`, `VERIFIED`, `DEPLOYED`. Use `N/A` for verification that genuinely doesn't apply, with a reason. A ticked task does not mean the phase is accepted.

**Rules**
- Business rules live in the database and are mirrored in `DemoGateway`; hiding something in the UI is not enough.
- New migrations continue from the latest number (0045 after Phase 8, so next is `0046`). Never rewrite an applied migration.
- Logic goes in Core with an xUnit test in `tests/GymMembership.Tests`; database rules get a check in `supabase/tests/local`.
- UI work: AutomationIds used by the UI scripts stay on the same controls; phones keep their layouts.
- **Design (owner, 2026-11-05):** every task that changes a screen uses the `impeccable` skill, so the system doesn't look like slop or AI-generated UI, alongside the `frontend-design` skill that `CLAUDE.md` requires. Main design phases: 7, 8 and 9 (Phase 9 is the whole-app yellow-and-black redesign). Smaller screen changes: Phases 2, 3, 5 and 6, each marked below. Phases 1, 4 and 10 change no design unless a fix touches a screen.
- Completed changes get `ponytail:ponytail-review` before finishing (`CLAUDE.md`).
- Each new decision goes in section 2 with the owner's answer and the date.

### Current work / restart here

- **2026-11-05:** Phase 6 DEPLOYED: `0043` and `0044` applied live. Phases 2 to 5 are also DEPLOYED. Owner checks on the real backend are not recorded yet. Commits from Phase 6 are not pushed to GitHub. xUnit 842/842; database suite exit 0, 353 counted; Windows and Android builds pass.
- **2026-11-06:** Owner answered D4, D5, D7 (IP kept 90 days) and D13, all as recommended. Phase 7 started: overview mockup `docs/mockups/admin-overview.html` drafted, waiting for approval (D6).
- **2026-11-06:** Phase 8 DEPLOYED: `0045` applied and `create-user` and `reset-password` redeployed. Phase 7 VERIFIED (no database change). xUnit 864/864; database suite exit 0, 363 counted; Windows and Android builds pass; `Test-WindowsAdminNavigation.ps1` passes. Commits are not pushed to GitHub.
- **2026-11-06:** Owner added a new Phase 9, the yellow-and-black redesign of every Windows page and the Android member and coach screens, with the new badge, the HF monogram and the name "Gym Center" (D22 to D25, design `docs/superpowers/specs/2026-11-06-yellow-black-redesign-design.md`). Trying to break the system is now Phase 10 and the final check Phase 11.
- Next concrete action: owner approves or changes `docs/mockups/yellow-black.html` (9.1), then 9.2 colours and shared pieces. Owner still to set session prices and check Phases 2 to 8 on the real backend.

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
- [x] **D3 — name lock details (owner, 2026-11-05: option A):** Reverses D13 of the 2026-10-20 plan for everyone: members, coaches, staff and admins. Only a System admin can correct a name, with a reason of 1 to 500 characters that goes in the audit log. Implementer's call, not asked: a System admin may correct their own name, because a lone System admin's typo would otherwise be permanent.
- [x] **D4 — paying 0 (owner, 2026-11-06):** Refuse 0 everywhere (cashier, member payment, direct calls) unless a discount brought the price to 0. Checked: codes allow 1 to 100% (`0002_tables.sql:136`), so a 100% code can reach 0.
- [x] **D5 — password rules (owner, 2026-11-06: as recommended):** Today 6 to 64 characters, length only. Decided: 8 to 64 with at least one letter and one number, for new passwords only (registration, change, reset, temporary); existing passwords keep working. The Supabase Auth dashboard setting must match, or the rule is only client-side. Owner to change that setting.
- [x] **D6 — overview layout:** see the answer above. Does simplifying replace the eight cards and the black "Open" buttons the owner kept on 2026-11-01? Recommend: a mockup first in `docs/mockups/`, as with the full-page layout, showing a number strip plus a compact list of links.
- [x] **D7 — audit table data (owner, 2026-11-06: as recommended, IP kept 90 days):** Show the existing `audit_log` in a table, adding `ip_address` and `role` columns filled from now on, with older rows blank rather than guessed. IP comes from the request headers Supabase passes through, and is personal data, so it is cleared from rows older than 90 days; the rest of the row stays. Filters: date range, user, action type, object type. Export: CSV of the filtered rows.
- [x] **D8 — email check exposure (owner, 2026-11-05: build the live check):** Showing "already in use" tells anyone whether an address has an account, however well it is rate limited; the owner accepts that for this gym. Edge Function `check-email` with a per-IP limit on the server.
- [x] **D9 — refund formula (owner, 2026-11-05):** Activated = the term's start date has passed or the member has checked in during it, whichever is first. Before that, a full refund with no 24-hour limit. After that, the request goes to staff for manual review with a required reason: gym failed to provide the service, member was misled, contract allows it, illness or relocation, or change of mind (not automatic). Staff decide; the app only surfaces the case. The wording should be checked by someone qualified, since the guidance is not legal advice.
- [x] **D10 — what a refund cancels (owner, 2026-11-05):** The refunded term only. A later paid renewal has its own payment and stays; staff see a warning when approving.
- [x] **D11 — amenities per session (owner, 2026-11-05):** Reverses D6 and D9 of the 2026-10-20 plan. A session is one booking of 30 minutes to 2 hours. Passes already sold stay valid until they expire. Solo use without a coach present ends. Any checked-in coach counts as present, checked at the time of use. The owner has not given a price, and today's prices are for 30-day passes, so nothing was converted: each amenity keeps selling its pass until staff enter its session price with Set session price. New amenities are per session from the start.
- [x] **D12 — offline check-in (owner, 2026-11-05):** Reverses "offline is never Active" in the 2026-10-20 plan. The desk keeps a local copy of member codes and statuses, refreshed while online. Offline scans show "Last updated at ..." rather than Active, and check-ins are queued with their time, flagged as offline, then sent on reconnect and rechecked by the server. Copies older than 24 hours show "can't check". Known risk, accepted: a member who expired since the last refresh can get in.
- [x] **D6 — overview layout (owner, 2026-11-06: mockup approved as is):** The eight cards and black "Open" buttons are replaced; title "Overview"; check-in list shows how each came in.
- [x] **D13 — status labels (owner, 2026-11-06: as recommended):** Active, Inactive and Deleted for accounts, packages, amenities and discount codes only. Memberships, payments and refunds keep their own statuses, because "Pending" or "Expired" would lose meaning.
- [x] **D14 — what CRUD means per entity (owner, 2026-11-05: approved):** Master data (accounts, packages, amenities, codes) gets full create, read and update, with delete as archive. Money and history (payments, refunds, terms, check-ins, audit log) can't be edited or deleted, only reversed by the existing actions.
- [ ] **D15 — items without a place in the priority order:** The list's order doesn't mention amenities (7), QR offline (10), the forgot-phone fallback or status labels (15). Recommend: 7, 10 and forgot-phone after refunds and archiving (Phase 6), and 15 with the UI (Phase 7). This plan assumes that until the owner says otherwise.
- [x] **D18 — phone and member goals (owner, 2026-11-05):** Remove the update permission and leave the columns unused, because nothing on the to-do list asks for them. Stored values stay.
- [x] **D19 — editing discount codes (owner, 2026-11-05):** The end date can also change. Code and percent stay fixed, because people may already have used them at that value.
- [x] **D20 — unused delete permissions (owner, 2026-11-05):** Removed from shifts and notifications.
- [x] **D21 — what "activated" means (owner, 2026-11-05):** Changes D9. Activated = the member has checked in during the term. A paid term with no check-in in it, started or not, is owed a full refund.
- [ ] **D16 — "every function" in the testing item:** Recommend: every database function callable from the app gets at least one attempt to break it, enforced by a `rules.mjs` check that fails when a function has no test. UI screens are covered by the listed cases only.
- [x] **D22 — redesign scope (owner, 2026-11-06):** Windows: every page for every account type, including sign-in, registration and the locked screen. Android: member and coach screens only (with sign-in and registration). Android front-desk and admin layouts stay; they inherit the colours.
- [x] **D23 — colours (owner, 2026-11-06):** Yellow and black after the owner's reference dashboard, in both light and dark themes. Yellow is the signal colour, not the background of everything.
- [x] **D24 — logo (owner, 2026-11-06):** The "Health & Fitness Gym Center" badge (`docs/screenshotsforexample/882263184_1999024240766862_3446343023741622644_n.jpg`) where there is room; option B, an "HF" monogram in the badge's ring, where it is small (side menu, icons, splash, phone headers). Chosen in the visual companion.
- [x] **D25 — name (owner, 2026-11-06):** "Health & Fitness Gym Center" in full beside the badge; "Gym Center" where space is tight (window title, Android app name, side-menu tooltip).
- [x] **D17 — role names (owner, 2026-11-05):** Super admin is now called **System admin**, and Sub-admin is now called **Admin**. Implementer's call, not asked: this changes the names people see only, and permissions stay the same. The internal codes stay as they are (`admin` = System admin, `subadmin` = Admin), because renaming them would touch applied migrations, permissions and existing accounts. From here on, "admin" in this plan's code references means the code; on screen it means the former Sub-admin.

## 3. Phases and item coverage

Order set by the owner: 3, 2, 1, then CRUD (13), refunds and archiving, UI and audit logs, testing. Phase 6 and item 15 are placed per D15. The list uses the number 2 twice; the refund formula is called "R2" here.

| Phase | Scope | Items | Needs | Status |
| --- | --- | --- | --- | --- |
| 1 | Parameterized query audit | 3 | D2 | VERIFIED |
| 2 | Live email-in-use check | 2 | D2, D8 | DEPLOYED |
| 3 | Lock names after creation | 1 | D2, D3 | DEPLOYED |
| 4 | CRUD | 13 | D14, D18-D20 | DEPLOYED |
| 5 | Refunds and archiving | 11, R2, 6, 8, 9, 12 | D9, D10 | DEPLOYED |
| 6 | Amenities and check-in fallbacks | 7, 10, forgot phone | D11, D12, D15, D21 | DEPLOYED |
| 7 | Simplify admin overview, status labels, role names | 4, 15, role names | D6, D13, D17 | VERIFIED (no database change) |
| 8 | Audit log table and labels | 5, 14 | D7 | DEPLOYED |
| 9 | Yellow and black redesign: every Windows page, Android member and coach screens, new logo and name | Added 2026-11-06 | D22-D25 | PLANNED |
| 10 | Try to break the system | Testing | D4, D5, D16 | PLANNED |
| 11 | Final check and report | All | Phases 1-10 | PLANNED |

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
- [x] Edge Function `check-email`: validates the address, returns only `in_use` true or false. The limit is on the server in `check_email_in_use` (`0039`), callable by the service role only, so the `rules.mjs` anonymous check stays as it is. Public by `verify_jwt = false` in `supabase/config.toml`. (2026-11-05; the function itself has not been run, because Deno isn't installed here. See the last task.)
- [x] Limits (implementer's call): 30 lookups per network address and 600 overall in 10 minutes. The overall limit catches forged or rotating addresses. Addresses are hashed, rows last 10 minutes, and the email looked up is not stored. (2026-11-05, `supabase/tests/local/email-check.mjs`, 8 checks.)
- [x] `IAuthService.EmailInUseAsync`, with the same behaviour in `DemoGateway`. (2026-11-05, gateway tests in `SupabaseGatewayTests`.)
- [x] `RegisterViewModel`: 400 ms debounce with cancellation of the previous lookup; no lookup until the email shape is valid. Fast typing sends one lookup, and a slow old answer never overwrites a newer one. (2026-11-05, `RegisterEmailCheckTests`.)
- [x] (`impeccable`, `frontend-design`) `RegisterPage.xaml`: message under the email field in the existing `FieldError` style, announced for screen readers. A failed or refused lookup shows nothing and does not block registering. Copy: "This email already has an account. Sign in, or use a different email." (2026-11-05: checked in a Windows demo build at 1180 and 520 px wide, dark theme: appears after the pause, clears on edit, ignores case. The screen reader announcement, light theme and Android screen have not been checked.)
- [x] The duplicate error on submit stays (`SupabaseGateway.cs`); it is the real guard. (2026-11-05, test added.)
- `ponytail:ponytail-review` (2026-11-05): one redundant call removed. xUnit 792/792 after that; database suite 315 counted, 0 failed, run before that one-line C# change.
- [x] Applied `0039` (`db push`) and deployed `check-email` (`functions deploy --use-api`, `verify_jwt=false` confirmed) on the owner's go-ahead, 2026-11-05. Migration history up to date; `create-user` and `reset-password` unchanged.
- [x] Live function checks (2026-11-05): real account and the same address in capitals answer `in_use: true`; a free address answers `false`; a bad address 400; GET 405. Forged addresses don't escape the limit: 36 calls with a different `x-forwarded-for` and `x-real-ip` each were counted as one address and refused at exactly the 31st lookup. A forged `cf-connecting-ip` is rejected by Cloudflare with 403. The overall limit of 600 was tested only in the local suite. Test rows were deleted from `email_checks` afterwards (0 rows left). The real account's email was read by script and not printed.
- [ ] Owner checks the message on the real register page, without creating a new account.

**Found while checking, already there, not changed**
- [ ] The sign-in and register layout keeps its two columns in a narrow Windows window: at 520 px wide the headings break mid-word ("accoun / t") and the brand panel clips. Fix with the Phase 7 design work (`AuthShell`).
- [ ] Empty fields on the register page show a lighter box inside the field border in dark mode. Check whether it is real or comes from the screenshot method, then fix with Phase 7.

### Phase 3: lock names (item 1)
- [x] Migration `0040`: revokes `update` on `full_name`, `first_name` and `last_name` from signed-in users and drops `set_my_name`. Simpler than the trigger first planned: the table privilege already refuses every client, including a writer that only sets `full_name`, and no other function writes names. (2026-11-05)
- [x] Sign-up, `create-user` and archive still work: names are set at creation by `handle_new_user`, which privileges don't affect. (2026-11-05, `names-email.mjs`, `account-archive.mjs`.)
- [x] Correction path per D3: `correct_name`, audited as `user.correct_name` with old name, new name and reason. Refuses Admin, front desk, coach, member and anonymous callers, and archived or unknown accounts. (2026-11-05, 7 new checks in `names-email.mjs`.)
- [x] (`impeccable`, `frontend-design`) Settings: names shown as plain text with "Names can't be changed after sign-up. If yours is wrong, ask the gym to correct it."; the Save name button, `SaveNameAsync` and `SetNameAsync` are gone. Users and roles: a "Correct name" action for System admins opens a panel built like Reset password. Same behaviour in `DemoGateway`. (2026-11-05)
- [x] Tests: `names-email.mjs` and the xUnit name tests updated; direct updates as a signed-in user, staff and a System admin are refused. `rules.mjs` now checks the table's letter rules directly, since app users can no longer reach them. (2026-11-05)
- Checked in a Windows demo build, dark theme, 1180 px wide: Settings shows the name with no input; the menu offers Correct name; the first name is prefilled; a missing reason is refused with a message; a correction updates the list and shows a notice. The Settings wording was changed after that look (text only). Not checked on screen: Admin not seeing the action (xUnit only), light theme, narrow window, Android Settings.
- `ponytail:ponytail-review` (2026-11-05): nothing to cut.
- [x] Applied `0040` live on the owner's instruction, 2026-11-05 (dry run showed only `0040`). Confirmed live: signed-in users can't update the three name columns, can still update `phone`, `set_my_name` is gone, signed-in users can call `correct_name` (it checks the role itself), and anonymous users can't. "Save name" in installed versions (v0.4.0 and older) now fails with a general error until they update.
- [ ] Owner checks Correct name on the real backend.

### Phase 4: CRUD (item 13, marked very important)
- [x] Table below drafted from the live database's permissions and a read of the services, view models and screens (2026-11-05). Owner approved it the same day (D14). The findings below are the state before the fixes in this phase.

| Entity | Create | Read | Update | Delete | Finding |
| --- | --- | --- | --- | --- | --- |
| Accounts | Register; Users and roles | Users and roles | Roles, active, reset password, sign-in email; name locked (Phase 3) | Archive | Looks complete |
| Phone (all accounts) | None | None | Database allows it, no screen | None | **G1: stored but nowhere in the app** |
| Member goals | None | None | Database allows it, no screen | None | **G1: stored but nowhere in the app** |
| Coach profile | Created with the account | Coaches list, My coach profile | My coach profile | With the account | Looks complete |
| Packages | Packages and amenities | Same; member Packages | Same | Archive; restore in Archive | Looks complete |
| Amenities | Packages and amenities | Same; member Amenities | Same | **Deactivate only, no delete button** | **Gap: item 12, fixed in Phase 5** |
| Discount codes | Discounts | Discounts | **Limit and on/off only**; code, percent and dates are fixed | Archive; restore in Archive | **G2: dates can't be changed** |
| Gym settings | One fixed row | Gym settings | Gym settings | Not applicable | Looks complete |
| Shifts | My shifts | My shifts, booking | Status | Cancel; **database also allows real delete, unused** | **G3: unused delete permission** |
| Notifications | Server only | Bell | Mark read | **Database allows delete, no screen** | **G3: unused delete permission** |
| Sessions, requests, reservations | Through the booking flows | Their pages | Reschedule, respond, extend | Cancel; history kept | Looks complete |
| Messages, support | Send | Messages | Mark read | None; history kept | By design per D14 |
| Payments, refunds, terms | Through the payment flows | Their pages | Verify, void, refund decisions only | None; reversed instead | By design per D14 |
| Check-ins, scans, audit log | Server only | Their pages | None | None | By design per D14 |
| Roles and permissions | Fixed in the database | Used by the app | Assign and remove roles | None | By design |
- [x] Each row tested (2026-11-05). New in this phase: `supabase/tests/local/crud-rules.mjs` pins all 38 tables' permissions against the approved table, so a new table fails until it is listed, and `CrudTests.cs` runs packages, amenities, codes, accounts, gym settings, coach profile and shifts through the app's own services. Sessions, requests, reservations, messages, payments, refunds and check-ins rely on the suites already there (`session-requests`, `structured-requests`, `reservations`, `phase7`, `payment-authority`, `member-qr`, `rules`); no new behaviour was added to them.
- [x] Gaps fixed in `0041` and the app (2026-11-05):
  - G1: phone and goals no longer editable; demo refuses direct edits of profiles and member rows too.
  - G2: a code's end date can change, through one Edit panel with the limit. Changing only the date leaves an old unlimited code unlimited. Code and percent are refused by a trigger, not column rights, so installed apps that send every column back on save keep working.
  - G3: delete permissions and policies removed from shifts and notifications.
  - Removed the gateway's `DeleteAsync`, which nothing in the app called, so the app has no physical delete path at all.
- **Found and fixed while testing:** the discount list showed "valid until" one day late, because `ExpiresAt` is the midnight that ends the chosen day. It now shows the real last day (`DiscountRow.LastDay`).
- **Still open:** amenities have no delete button; that is item 12 in Phase 5.
- (`impeccable`, `frontend-design`) Checked in a Windows demo build, dark theme, 1180 px wide: the list shows the Edit button and last day, and the panel opens with the limit and date filled in. The confirm dialog appeared, but the final save and the reworded dialog text were checked by xUnit only, not on screen. For an old unlimited code the panel shows "0 used, no limit" above a Max uses box prefilled with 1, which is safe but slightly confusing; the dialog states the result. Not checked on screen: light theme, narrow window, Android.
- `ponytail:ponytail-review` (2026-11-05): 2 small cuts applied.
- [x] Applied `0041` live under the per-phase rule, 2026-11-05 (dry run showed only `0041`). Confirmed live: all 38 tables match the approved table, the `discounts_fixed_guard` trigger exists, and the two delete policies are gone. Installed apps are not expected to be affected: they send a code's unchanged values and never used these permissions.
- [ ] Owner checks Edit on a discount code on the real backend.

### Phase 5: refunds and archiving (items 11, R2, 6, 8, 9, 12)
- [x] Item 11: approving a refund cancels that member's term and ends their access if it was the running one; a later paid term and other members are untouched. Already built in `0025`; now pinned by tests in `phase7.mjs` and `SubscriptionTests`. (2026-11-05)
- [x] R2: `0042` changes `request_membership_refund`. A term that hasn't started: no reason category, full refund owed. A started term: a reason category is required and staff review it. The 24-hour window is gone. `amount` stays equal to the payment. (2026-11-05)
- [x] (`impeccable`, `frontend-design`) `RefundsPage`: each card says which case it is and the reason, warns about later paid terms, and labels the amount "Full refund" or "Refund requested" so a case under review doesn't read as already owed. My membership: a started term asks for a reason from a list first, and the confirm dialog says a refund isn't guaranteed. (2026-11-05)
- [x] Tests at the edges: exactly at the end of the term and after, 25 hours after payment, queued term, each reason, unknown reason, second request, later paid terms. The 0-amount and voided-payment refusals are unchanged and covered by existing `phase7.mjs` checks. (2026-11-05)
- [x] Item 6: archiving a deleted user keeps their history. Already built in `0023`; covered by `account-archive.mjs` and `CrudTests`. No change needed. (2026-11-05)
- [x] Items 8 and 9: already behave this way. Turning a code off archives it, so every inactive code is in the Archive and can be restored, and the row is kept; the Discounts list only shows active codes, so there is nothing further to delete. Pinned by `CrudTests`. **Owner to confirm this matches what they meant.** (2026-11-05)
- [x] Item 12: the missing Archive action on amenities, System admin only, with a confirm panel like packages. The row stays, passes already sold are kept, and it can be restored. (2026-11-05, `CrudTests`.)
- **Implementer's calls, not asked:**
  - A term that has ended can't be requested, because there is nothing left to cancel.
  - Staff can still reject a term that hasn't started, for example if the payment is disputed.
  - Staff approve the full amount or reject; there are no partial refunds, as before.
- Installed versions (v0.4.0 and older): their request button only appears in the old 24-hour window, and on a started term they send no reason, so they get a general error until they update. A queued term still works, because the two-argument call is still accepted.
- Noticed, not changed: on packages, amenities and codes, Deactivate and Archive have the same effect apart from the recorded reason. Worth simplifying with the Phase 7 design work.
- Checked in a Windows demo build, dark theme, 1180 px wide: member reason list, confirm dialog and pending text; the staff card for a started term; the amenity Archive button and confirm panel. The amount label was changed after that look. Not checked on screen: the not-started flow, the later-terms warning, the final approve, the final archive click (these are covered by tests), light theme, narrow window, Android.
- `ponytail:ponytail-review` (2026-11-05): nothing to cut.
- [x] Applied `0042` live under the per-phase rule, 2026-11-05 (dry run showed only `0042`). Confirmed live: the new function and columns exist, the old two-argument function is gone, the API reaches the function with 2 and 3 arguments, anonymous callers are refused, and the one older request was left unchanged.
- [ ] Owner checks a refund request and an amenity archive on the real backend, using test records.

### Phase 6: amenities and check-in fallbacks (items 7, 10, forgot phone)
- [x] D21 (`0043`): a term counts as activated only if the member checked in during it. A paid term with no check-in in it is a full refund, however long ago it started. (2026-11-05, `phase7.mjs`, `SubscriptionTests`.)
- [x] Item 7, sessions (`0043`): buying a per-session amenity gives one session, and one booking of touching slots uses it. Cancelling before the start gives it back. A timed pass is used first. Two separate bookings on one session make neither. Online purchases and counter payments are both covered. Nothing is converted automatically; `Set session price` on each amenity switches it, and an ordinary edit never does. (2026-11-05, `amenity-sessions.mjs`, `AmenitySessionTests`.)
- [x] Item 7, coach present (`0043`): logging an amenity use, and any booking starting within the next slot, needs a coach checked in; an archived coach doesn't count. A later booking can't be checked yet, so it is allowed. The Amenities page says when no coach is in. (2026-11-05)
- [x] Item 10 (`0044`, `OfflineDesk`): the desk saves who has paid and refreshes every 30 minutes. A network failure, and nothing else, falls back to it. An offline scan shows the saved list with "Last updated at ...", never Active; a copy older than 24 hours or missing can't check anyone. Check-ins are queued with their real time and survive a restart. Sending happens every minute and on Send now. The server rechecks each against the term covering that time, resending can't double up, and a refusal is shown to staff, not dropped. (2026-11-05, `checkin-fallbacks.mjs`, `OfflineDeskTests`.)
- [x] Forgot phone (`0044`): the Check-in name search shows "Paid until ..." or "No active membership" and disables Check in when there is none; the server still refuses. The check-in records `manual` and who did it. Works from the saved list when offline. (2026-11-05)
- **Known limits, not solved:**
  - The app must already be signed in when the connection drops; signing in needs the server.
  - The coach rule covers logged uses and imminent bookings. Nothing physically stops a member with a booking made earlier if no coach arrives.
  - Offline scans aren't in the scan log, because `scan_member` needs the server; only the resulting check-ins are recorded. D12 of the 2026-10-20 plan logged every scan.
  - The saved list is a plain file in the Windows user's app data, holding names and member codes.
  - Accepted by the owner: someone who expired since the last refresh can get in while offline.
- Installed versions (v0.4.0 and older): check-in, buying and booking still work. They don't show the session wording or the coach notice, and a refused use shows a general error.
- (`impeccable`, `frontend-design`) Checked in a Windows demo build, dark theme, 1180 px wide: the session price panel, and the Check-in list with paid status and disabled buttons. Not checked on screen: every offline state, because the demo can't lose its connection (covered by tests, owner to try with Wi-Fi off), buying and booking a session, the no-coach notice, light theme, narrow window, Android.
- `ponytail:ponytail-review` (2026-11-05): 2 small cuts applied.
- [x] Applied `0043` and `0044` live under the per-phase rule, 2026-11-05 (dry run showed only those two). Confirmed live: all active amenities still sell passes and no session rows exist, the two triggers are on, the old one-argument `record_check_in` is gone and the API reaches the new one with one argument, anonymous users are refused, and the app can't call `coach_in_gym` directly. No coach was checked in at that moment, so amenity use is blocked until one checks in.
- [ ] Owner sets session prices, and tries a scan and a name check-in with Wi-Fi off on a real desk.

### Phase 7: admin overview, status labels and role names (items 4, 15, D17)
- [x] (`impeccable`, main design phase) Mockup `docs/mockups/admin-overview.html`, checked in headless Edge (dark and light, maximised and 900 px, both admin roles, a failed summary; URL options `?theme=light&role=admin&width=narrow&state=fail`). Owner approved it as is, 2026-11-06 (D6).
- [x] Built (`4ec5395`): a lime bar only when refunds wait (says how many are for a used term), four numbers that open Revenue, Users and roles, Packages and amenities and Discounts, the day's check-ins with QR / By name / Sent later, the other four pages as links, and the five newest audit events. Every page the cards reached is one click away, and the side menu is unchanged. The page heading was dropped because the window header already says "Overview", as on the other admin pages; the long captions and per-card Retry are gone. `AdminOverviewTests` (+5), `WindowsAdminNavigationTests`.
- [x] `scripts/Test-WindowsAdminNavigation.ps1`: card alignment check removed (no cards), title waits moved to `AdminOverviewUpdated`, sign-in by "System admin". Passes against a demo build, all 8 shortcuts, 2026-11-06.
- [x] Checked in a Windows demo build at 900 px and maximised, light and dark, as System admin and Admin (2026-11-06). Not seen on screen: the lime bar and check-in rows (the demo has no pending refund or check-in today; both covered by tests).
- [x] Item 15: `RecordStatus.Of` in `Foundation.cs` gives Active, Inactive or Deleted; used by the Users, Packages and amenities, Discounts and Archive chips (`RecordStatusTests`). An expired code says Inactive and its card still shows the last day; the Archive keeps its Expired filter. The chip colours ignore case and treat Deleted as bad.
- [x] Role names (D17): `UserRow.RoleName` is the one place; the shell uses it through `RoleTitle`. Wording changed in errors, the Users role picker and actions ("Make admin"), archive messages and the demo sign-in buttons ("System admin", "Admin"; they never had AutomationIds, so the two Windows scripts now find them by the new names).
- [x] Role-picker trap: `UserRow.RoleCode` maps picker names explicitly and refuses anything else; "System admin" is never offered. Tests: choosing "Admin" creates `subadmin`, a forced "System admin" is refused before any call, and a direct `create-user` for `admin` fails in the demo. The live `create-user` only accepts member, coach, employee and subadmin (`ROLES`), so the server refuses it too.
- [x] Title: "Overview", in the window header only. The Audit Log role column (Phase 8) uses `RoleName`.
- [ ] `scripts/Test-WindowsArchive.ps1` was already out of date before this phase: it opened the first account (the System admin, who can't be deleted since `0037`) and a page called "Discounts & Archive". Both fixed; it now fails later, at "Restore record", because the first archived row it opens can't be restored. Fix in Phase 11.
- `ponytail-review` (2026-11-06): one shrink applied (`RoleTitle`); two cosmetic findings left (one-caller `Tap` helper, two "has text" properties the tests pin).

### Phase 8: audit log table and labels (items 5, 14)
- [x] `0045`: `ip_address` and `role` on `audit_log`, filled inside `audit()` from `request_ip()` (Cloudflare's `cf-connecting-ip`, else the first `x-forwarded-for`; null, never an error, if unreadable) and `main_role()` (RoleResolver's order). Older entries stay blank (D7). `create-user` and `reset-password`, which write the log directly, fill both through `auditBy` in `_shared/admin.ts`. A nightly job (`clear-audit-ips`, 00:20 Manila) clears addresses older than 90 days. (2026-11-06, `audit-log.mjs`, 10 checks.)
- [x] Server-side paging: `audit_records` returns a page newest first (the app asks for 11 to know about a next page) with filters for Manila dates, part of the user's name, action type (the part before the dot) and object type (the table), for `audit:read` only. Account and role entries also get the person's name. `AuditAsync`, which loaded the whole table, is gone; the overview reads the first page. The demo mirrors it. (2026-11-06, `audit-log.mjs`, `AuditLogTests`.)
- [x] Item 14: `AuditLabels` in Core (`user.archive` is "Account deleted"; 24 actions, 7 action types, 8 object types). `AuditLogTests` scans the migrations, Edge Functions and demo and fails on any code or table without a label; an unknown code shows as itself. The demo now audits catalog archives under the table name, as `0024` does.
- [x] (`impeccable`, `frontend-design`) `AuditPage.xaml`: filters panel, then a table with `TableHead` and `c:TableBody` (`WideFrom="1150"`, cards below that) with the nine columns: date and time, user, role, IP address, action type, action, object type, object, change details. Previous and Next are disabled at the ends. The log view uses the full width, left edge in line with the Audit log and Archive buttons.
- [x] Export CSV: every row the applied filters match, read 1000 at a time, through the system Save dialog. Every cell is quoted; one starting with `=`, `+`, `-`, `@`, a tab or a return gets a leading `'`. (2026-11-06, `AuditLogTests`.)
- [x] Checked in a Windows demo build, maximised and at 900 px, dark (2026-11-06). Fixed from that look: each dropdown showed its label twice, the table was off-centre from the buttons above it, a date wrapped, and role entries showed an id instead of a name. Not seen on screen: a full page of 10 rows and the Save dialog (the demo has two entries; both covered by tests).
- [x] Applied `0045` and deployed `create-user` and `reset-password` under the per-phase rule, 2026-11-06 (dry run showed only `0045`). Confirmed live: both columns exist, the nightly job is scheduled, `audit()` fills the address, signed-in users can call `audit_records` but not `main_role` or `clear_old_audit_ips`, and an anonymous call through the API is refused with 42501. Not yet seen live: a real entry with its address and role; the next audited action will show it.
- [ ] Owner checks the Logs page on the real backend after doing any audited action (for example a role change on a test account): the IP address and role columns should be filled for it.

### Phase 9: yellow and black redesign (added 2026-11-06, D22-D25)
Design: `docs/superpowers/specs/2026-11-06-yellow-black-redesign-design.md` (scope, colour table, logo and name rules, the page standard, the page list). Every task uses `impeccable` and `frontend-design`. No behaviour or database change: every action and fact on a page today stays reachable, and AutomationIds stay on the same controls. After each task: xUnit passes, `scripts/Test-WindowsAdminNavigation.ps1` passes against a demo build, and the screens are checked in the demo app at about 900 px and maximised, light and dark (Android on the `pixel_7_-_api_36_0` emulator).
- [ ] 9.1 Mockup in `docs/mockups/yellow-black.html` with the real tokens from the design's colour table: the sign-in screen with the badge, the Overview, one member page (My membership) and one coach page (Dashboard) on Windows, and My membership on Android, each light and dark; the HF monogram at 128, 48 and 32 px. Contrast checked (body 4.5:1, large 3:1, muted text on page and panel). Owner approves before the app changes. (2026-11-06: drafted and checked in headless Edge, every screen light and dark, sign-in and coach also at 900 px; URL options `?screen=signin|overview|member|coach|android|colours&theme=dark&width=narrow`. Contrast measured: all text pairs pass; yellow on white is 1.2:1, so in light it is only a filled surface with black on it and the focus ring is black. Waiting for owner approval.)
- [ ] 9.2 Colours and shared pieces: `Colors.xaml` remapped to the approved values with names that say what they are (no colour left pointing at the old sage or lime); `Styles.xaml` (HeroPanel becomes the yellow attention panel, InkButton and the other buttons, Panel, TablePanel, Chip, Field, fonts unchanged); `StatusChip` meaning colours; `IconRail.Windows.cs` side menu (yellow in light, `#161616` in dark, selected item black or yellow); `HeaderBar`; `IconView` chips; Android bottom bar. `PageLayoutTests` rules renamed with the styles, not dropped. Every page picks up the new colours here, before any layout work.
- [ ] 9.3 Logo and name: HF monogram as vector artwork replacing `DumbbellLogo` in the side menu and phone headers; `Resources/AppIcon/appicon.svg` and `appiconfg.svg`, `Resources/Splash/splash.svg`; the badge as `Resources/Images/gym_badge.png` (trimmed to the circle, on a white round in both themes). "Gym Center" in `App.xaml.cs` (window and title bar), `MemberShell.xaml` title, `IconRail.Windows.cs` tooltip and screen-reader name, the `Styles.xaml` heading and `ApplicationTitle` in the project file; `ApplicationId` unchanged. Demo gym name "Health & Fitness Gym Center". A test pins that "Gym Membership" no longer appears in the app's screens or project title.
- [ ] 9.4 Entry pages on Windows and Android: sign-in, registration, locked. Full badge and the full name; fixes the Phase 2 notes (two-column layout breaking at 520 px, the lighter box inside empty dark fields).
- [ ] 9.5 Member pages, Windows and Android: My membership, Packages, Amenities, Book an amenity, Coaches, Sessions, Time requests, Messages, Settings.
- [ ] 9.6 Coach pages, Windows and Android: Dashboard, Sessions, Time requests, Trainees, My shifts, My coach profile, Amenities, Messages, Settings.
- [ ] 9.7 Front-desk pages, Windows only: Home, Payments to verify, Cashier, Check-in, Amenity status (with verify amenity use). Check on Android that these still read correctly in the new colours, without relayout.
- [ ] 9.8 Admin and System admin pages, Windows only: Users and roles, Packages and amenities, Discounts, Cancellations and refunds, Void a payment, Gym settings, Revenue, Logs and Archive (and the Archive panel). The Overview gets the new colours only; it is the standard.
- [ ] 9.9 `impeccable` review across all changed screens, fixes made; `ponytail-review` on the phase diff; Windows and Android builds; plan and progress updated. Owner checks the new look on a real Windows desk and a phone.

### Phase 10: try to break the system (testing)
Each attempt gets a recorded result: refused correctly, or a bug with its fix. Test both the app's validation and the server, because the app's checks can be bypassed.
- [ ] Regex validation: bad names, emails and money values against `Services.cs:43-57,617` and the database constraints, which should agree.
- [ ] Check-in without a valid membership: expired, pending, archived and unknown QR code, by scan and by name search. The server must refuse `record_check_in` directly.
- [ ] Account with an existing email: differing case and surrounding spaces, at registration and through staff `create-user`.
- [ ] Password rules per D5: too short, too long, missing a letter or number, on register, change, reset and temporary password.
- [ ] Paying 0 per D4, plus negative amounts, at the cashier, member payment and through a direct function call.
- [ ] Every other database function per D16, with the `rules.mjs` check that fails when one has no test.

### Phase 11: final check and report
- [ ] xUnit and database suites pass; Windows and Android builds pass; `scripts/Test-WindowsAdminNavigation.ps1` passes.
- [ ] `ponytail:ponytail-review` on the changes; findings recorded.
- [ ] `impeccable` review of every screen changed by this plan, including Phase 9; fixes made and findings recorded.
- [ ] Report to the owner: which items are done, which need clarification, which to check manually.
- [ ] With the owner, separately: apply migrations, deploy Edge Functions, set the Auth password rule, open the PR, release.

## How to check (demo mode)
- Real config `src/GymMembership.App/Resources/Raw/supabase.json` must be moved out of the project before a demo build and put back right after (never commit it, never leave a `.bak` in the repo). Android demo builds also need `--no-incremental`.
- Demo sign-in buttons on the login page: Member, Coach, Front desk, System admin, Admin.
- A demo Windows build can go to a separate folder so Visual Studio's lock on `bin\Debug` doesn't matter: `dotnet build ... -f net10.0-windows10.0.19041.0 -p:OutDir=<folder>\` (move `supabase.json` out first and back after).
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

- **2026-11-06:** New Phase 9 added at the owner's request (yellow and black redesign, D22-D25); old Phases 9 and 10 renumbered to 10 and 11. Design written. No code changes.
- **2026-11-06:** Phase 8 done and deployed: audit log table with labels, 10 a page, filters and CSV export; `0045` live. xUnit 864/864; database suite exit 0, 363 counted.
- **2026-11-06:** Phase 7 done: overview rebuilt (mockup approved, D6), status labels (item 15), role names and the role-picker fix (D17). xUnit 850/850. No database changes.
- **2026-11-06:** D4, D5, D7, D13 answered by the owner. Phase 7 overview mockup drafted. No code or database changes.
- **2026-11-05:** Plan created from the to-do list and a read of the code. No code, database or git changes.
- **2026-11-05:** Complete to-do list received; added items 5 (rest), 6-15, R2, the forgot-phone fallback and decisions D9-D16. Phases renumbered to 10. No code, database or git changes.
- **2026-11-05:** Owner renamed the roles: Super admin to System admin, Sub-admin to Admin (D17, Phase 7). No code, database or git changes.
- **2026-11-05:** Branch `security-admin-cleanup-2026-11-05` created from `origin/main` (D2); plan committed there.
- **2026-11-05:** Phase 6 done and deployed: `0043` and `0044` live. Activated now means a check-in during the term (D21). xUnit 842/842; database suite exit 0, 353 counted.
- **2026-11-05:** Phase 5 done and deployed: `0042` live. Items 11, 6, 8 and 9 were already built and are now pinned by tests. xUnit 819/819; database suite exit 0, 330 counted.
- **2026-11-05:** Phase 4 done and deployed: `0041` live, CRUD table approved, 38 tables match it. xUnit 810/810; database suite exit 0, 327 counted.
- **2026-11-05:** Phase 3 deployed: `0040` applied live. Owner set the rule: push each phase's database changes when it finishes.
- **2026-11-05:** Phase 3 built: names locked, System admin correction (owner chose option A, D3), not deployed. xUnit 798/798; database suite exit 0, 322 counted.
- **2026-11-05:** Phase 2 deployed at the owner's request and the function tested live, including forged addresses.
- **2026-11-05:** Phase 2 built: live email check with server limits, not deployed. Owner chose the live check (D8). xUnit 792/792; database suite exit 0, 315 counted.
- **2026-11-05:** Phase 1 done: query audit found no injection path, tests added, one library limit recorded. xUnit 779/779; database suite exit 0, 307 counted. Owner added the `impeccable` rule for screen work.
