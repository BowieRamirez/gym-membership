# Owner's fixes — implementation and progress plan

Created: 2026-11-12
Last updated: 2026-11-12 (plan created; nothing built yet)
Status: WAITING. Decisions G1 to G18 are open; no phase has started.
Branch: `owner-fixes-2026-11-12`, a new branch from `main` at `b91cafe` (v0.5.0 plus the test-wait fix). Every phase of this plan stays on this branch; the owner merges to `main` when the plan is done.

**Goal:** Work through the owner's 9-item list of fixes (received 2026-11-12), plus item 10 added the same day: the cashier shift-in bug, discount codes that can't be changed once made and are shown to the members they fit, clean account names, an Actions menu and a proper add form for packages and amenities, drafts, publishing, archiving and versions for packages, a Membership checking page at the desk, amenity bookings held by a down payment with a solo or coached session, and gym closing days set by Admin that block bookings and are announced in chat.

**Separate from** `docs/superpowers/plans/2026-11-09-features-and-improvements.md` (done, v0.5.0), whose decisions are F1 to F19, and the 2026-11-05 plan (D1 to D31). This plan's decisions are numbered **G1 onward**. Where an item here reverses a D or F decision, the G decision says so.

## How to use this plan

This is the restartable checklist for the 2026-11-12 list. The original item numbers are kept. Mark things here when you stop and read here when you continue.

1. **Decisions first.** A phase starts only when every G decision it lists under "Needs" has the owner's answer recorded in section 2, with the date. Ask the open ones for a phase together, each with its recommendation, before writing code for it.
2. Work on one phase at a time, in the order below. Before starting, read `CLAUDE.md` and the current code; the paths here are starting points, not promises.
3. Change the phase status in the table to `IN PROGRESS`, fill in "Current work", and tick a task only when it is actually finished (`- [ ]` to `- [x]`, with the date and the test or commit that proves it).
4. Keep code, automated tests, manual checks and live deployment separate. Code written is not the same as verified or deployed.
5. Before stopping, update "Current work" and the session log: exact failing tests, uncommitted files, and the next concrete action.
6. Before resuming, read this file, run `git status --short`, check the branch and the last commit, and continue from the next action. Do not overwrite unfamiliar changes.
7. **Database changes (owner's standing rule):** when a phase changes the database, push its migrations and Edge Functions to the live project as part of finishing the phase once the dry run succeeds (`npx supabase db push --remote --dry-run`, then `--remote --yes`); no need to ask each time. Dry-run first, and push only that phase's own changes. Real account or payment operations, data edits, destructive changes to existing data, `git push`, merging and releases still need the owner's go-ahead.

Status vocabulary: `PLANNED`, `WAITING` (decisions open), `IN PROGRESS`, `BLOCKED`, `IMPLEMENTED`, `VERIFIED`, `DEPLOYED`. A ticked task does not mean the phase is accepted.

**Rules**
- Business rules live in the database and are mirrored in `DemoGateway`; hiding something in the UI is not enough.
- New migrations continue from the latest number: **next is `0059`**. Never rewrite an applied migration.
- **A migration that recreates functions** (drop and create) must revoke their grants explicitly from `anon` and `authenticated` where needed: Supabase's default privileges grant execute on every new function. After each push, check the live grants read-only (see `0055`/`0056`).
- Logic goes in Core with an xUnit test in `tests/GymMembership.Tests`; database rules get a check in `supabase/tests/local` (and `rules.mjs` keeps failing when an app-called function has no break attempt, D16).
- **Skills, every phase:** `superpowers:test-driven-development` for each Core and database rule (failing test first); `superpowers:systematic-debugging` for every bug (item 1 above all); `superpowers:verification-before-completion` before ticking a task; `ponytail:ponytail-review` on the phase diff before finishing (`CLAUDE.md`); context-mode tools for large outputs.
- **Skills, every screen:** the `impeccable` skill together with `frontend-design:frontend-design`. A new screen or flow gets `impeccable shape` and a mockup in `docs/mockups/` the owner approves before the app changes; a changed screen is captured first, then `impeccable critique`, fixes and `impeccable polish`. Copy goes through `impeccable clarify`. Snapshots stay in `.impeccable/critique/`.
- **Platforms:** member screens (codes on Packages and amenities, booking with a coach, the down payment) are checked on Windows (about 1100 px light, 1600 px dark) and on the Pixel 7 emulator in light and dark, scrolled, plus font scale 1.3, as real phone layouts. **Staff pages (Discounts, Packages and amenities, Users and roles, Cashier, Membership checking) are Windows only.**
- UI work: AutomationIds used by the UI scripts stay on the same controls (`ArchiveAmenity`, `SetSessionPrice`, `DiscountEdit`, `UserAccountActions` and the rest move into the Actions menus with the same ids where they still exist). `scripts/Test-WindowsAdminNavigation.ps1`, `Test-WindowsArchive.ps1` and `Test-WindowsSessionRequests.ps1` must still pass at the end of each phase that touches their pages; update a script only when its page really changed, and say so.
- Each new decision goes in section 2 with the owner's answer and the date.

### Current work / restart here

- **Where it stands (2026-11-12):** plan written from the owner's list and a read of the code. No code or database changes yet. Branch `owner-fixes-2026-11-12` holds only this file.
- **Next concrete action:** ask the owner G1 to G4 (Phase 1 and 2), and ask for the sketch named in item 4 (G8). Phase 1's shift-in bug needs no decision and can start right away.
- **Unrelated, left alone:** `.codex/`, `.gemini/`, `.impeccable/config.json`, `2026-10-30-visual-redesign.md` (modified), two PDFs in `docs/`.

## 1. Repository findings

From reading the code on 2026-11-12. Verify before relying on these; line numbers drift.

| Item | What exists today | Consequence |
| --- | --- | --- |
| 1 Shift in | `CashierViewModel.StartShiftAsync` (`StaffViewModels.cs:240`) calls `OpenShiftAsync`, then `LoadShiftAsync`, which sets `Shift`; `HasShift`, `NeedsShift`, `CashNeedsShift` hang off it. The owner sees the old "start shift" state until the page is reopened. Cause not found yet (stale read, a binding that isn't told, or the real gateway answering differently from the demo). | Reproduce on the real backend and in demo mode first, then fix at the cause with a test that fails today. No decision needed. |
| 2 Delete vs archive | Codes have Edit (until first use, F7/`0048`), Deactivate/Activate and Archive (`DiscountsPage.xaml`, three chips per row). Archive keeps the row (`archived_at`, `0024`); there is no real delete. | Delete for a never-used code is new; Deactivate and Archive stay for used ones (G1). |
| 2 No editing | `guard_discount_fixed` (`0048`) refuses code and percent changes always, and every other change once a code has a use; before that the end date, limit and "Show on Packages" can change. | "Never editable after creation" reverses F7's "editable until used" (G2). |
| 2 Which packages / amenities | A code applies to anything: `apply_discount` (`0003_helpers.sql:181`) is called by `avail_membership`, `avail_amenity` and `renew_membership` with no item check. | Needs a code-to-item link table and a check in `apply_discount` (G3). |
| 2 Once per person | Already enforced: `apply_discount` refuses a code the member has used (`invalid_code`), and `packages_codes` hides used codes (`0048`). | Keep; add a test that pins it for amenities too, and a clearer message than "invalid code" (`code_already_used`). |
| 2 Codes shown to members | `show_on_packages` (`0048`) lists chosen codes on Packages only, with "Use code". No amenity list, no "new" mark, no list of what a code fits. | Show codes on Packages and on amenity purchase, with discount, what it fits and end date; hide inactive, expired, used (G4). |
| 3 Names | `0035` split `full_name` into `first_name` and `last_name`; `last_name` may be `''` for old one-word names. Users and roles already has a "Correct name" form (`CorrectLastName`, `UsersPage.xaml:73`). New sign-ups need both parts. | Find the accounts with a blank last name (and any whose `full_name` doesn't match the parts), list them for staff, and let the member add it themselves (G5). Never fill a name in by guess. |
| 4 Row buttons | Packages: Edit, Deactivate, Archive. Amenities: Edit, Add or replace photo, Set session price, Deactivate, Archive (`CatalogAdminPage.xaml`). Accounts use one "Actions" button opening a list (`UsersPage.xaml.cs:26`, `OnAccountActions`). | Reuse the accounts pattern for packages, amenities and codes. |
| 4 Add form | Adding is an inline form on `CatalogAdminPage`; the photo is a separate row action; `image_path` exists on both tables (`0013`). The sketch "in the document" is not in `docs/gym-system-documentation.pdf` or `docs/gym-system-diagrams.pdf`. | A separate add page built to the owner's sketch, which we need (G8). |
| 5 Check-in | Page "Check-in" (`StaffShell.xaml:22`, rail icon `scan-line`): scan a rotating pass (`0049`) or search by name with the member's desk PIN; status is shown; check-ins are recorded with method and who recorded them (`0044`, `0049`); today's list (F19). | Most of item 5 exists. New: the name "Membership checking", a clear Confirm step and reason text for every inactive case (G10, G11), and login mode (G12). The existing Edge Functions (`check-email`, `create-user`, `reset-password` in `supabase/functions/`) show the pattern for a password check done on the server. |
| 6 Reservations | Booking holds the slot as `awaiting_payment` before anything is paid (`0052`), cancelled "Not paid" if the hold lapses. Payment is the full session price. `reservations.coach_id` exists; coaches say which amenities they run (`coach_amenities`, F14); coach shifts are in `shift_posts`. | Item 6 reverses the unpaid hold: no slot without a down payment (G13). Picking a coach needs "who is free then" from shifts, amenity links and other bookings (G14). |
| 10 Closing days | Gym settings has opening and closing times only (`0028`: `opens_at`, `closes_at`). Those hours are checked on the server wherever a time is set up: coach shifts (`0028` trigger, `outside_gym_hours`), amenity bookings (`book_slots`, `0029`/`0043`/`0052`) and session requests (`request_session`, `0031`). No closed dates exist. Admin and System admin announcements exist (`0051`, F10): an "Announcements" conversation in chat plus a notification. | A `gym_closures` table and a single "is that day closed" check added to every place the hours check already runs. The closure is posted as an announcement, so it reaches chat and notifications with no new channel (G16 to G18). |
| 7, 8, 9 Package lifecycle | `membership_packages`: name (unique), description, price, duration_days, is_active, image_path, archive columns. No draft, no published state, no version link, no real delete. A member's own terms are copied at purchase into `membership_terms` (`0018`), so a later package change doesn't touch them. | Add a draft/published state, delete only for never-published, never-bought packages, versions linked to the one they replace (G6, G7, G9). The unique name has to allow two versions of one package. |

## 2. Decisions to settle

Recommendations are proposals, not approved requirements. Record the answer and date beside each. Block only the phases affected.

- [ ] **G1 — delete, deactivate, archive for codes (item 2):** Recommend: **Delete** only when no payment of any state (pending, verified, voided, expired) ever named the code, and the row really goes. A code with any payment can be **Deactivated** (off, can be switched back on) or **Archived** (off and moved to the archive list); payments and history keep pointing at it. The Actions menu shows only what's allowed for that code.
- [ ] **G2 — nothing editable after creation (item 2):** Reverses F7 (editable until first use). Recommend: after creation only on/off and archive change; code, percent, dates, limit, "show to members" and the packages/amenities it fits are fixed from the start. To change terms, deactivate and make a new code. Edit leaves the Actions menu.
- [ ] **G3 — what a code fits (item 2):** Recommend: the create form asks which packages and which amenities, with "All packages" and "All amenities" shortcuts; at least one item is required. A renewal counts as its package. Stored in a new `discount_items` table and checked in `apply_discount` (`code_not_for_item`). A package's new version (Phase 4) does **not** inherit old codes; the admin makes new ones.
- [ ] **G4 — which codes members see, and where (item 2):** Recommend: keep a "Show to members" choice on each code (a code meant for one person stays hidden), on by default. A member sees shown codes that are active, started, not expired, not used by them and not at their limit, on Packages and on an amenity's buy step, each with percent, what it fits and the end date. "New" mark for codes made in the last 7 days. "Newly created" read as "shown from the moment it's made", not a separate feed.
- [ ] **G5 — fixing names (item 3):** Recommend: a migration lists nothing and changes nothing except trimming spaces and rebuilding `full_name` from the parts where they disagree (no names made up). Users and roles gets a "Name incomplete" filter and badge for accounts with a blank last name; staff fix them with the existing Correct name action. A member or coach with a blank last name sees "Add your last name" in Settings and can set it once (`0040` locks names; this one blank-to-filled change is allowed). New accounts keep requiring both parts.
- [ ] **G6 — draft, published, archived (items 7, 8):** Recommend: packages and amenities get `published_at` (null = draft). Drafts can be edited and deleted, are never shown to members and can't be bought (the server refuses). **Deactivate goes away for packages**: a package is Draft, Published or Archived. Delete only for a draft that was never published and never bought; anything else is archived. Archived: not for sale, renewals refused, existing memberships and payments untouched. An archived package can't come back on sale; make a new version instead.
- [ ] **G7 — amenities after publishing (item 8):** Item 8 locks published *packages*; versions (item 9) are only for packages. Recommend: published amenities stay editable (price and session details apply to new purchases only; paid sessions keep what was paid), with drafts and delete-vs-archive as packages. Amenity closure keeps using Amenity status (`0014`), so Deactivate goes for amenities too. Say if amenities should be locked and versioned like packages.
- [ ] **G8 — the add form (item 4):** Need the owner's sketch (it isn't in the two PDFs in `docs/`). Recommend until it arrives: a separate page (Windows only) with photo first, then name, description, price, and duration (packages: days; amenities: session length and price per session), with **Save as draft** and **Add** (Add publishes and checks every required field). The same page is used to edit a draft and to edit a new version.
- [ ] **G9 — versions (item 9):** Recommend: a new version copies name, description, price, duration, benefits and photo into a draft linked to the current one (`replaces_id`); one draft version per package at a time. "Publish" shows old and new side by side, changes highlighted, then publishes the new one and archives the old in one step. Members on the old version keep their terms; **renewing** an old-version membership renews into the newest published version at its price, said plainly on the renewal screen. "Benefits" don't exist as data today: add a benefits list (one line each) to packages, shown on Packages. The unique name rule changes to "one published package per name".
- [ ] **G10 — Membership checking flow (item 5):** Recommend: rename the page and the rail label to "Membership checking" (icon kept). Scan or search; the card shows photo, full name and status. A manual search still needs the member's desk PIN or an Admin/System admin reason (F8, unchanged). Active: a **Confirm check-in** button records the date and time; nothing is recorded by the scan alone. Not active: no button, the reason ("Expired on 3 Nov", "Payment not verified yet", "No membership", "Account deactivated") and "Please see the front desk staff." Check whether today's scan already records on its own before changing it.
- [ ] **G11 — "direct the member to staff" (item 5):** The page is used by staff, so the member is already at staff. Recommend: the reason line plus the next step for the staff member (Cashier for renewal, Payments to verify for a pending payment), as a button. Say if a member-facing screen (kiosk) was meant instead; that joins G12.
- [ ] **G12 — login mode (item 5):** **Defined by the owner, 2026-11-12:** a member at the desk with no phone for the QR and no desk PIN can check in by typing their **account password** instead. Recommend how it works: on Membership checking, staff find the member by name and choose "Sign in instead". The member types their own password into a hidden field on the desk screen, so staff never see or type it. A new Edge Function `verify-member-password` checks the email and password against Supabase Auth without signing anyone in on the desk or ending the staff session. The password is never stored, logged or kept in the offline queue, so this mode needs the internet. A correct password counts like a correct desk PIN: it gives a short proof that `record_check_in` accepts with method `password`. The wrong-try limit is shared with the PIN (`0049`), so 5 wrong tries lock the member's manual check-in for 15 minutes and the desk is told. The field clears after each try and when the card closes. Only accounts with the member role can use it. Say if the try limit should differ, or if coaches should be able to use it too.
- [ ] **G13 — down payment (item 6):** Reverses `0052`'s unpaid hold. Recommend: down payment is a Gym settings percent of the session price, **50%** by default, paid by GCash (member uploads the reference) or cash at the desk. The slot is reserved only when the desk verifies the down payment; before that others can book it, and if two pay for one slot the first verified wins and the other's down payment is refunded through Refunds. The rest is paid at the desk before the session. Cancelling: down payment refunded if cancelled 24 hours or more before; kept otherwise. Say the percent, the cancel rule and whether cash at the desk counts.
- [ ] **G14 — solo or with a coach (item 6):** Recommend: the member chooses Solo or With a coach. With a coach lists coaches who run that amenity (`coach_amenities`), have a posted shift covering the whole time, and aren't booked then; picking one fills `reservations.coach_id`; no coach approval step. A coached session costs a coach fee per session set on the amenity (₱0 allowed). Solo still needs a coach in the gym at the time of use (D21, `coach_in_gym`). Say if the coach fee should be the coach's own rate (`0033`) instead.
- [ ] **G16 — closing a day (item 10):** Recommend: Gym settings gets a "Closed days" list where Admin and System admin pick a date (today or later) and type a reason ("Holiday", "Repairs"); whole days only, Manila time. A closed day can be removed (reopened) up to the day itself. Past closed days stay in the list as history. Say if half days (closing early) are wanted too.
- [ ] **G17 — what a closed day blocks (item 10):** Recommend: nobody can set up anything on that day: amenity bookings, session requests and proposals, coach shifts. Membership check-in is refused too, with the reason shown on Membership checking. Memberships still count down through a closed day (no days added). Say if members should get the day back.
- [ ] **G18 — what happens to things already set up that day, and the notice (item 10):** Recommend: closing a day cancels that day's amenity bookings, scheduled sessions and coach shifts, with the reason "Gym closed: <reason>". Paid amenity bookings and down payments go to Refunds for the desk to pay back, as for any cancelled paid booking. Admin sees how many will be cancelled before confirming. Everyone is told through an announcement to all members and coaches (Announcements in chat, plus a notification): "The gym is closed on Friday, 25 Dec: Holiday." Members and coaches whose booking, session or shift was cancelled also get a personal note. Reopening the day posts "The gym is open again on ..." but does not bring back cancelled bookings.
- [ ] **G15 — old apps (all items):** Recommend: as before, the server refuses old flows (unpaid holds, editing codes, buying drafts); desks and phones need the new build. Release as v0.6.0 when the plan is done.

## 3. Phases and item coverage

| Phase | Scope | Items | Needs | Status |
| --- | --- | --- | --- | --- |
| 1 | Cashier shift-in bug; account names | 1, 3 | G5 (names only) | PLANNED |
| 2 | Discount codes: delete or archive, locked terms, items, once per person, shown to members, Actions menu | 2 | G1, G2, G3, G4 | WAITING |
| 3 | Package and amenity lifecycle in the database: drafts, publish, delete vs archive, versions | 7, 8, 9 | G6, G7, G9 | WAITING |
| 4 | Packages and amenities screens: Actions menu, add page, drafts, versions, members' Packages | 4, 7, 8, 9 | Phase 3, G8 | WAITING |
| 5 | Membership checking, including login mode | 5 | G10, G11, G12 | WAITING |
| 6 | Amenity reservations: down payment, solo or with a coach | 6 | G13, G14 | WAITING |
| 7 | Gym closing days | 10 | G16, G17, G18 | WAITING |
| 8 | Try to break the new parts; final check and report | All | Phases 1-7 | PLANNED |

### Phase 1: cashier shift-in bug and account names (items 1, 3)
- [ ] Baseline before any change: xUnit count, every database test file, Windows demo build, the three UI scripts. Record the numbers here.
- [ ] Shift-in bug (`superpowers:systematic-debugging`): reproduce in demo mode and on the real backend (Cashier → Start shift); write down exactly what stays stale. Find the cause in `CashierViewModel.StartShiftAsync`/`LoadShiftAsync`, `SupabaseGateway` `my_cash_shift`/`open_cash_shift`, or `CashierPage.xaml` bindings. A test that fails today (`CashShiftTests`: after Start shift, `HasShift` is true and the drawer strip shows without reopening the page), then the fix at the cause. Check End shift for the same fault.
- [ ] Names, database (`0059`, G5): trim and rebuild `full_name` where it disagrees with the parts; allow one blank-to-filled last-name change by the account owner despite `0040`; `names.mjs` (or the existing names test file) proves no name is invented, staff correction still works, and a filled last name can't be changed by the member again.
- [ ] Names, Core and screens: `UserRow.NameIncomplete`, a "Name incomplete" filter and badge on Users and roles; Settings "Add your last name" for members and coaches with a blank one (Windows and Pixel 7). Demo seed gets one account with a first name only.
- [ ] Push live (dry run first), check live grants; UI scripts; `ponytail-review`; plan updated.

### Phase 2: discount codes (item 2)
- [ ] Database (`0060`): `discount_items` (code to package or amenity, or all of a kind); `apply_discount` refuses a code for an item it doesn't fit (`code_not_for_item`) and a used one with `code_already_used`; `guard_discount_fixed` refuses every change except on/off and archive (G2); `delete_discount(id)` deletes only when no payment ever named it (`discount_used`); `member_codes_for(kind, item)` returns the codes a member may see (G4). `discount-limits.mjs` and a new `discount-items.mjs`: watched fail first, including another member's view, an expired code, a code at its limit and a used code.
- [ ] Core: `Discount.Items`, `DiscountRow.CanDelete`, `ErrorText` for the new errors, `IMembershipService` codes for an amenity, Discounts create form with item choice (no Edit), demo mirror. xUnit, mutation-checked.
- [ ] Screens: Discounts rows get one **Actions** menu like accounts (Delete or Deactivate/Activate, Archive, as allowed); the create form picks packages and amenities. Member Packages and the amenity buy step show the codes with percent, what it fits and end date, "New" mark. Windows; member screens on the Pixel 7 too. Mockup first for the member code card.
- [ ] Push live; UI scripts; `ponytail-review`; plan updated.

### Phase 3: package and amenity lifecycle, database (items 7, 8, 9)
- [ ] Database (`0061`): `published_at` on packages and amenities (existing rows published); `replaces_id` and `benefits text[]` on packages; the unique name becomes "one published package per name"; buying, renewing and booking refuse drafts and archived items (`item_not_for_sale`); a published package refuses edits (`package_published`); `publish_package(id)` checks required fields (`missing_fields`) and, for a version, archives the one it replaces in the same transaction; `new_package_version(id)` copies into a draft (one draft per package, `draft_exists`); `delete_catalog_item(kind, id)` only for never-published, never-bought rows. Renewal of an archived package renews into its newest version (G9). Tests: watched fail first; a member's terms and payments unchanged after a new version is published.
- [ ] Core: `Package.State` (Draft, Published, Archived), `Benefits`, `ReplacesId`, service calls for publish, new version, delete; old-vs-new compare in Core (`PackageChanges.Between(old, new)`) with xUnit; demo mirror.
- [ ] Push live; plan updated.

### Phase 4: packages and amenities screens (items 4, 7, 8, 9)
- [ ] Mockups from the owner's sketch (G8): the add/edit page and the Actions menu; owner approves before the app changes.
- [ ] `CatalogAdminPage`: one **Actions** menu per row, offering only what's allowed for its state (Edit draft, Publish, Create new version, Delete, Archive, Photo, Session price); Draft / Published / Archived shown on each row; a draft version shown under the package it will replace.
- [ ] Add/edit page: photo, name, description, price, duration or session details, benefits (packages), **Save as draft** and **Add**; missing fields named on Add. The publish compare for versions.
- [ ] Member Packages: drafts and archived never shown; benefits listed. Windows and Pixel 7.
- [ ] Critique and polish; UI scripts (`Test-WindowsArchive.ps1` touches this page); `ponytail-review`; plan updated.

### Phase 5: Membership checking (item 5)
- [ ] Database only if G10 changes when a check-in is recorded (scan no longer records on its own): `record_check_in` keeps the 10-minute scan rule; tests updated.
- [ ] Login mode, server (G12): Edge Function `verify-member-password` (signed-in front desk, Admin or System admin only) checks the member's email and password with Supabase Auth in a separate client, then issues a one-use proof valid for 2 minutes. `record_check_in` accepts that proof with method `password` (new value in the `method` check, next free migration number). Wrong passwords count toward the PIN's try limit. Tests: watched fail first. A member or coach can't call the function. A wrong password is refused and counted; after the limit even the right password is refused. A proof can't be used twice, after it expires, or for another member. The desk's session is unchanged afterwards. The password doesn't appear in any table, the audit log or the function's logs.
- [ ] Login mode, Core and screen: "Sign in instead" on the member's card next to the PIN step; a hidden password field (`IsPassword`, no autofill or saving); the error text tells the desk "Wrong password" or "Too many tries, try again in 15 minutes"; offline the option is hidden with "Needs the internet". xUnit for the view model, including that the field is cleared after every try.
- [ ] Core: `MemberCheck.Reason(status)` gives the reason text for every inactive case, xUnit for each; Confirm step in `CheckInViewModel`.
- [ ] Screens: rename to "Membership checking" (page title, rail, phone label if any, `StaffShell.xaml.cs` map); the card with photo, name, status, Confirm check-in or the reason and next-step button. Mockup first. UI scripts (`Test-WindowsAdminNavigation.ps1` names this page).
- [ ] Push live if this phase added a migration; `ponytail-review`; plan updated.

### Phase 6: amenity reservations (item 6)
- [ ] Database (next free number): `gym_settings.down_payment_percent` (50); a booking starts as a request with no slot held; verifying the down payment confirms it and refuses with `slot_taken` if someone else's was verified first; the losing down payment goes to Refunds; the `awaiting_payment` hold is removed. `reservations.mode` (`solo`, `coach`), coach fee on amenities, `free_coaches(amenity, starts, ends)`; booking with a coach refuses a coach who isn't free. Tests: watched fail first, two members paying for one slot, a coach booked twice, the cancel rule.
- [ ] Core: booking flow states, the coach list, down payment and balance amounts in Core with xUnit; demo mirror.
- [ ] Screens: Book an amenity (Solo / With a coach, the coach list, down payment step), desk Payments to verify and Amenity status show down payment and balance. Mockup first; Windows and Pixel 7 (light, dark, font 1.3).
- [ ] Push live; UI scripts; `ponytail-review`; plan updated.

### Phase 7: gym closing days (item 10)
- [ ] Database (next free number): `gym_closures` (date primary key, reason, set by, set at; read by every active user, written only by the functions); `gym_closed_on(date)`; the check added next to every `outside_gym_hours` check (coach shift trigger, `book_slots`, `request_session` and `propose_time`) and to `record_check_in`, refusing with `gym_closed`; `close_gym_day(date, reason)` (Admin and System admin only) cancels that day's bookings, sessions and shifts, sends paid bookings to Refunds, posts the announcement and the personal notes in one transaction; `closure_preview(date)` returns the counts; `reopen_gym_day(date)`. Tests (`gym-closures.mjs`): watched fail first; a member, coach or front desk account can't close a day; every booking path refuses a closed day; the announcement and personal notes arrive; a past date is refused.
- [ ] Core: `GymClosure` model, `ErrorText` for `gym_closed` ("The gym is closed on Friday, 25 Dec: Holiday."), Gym settings closed-days list, close and reopen with the preview count; the member calendar, Book an amenity and the coach's Post a shift mark closed days and don't offer them. xUnit; demo mirror.
- [ ] Screens: Gym settings "Closed days" section (Windows only); closed days greyed with the reason on the booking calendars, the member calendar and the coach's shift form (Windows and Pixel 7, light, dark, font 1.3). Mockup first.
- [ ] Push live; UI scripts; `ponytail-review`; plan updated.

### Phase 8: try to break the new parts; final check and report
- [ ] Break attempts on every new function from a member, coach and front desk account (`rules.mjs`), live grants checked read-only.
- [ ] Full xUnit, every database file, both builds, three UI scripts; report to the owner; PR to `main` on the owner's go-ahead.

## How to check (demo mode)
- Real config `src/GymMembership.App/Resources/Raw/supabase.json` must be moved out of the project before a demo build and put back right after (never commit it, never leave a `.bak` in the repo). Android demo builds also need `--no-incremental`; to see a demo APK on the emulator build it with `-p:EmbedAssembliesIntoApk=true`; afterwards build the real app with `--no-incremental` so `supabase.json` is packed again.
- A demo Windows build can go to a separate folder so Visual Studio's lock on `bin\Debug` doesn't matter: `dotnet build ... -f net10.0-windows10.0.19041.0 -p:OutDir=<folder>\`.
- Demo sign-in buttons: Member, Coach, Front desk, System admin, Admin.
- Windows captures: `PrintWindow` (flag 2). Android: `uiautomator dump` plus `input tap`; adb under `C:\Program Files (x86)\Android\android-sdk`; Git Bash needs `MSYS_NO_PATHCONV=1`.
- The owner often runs the app from Visual Studio. Ask before closing it.

## To-do list as received

Received 2026-11-12, as sent.

1. Cashier shifts
- Fix the shift-in bug. After a cashier starts a shift, the interface should immediately show the updated shift status.
2. Discount codes
- Allow administrators to delete a code only if it has never been redeemed.
- Allow used codes to be deactivated or archived, while preserving redemption and purchase history.
- Once a code is created, its details cannot be edited. To change its terms, deactivate it and create a new code.
- Specify which membership packages or amenities each code can be used with.
- Allow each person to use a given code only once.
- Replace the three separate action buttons with an Actions menu like the one used for accounts.
- Show members newly created, active discount codes that are available to them.
- For each code, display its discount, applicable packages or amenities, and expiration date if there is one.
- Hide codes that are inactive, expired, or already used by that member.
3. Accounts
- Fix inconsistent account name data, including accounts with a first name but no last name. Do not invent missing names; provide a way to correct them.
4. Membership package and amenity interface
- Replace separate row buttons with an Actions menu like the accounts menu.
- Improve the package and amenity interface so it is clearer and easier to use.
- When adding a package or amenity, open a separate form based on the document's sketch. Include a photo, name, description, price, duration or session details, and an Add button.
5. Membership checking
- Rename the member check-in section to Membership checking.
- Allow staff to scan a member's QR code or search for the member manually if scanning is unavailable.
- Verify the member's identity and display their membership status.
- If the membership is active, allow staff to confirm check-in and record the date and time.
- If the membership is inactive or expired, show the reason and direct the member to staff for assistance.
- Add a login mode; its exact process still needs to be defined.
6. Amenity reservations
- Require the down payment before confirming a reservation. Without it, the time slot is not reserved for the customer.
- Allow customers to choose a solo session or a session with a coach.
- If a customer chooses a coach, show coaches available for the selected date and time.
7. Package deletion and archiving
- Allow deletion only for unpublished packages that have never been purchased.
- Archive published or purchased packages instead of deleting them, including when they are no longer needed.
- Archived packages are unavailable for new purchases, but existing memberships and purchase history remain intact.
- Existing customers keep the package terms they originally purchased.
8. Draft packages and amenities
- Allow administrators to save incomplete packages and amenities as drafts and finish them later.
- Drafts can be edited or deleted and are not visible or available to customers.
- Require all mandatory fields to be completed before publication.
- Once a package is published, it cannot be edited. Changes must be made through a new version.
9. Create a new package version
- Add a Create new version action to published packages.
- Copy the package's name and information—including its description, price, duration, benefits, and photo—into a new draft.
- Let the administrator adjust the copied details and review the changes before publishing.
- Keep the current version available for sale while the new version is a draft.
- When the new version is published, archive the previous version from sale. Existing customers retain their original terms.
- Allow the new draft to be deleted without affecting the published version.

Added by the owner later on 2026-11-12, as sent:

10. "also add in plan add day closing in admin gym settings add a feature admin can pick a date to close gym no one can set up date for that day in amenities and sessions it will notif and show in updates in chats"

## Session log

- **2026-11-12:** Plan created from the owner's 9-item list and a read of the code, on the new branch `owner-fixes-2026-11-12`. Decisions G1 to G15 open; the sketch for item 4 requested. No code or database changes.
- **2026-11-12:** Owner defined login mode (item 5): a member with no phone and no PIN checks in by typing their account password at the desk. G12 rewritten with a recommendation; Phase 5 gains the server and screen tasks.
- **2026-11-12:** Owner added item 10 (gym closing days). Decisions G16 to G18 and Phase 7 added; the final check is now Phase 8.
