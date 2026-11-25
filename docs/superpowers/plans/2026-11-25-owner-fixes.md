# Owner fixes 2026-11-25 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-25
Last updated: 2026-11-25
Status: Fixes 1-2 BUILT and pushed (app only, no migration). Fix 3 (staff scheduling): Phases 3.1-3.5 done (0151-0155 live; shift check-in rule off until the admin turns it on from Scheduling); 3.6 (checks and rollout) TODO. S1-S9 confirmed. More fixes get added below as the owner sends them.
Branch: `owner-fixes-2026-11-25` (cut from `main` at `df3f54c`, v0.11.0). Owner merges to `main` when done; no push or PR without the owner.
Next migration: `0158`.

**Goal:** The owner's fixes after the v0.11.0 release.

**Tech Stack:** .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit; Supabase when a fix needs the database.

## How to use this plan

- One fix at a time; each ends working, tested and committed.
- Skills: `ponytail` full mode; `superpowers:test-driven-development` for rules; `superpowers:systematic-debugging` for bugs; `impeccable` and `frontend-design` for screen changes (staff pages are Windows only); `ponytail:ponytail-review` on each diff; `superpowers:verification-before-completion` before "done".
- Push a fix's migrations live when it is finished (dry run first, then check live grants).
- The owner tests the app themself; unit tests and builds here.

| Fix | What | Status |
|---|---|---|
| 1 | Cashier: Membership is register-only; Renewal lists existing accounts; no Register on Day pass | BUILT (`ef2c67c`, pushed) |
| 2 | Cashier: no Day pass cards under Membership/Renewal; amenity booking Member or Walk-in, one at a time | BUILT (`2db811e`, pushed) |
| 3 | Staff scheduling: admin sets coach and front desk shifts; check-in follows the shift; payroll pays from shift start (5 phases) | PLANNED |

## Notes from today (no change)

- **Why some bookings have no refund button at the desk (Amenity status):** the desk asks for refunds only for walk-ins (0096, `BookedRow.CanRequestRefund`). A member cancels their own booking in the app (`cancel_reservation`), which sends the refund request to the System admin. Working as decided.

## Fix 1: Cashier customer by kind

Owner, 2026-11-25 (screenshot of Cashier, Membership with the register form open): when people want an account made by the front desk, Membership should be just "Register new member", with no customer name search. Renewal is for people who already have an account, so it is the list of members, with no Register button. Day pass had the Register button again; remove it there too.

Decisions (asked 2026-11-25):

- Renewal lists every existing account: one with no active membership buys a package again (paid as a `membership`), one still active extends (paid as a `renewal`, as before). Someone on a Day pass until midnight is hidden. *Owner may narrow this to "no membership only"; then active members extend from their own app.*
- Renewal: the list shows at once, and the search box narrows it.
- Day pass: keep the member search; drop only Register.

Built:

- [x] `CashierViewModel.ShowsSearch` (not Membership) and `ShowsRegisterForm` (Membership with no member chosen). Register form has no Back; after registering, the new member is chosen and the packages show; "Change" brings the form back.
- [x] Removed `IsRegistering`, `StartRegister`, `CancelRegister` and the Register new member button.
- [x] `MemberSuggest.RunAsync(listAll)`: Renewal lists every member with nothing typed; the dropdown only shows for a typed search. Change customer on Renewal reloads the whole list.
- [x] `PayKindNow` replaces `KindKey`: Renewal pays as `renewal` for an active member, else `membership`. Used for items, cash, Xendit and the discount check. Renewal lists no items until a member is picked; a status re-read that flips active reloads them.
- [x] `Visible`/`IneligibleReason`: Renewal hides only an active Day pass ("already has a Day pass until midnight").
- [x] No-match text: "New here? Register them under Membership."
- [x] Tests: `Membership_registers_while_Renewal_and_Day_pass_search`, `Register_a_new_member_and_they_are_chosen`, `Renewal_lists_accounts_and_a_member_with_no_membership_buys_a_package`; Renewal filter test updated. 1245 pass; Windows app builds.
- [ ] Owner hand check on Windows: Membership, Renewal (lapsed and active member), Day pass.

Ponytail review: `ShowHits`, `ShowNoMatch` and `ShowSuggestions` are now plain aliases; can be folded into `HasHits`, `NoMatch` and `HasSuggestions` in a later cleanup.

## Fix 2: Day pass cards and amenity booking customer

Owner, 2026-11-25 (screenshot of Membership's "Choose one" with Daypass and Daypass (With coach) cards): remove the Day pass from Membership when the desk registers a new member. In Amenity booking, once Book for a walk-in is pressed the member search must not show, and while the member search shows, the walk-in button must not show.

Built:

- [x] `CashierViewModel.RefreshItemsAsync` drops Day pass packages from the cards (Membership, and Renewal for a member with no membership); they sell on the Day pass tab only.
- [x] Amenity booking: two chips, Member and Walk-in (`ChoiceChip`), in place of the Book for a walk-in button. Member shows only the search; Walk-in shows only the name box. New `DeskBookingViewModel.StartMember`.
- [x] No-match text: "No account? Choose Walk-in."
- [x] Tests: `Membership_lists_no_Day_pass_packages`, `Member_and_walk_in_are_one_at_a_time`; `It_opens_on_memberships_as_cards_with_prices` now leaves out Day passes. 1247 pass; Windows app builds.
- [ ] Owner hand check on Windows.

## Fix 3: Staff scheduling (coaches and front desk)

Owner, 2026-11-25: can the coaches' shifts and payroll work like `C:\Users\bowie\Downloads\gym_workforce_hierarchy_and_shift_scheduling.pdf` (sections 3-7: weekly templates, assignments with an effective date, overrides, a 5-minute clock-in window, review, payroll from approved hours)? The PDF's overall system hierarchy (sections 1-2) is left out by the owner.

**Goal:** The admin sets every coach's and front desk person's working shifts (weekly templates, assignments from a date, one-day or date-range overrides). Coaches no longer post their own shifts. Check-in is refused with no shift that day or more than 5 minutes before the shift start (server time); a late check-in is allowed and flagged; late check-out is allowed. Payroll pays approved hours counted from the later of check-in and shift start.

**Architecture:** Reuse, don't replace.
- `shift_posts` stays as the coach's on-the-floor hours that booking, free-coach lists, gym closures and notifications already read (0028, 0031, 0064, 0065, 0120, 0126, 0135). The schedule *writes* those rows; coaches lose insert/update on them. No booking code changes.
- Clock-in: coaches `coach_check_in` (0032); front desk `open_cash_shift` (0050). Both get the shift rule.
- Review and pay: the existing per-record approve/reject, flags, hour corrections and paid freeze of hourly payroll (0142-0146).

**Tech Stack:** Supabase (Postgres migrations, pg_cron), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests. Migrations from `0151`.

### Fix 3 decisions

| # | Decision | Status |
|---|---|---|
| S1 | **Who is scheduled:** coaches and front desk (role `employee`). Admins and System admins are not scheduled and are not limited when they open a drawer. | Owner, confirmed |
| S2 | **Coaches cannot post shifts.** The admin's schedule makes them. Coach "Post a shift" goes; coaches still see their own shifts. | Owner, confirmed |
| S3 | **Clock-in window:** refused before shift start minus 5 minutes (`too_early`, "Your shift starts at 8:00 AM. Check in from 7:55 AM."); allowed from then on; after the start it is flagged **Late**. Server time (Manila). 7:54 refused; 7:55, 7:58, 8:00 allowed; 8:01 allowed and Late. | Owner, confirmed |
| S4 | **No shift that day: check-in refused** (`no_shift_today`), for coaches and front desk. A rest day or a rest override counts as no shift. | Owner, confirmed |
| S5 | **Late check-out allowed.** Check-out stays as today; open coach visits still auto-close at gym closing (0032). | Owner, confirmed |
| S6 | **Paid time starts at the later of check-in and shift start.** The real check-in time is kept; only payable hours are clipped. | Owner, confirmed |
| S7 | **Leaving before the shift end is flagged, not refused:** **Left early**, still paid for time worked once approved. | Owner, confirmed (recommended) |
| S8 | **Schedule changes reach the future only** (amended by option b in 3.6: a change starting today also rewrites today): a change rewrites the coach's scheduled `shift_posts` from tomorrow on, only rows with no booked session inside them; a shift with bookings is kept and reported to the admin. Past and today's rows are never touched. A daily job keeps 8 weeks of coach shifts filled ahead. | Owner, confirmed (recommended) |
| S9 | **Booking lists stay shift-based** (no same-day "checked in" filter). If a coach doesn't come, the front desk or an admin **moves the booked session to another coach** who is free that hour (has a shift, not booked, under the daily hour limit). The time, price and payment stay; the member and both coaches are notified; audited (`session.reassigned`). Per-session pay goes to the coach who completes it. | Owner, 2026-11-25 ("they can be assigned to other coach"); details recommended |

### Fix 3 skills

- `ponytail` full mode (reuse `shift_posts`, payroll review and freeze; smallest diff).
- `superpowers:test-driven-development` for every rule; the S3 clock-in table is the first failing test.
- `impeccable` and `frontend-design` for the Scheduling page and the coach/desk screen changes (staff pages Windows only, light and dark; coach screens Android and Windows, 1.3 font scale).
- `superpowers:systematic-debugging` if a check-in or payroll number comes out wrong.
- `ponytail:ponytail-review` on each phase's diff before its commit.
- `superpowers:verification-before-completion` before any "done"; `superpowers:requesting-code-review` for a whole-fix review at the end.
- Push each phase's migrations live when it is finished (dry run first), then check live grants (new functions are open to `anon` by default here; revoke explicitly).

### Fix 3 phases

| Phase | What | Status |
|---|---|---|
| 3.1 | Database: templates, assignments, overrides, `shift_for`, coach shift sync, clock-in rule (off by default) | DEPLOYED (`0151` live 2026-11-25, rule off) |
| 3.2 | Payroll: Late / Left early flags, pay from shift start | DEPLOYED (`0152` live 2026-11-25) |
| 3.3 | Scheduling page (Templates, Assignments, Calendar) | BUILT (`0153` live 2026-11-25; owner hand check left) |
| 3.4 | Coach and desk screens: no Post a shift, refusal sentences, shift and Late on the desk | BUILT (`0154` live 2026-11-25; owner hand check left) |
| 3.5 | Move a booked session to another coach (S9) | BUILT (`0155` live 2026-11-25; owner hand check left) |
| 3.6 | Checks and rollout (admin sets schedules, then the rule is switched on) | CHECKS DONE (`0156`, `0157` live); rollout and release wait on the owner; **not merged** |

#### Phase 3.1: Database (`0151_staff_scheduling.sql`)

- [x] Tables (writes through `security definer` functions only; read for staff with `reports:revenue`, and each person their own):
  - `schedule_templates (template_id, name unique, archived_at, created_at)`.
  - `schedule_template_days (template_id, weekday 1-7 (ISO, Monday 1), starts_at time, ends_at time)`, key `(template_id, weekday)`; no row = rest day; `ends_at > starts_at` (no overnight shifts).
  - `schedule_assignments (assignment_id, user_id, template_id, effective_from date, created_by, created_at)`; active one for a date = latest `effective_from <= date`. Never edited or deleted, so history stays.
  - `schedule_overrides (override_id, user_id, from_date, to_date, starts_at time null, ends_at time null, reason 1-200, created_by, created_at)`; null times = rest; a later override for the same day wins.
- [x] `shift_for(p_user, p_day) returns (starts_at timestamptz, ends_at timestamptz)`: override, else the active assignment's weekday row, else nothing. Manila times.
- [x] Shifts must sit inside gym hours and not on a gym closure day (0065): `outside_gym_hours` when saving a template day or an override.
- [x] Admin functions (`reports:revenue`, audited): `schedule_template_save`, `schedule_template_duplicate`, `schedule_template_archive` (refused while assigned from today on: `template_in_use`), `schedule_assign` (coaches and front desk only, not in the past), `schedule_override_add`, `schedule_override_remove` (future only).
- [x] `schedule_sync_coach(p_user, p_from, p_to)` writes `shift_posts` per S8; called by the admin functions for coaches; `cron.schedule('fill-coach-shifts', daily)` fills 8 weeks ahead.
- [x] Coaches lose shift posting: revoke insert/update on `shift_posts` from `authenticated`, drop the coach insert policy; guards 0028, 0033, 0065 stay for the sync.
- [x] `coach_visits` and `cash_shifts` get `expected_start`, `expected_end` (null), filled at clock-in from `shift_for`, so later template edits never change what a past day expected.
- [x] `gym_settings.schedule_enforced boolean default false`. When true: `coach_check_in` refuses `no_shift_today` / `too_early` and stores the expected times; `open_cash_shift` does the same for role `employee` only (S1). When false, expected times are still stored when a shift exists.
- [x] Database tests: the S3 table, no shift, rest override, override beats template, assignment history, admin not limited, enforcement off lets everyone in, sync keeps booked shifts, `anon` cannot call any new function.
- Built notes: the admin functions return the kept days (`schedule_assign`, `schedule_override_add`/`_remove` return a JSON list of dates; `schedule_template_save` returns `{template_id, kept}`); a too-early refusal reads `too_early:8:00 AM`; `fill_coach_shifts` runs 00:10 Manila; the "Your coach posted a shift" notice trigger is dropped. Local test harness now reads migrations with LF line endings (a Windows checkout broke 0148's text match). Tests: `supabase/tests/local/staff-scheduling.mjs` (17).
- Deployed 2026-11-25: dry run showed only `0151`; pushed; live grants checked (no new function for `anon`; helpers closed to the app; no insert on the schedule tables or `shift_posts`; cron `fill-coach-shifts` registered). Old tests that had coaches post shifts now write them as the database owner (the guards still run) and expect the app to be refused: `coach-availability`, `crud-rules`, `rules`, `gym-closures`. All 70 database test files pass. **Until Phase 3.4 ships, a coach on the current app who taps Post a shift gets a permission error.**

#### Phase 3.2: Payroll

- [x] New flags in `payroll_records_for`: **Late** (in after expected start), **Left early** (out before expected end, S7), next to **Auto closed**. Flagged records still need one-by-one approval (Approve all skips them).
- [x] Payable hours = `greatest(check-in, expected_start)` to check-out (S6), for coach visits and front desk cash shifts; records without an expected start count as today.
- [x] Drawer row: "Shift 8:00 AM–5:00 PM · in 8:07 AM (Late) · out 5:30 PM".
- [x] Tests: early check-in pays from start; late check-in pays from check-in; late check-out pays to check-out; paid freeze unchanged.
- Built notes: `payroll_records` (and `payroll_records_for`) now return `starts_at` as the payable start (`least(expected_start, ends_at)` when in early, so in-and-out before the shift counts 0 h) plus `actual_start`, `expected_start`, `expected_end`; every sum already read `starts_at`, so totals, overlap and Approve all needed no change. Drawer row: "Mon 3 Aug · shift 8:00 AM – 5:00 PM · in 8:07 AM → 5:30 PM · 9.38 h"; flags read Late / Left early. Audit labels added for the `schedule.*` actions from 0151 (missed in 3.1; caught by `AuditLogTests`). Demo payroll has no shifts, so it only carries the new fields. Tests: `supabase/tests/local/payroll-schedule.mjs` (7), `A_scheduled_record_shows_shift_real_check_in_and_its_flags`; payroll db tests and 1248 C# tests pass; Windows app builds.

#### Phase 3.3: Scheduling page (System admin and Admin, Windows only)

- [x] New staff page **Scheduling** next to Payroll, permission `reports:revenue`.
  - **Templates:** list with Duplicate and Archive; editor is a Mon-Sun week with Start, End and a Rest day switch per row.
  - **Assignments:** pick a coach or front desk person, a template and an Effective from date; current template and past assignments.
  - **Calendar:** a week at a time, one row per person, each day the shift, "Rest" or the override with its reason; Add override (one day or a range, times or rest, reason).
- [x] After a change that keeps booked coach shifts (S8), the notice names how many and which days.
- [x] **Turn on shift check-in** switch (`schedule_enforced`), with a confirm that lists who has no schedule yet.
- Built notes: tabs are **Week** (grid per person, a change outlined with its reason; "Change a day" form; Coming changes with Remove), **Templates** (Mon-Sun editor on the half-hour grid inside gym hours; Edit, Duplicate as "(copy)", Archive) and **People** (current and next template, Change: template + from date). The check-in switch sits above the tabs on every tab; turning it on names everyone with no schedule. New `schedule_set_enforced` (`0153`, `reports:revenue`, audited) because gym settings are the System admin's and a sub-admin needs the switch; `GymSettings.ScheduleEnforced` is never written by a Gym settings save. `ScheduleService` + `ScheduleData.ShiftFor` (same rule as `shift_for`, shared by the page and the demo); demo emulates all schedule functions and the coach shift sync. Admin overview gets a Scheduling link; icon `calendar-clock`. Tests: `SchedulingViewModelTests` (5), db `staff-scheduling.mjs` (18); 1254 C# tests pass; Windows app builds. Not opened in the app here (owner checks it).

#### Phase 3.4: Coach and desk screens

- [x] Coach app: remove Post a shift; My shifts shows the admin-made shifts read-only ("Set by the gym").
- [x] Coach self check-in, desk coach list and Cashier Start shift show the refusals (`ErrorText`): `no_shift_today` → "No shift today. Ask the admin to add one."; `too_early` → "Your shift starts at 8:00 AM. Check in from 7:55 AM."
- [x] Desk coach list shows each coach's shift today and a Late tag.
- Built notes: coaches' Post a shift form (My shifts side panel) and Cancel shift buttons (My shifts table and coach calendar) are gone, with `PostShiftAsync`/`CancelShiftAsync`, the calendar's post fields and `ShiftSlot`; My shifts says "The gym sets your shifts." Refusals: `no_shift_today` → "No shift today. Ask the admin to add one on the Scheduling page."; `too_early:8:00 AM` → "The shift starts at 8:00 AM. Check in from 7:55 AM." New `coach_shifts_today()` (`0154`, `check_ins:record`): each coach's shift today and Late (server time: in after the start, or not in while it runs), because the desk can't read the schedule tables and coach shift_posts start tomorrow. Check-in page coach rows show the shift and a Late chip; Home's in-gym list shows Late. Demo: same check-in rule for coaches and front desk drawers, `coach_shifts_today`, coach shift writes refused; dead demo `GuardShift` and the "Your coach posted a shift" notice removed; tests set coach shifts with `DemoGateway.OwnerAddCoachShift`. Tests: `ShiftCheckInTests` (6), db `staff-scheduling.mjs` (19); 1252 C# tests pass; Windows and Android build.

#### Phase 3.5: Move a booked session to another coach (S9)

- [x] `reassign_session(p_session, p_coach, p_reason)` (front desk and admin, `payments:record`): the session is scheduled and not started; the new coach has a scheduled shift covering it, no other session overlapping, and stays under `max_coach_hours_per_day` (same checks as `book_coach_session`, 0120); member's coach access rules unchanged. Changes only `coach_id`; time, price and payment stay. Notifies the member and both coaches; audit `session.reassigned` (old coach, new coach, reason). Revoke from `anon`.
- [x] Database tests: free coach accepted; coach without a shift, already booked, or over the daily limit refused (`coach_not_free`); started or cancelled session refused; `anon` refused.
- [x] Desk screen (where booked coach sessions are listed): **Change coach** on a session opens the coaches free at that hour (reuses `free_coaches`), with a reason; Windows only.
- [x] Member and coach apps show the new coach on the session (already read from the session row); notification text names the new coach.
- Built notes: `reassign_session` (`0155`) refuses a started session with its own `session_started` ("This session has already started, so it stays with its coach."); the desk acts before the start, when the coach shows Late. **Change coach** sits on Home's "Today's coach sessions" (row and card) while the coach hasn't arrived; the panel offers only coaches free for every hour of the session (`free_coaches`), asks why, and says who was told. Demo emulates it. Tests: db `reassign-session.mjs` (4), `ReassignSessionTests` (2); 1254 C# tests pass; Windows and Android build.

#### Phase 3.6: Checks and rollout

- [x] Whole-fix review (3.1-3.5, `27ea8c8^..fd6c5e1`). Found and fixed: the schedule could replace a coach shift that had an **amenity booking with that coach** (only coach sessions kept a shift); `0156` keeps it and reports the day, demo the same; db test added. Raised to the owner, not changed (decision): see "Open decision" below.
- [x] All unit and database tests; Windows and Android builds. Full db run: 72 files; the only failure was `rules.mjs` (no break test for three schedule functions), fixed and rerun (106 passed); `staff-scheduling.mjs` 22 passed; 1254 C# tests pass; Windows builds (Android built in 3.5, no Android change since).
- [x] Owner screenshot (Templates, New template): the per-day "Works" label ran under the time pickers and over "Rest day" (a Windows CheckBox is ~120 wide). Fixed: one caption row (Day, Works, Starts, Ends) and the checkbox alone in a 120-wide column.
- [x] Owner screenshot 2 (Edit template wasted the width) and "just table form": the editor is a table with the days across (Mon-Sun columns; Works, Starts, Ends rows; "—" on a day off), 7 equal cells a row like the Week grid; Name, Save and Cancel on one line above. A Works / Day off chip (`ChoiceChip`) replaces the checkbox.
- [ ] Rollout (owner): see the steps below. `0151`-`0157` are live with `schedule_enforced` off, so nothing changes until step 4.
- [ ] Coaches' own future shifts stay until the first sync for that coach replaces the unbooked ones (S8). Nothing to do; it happens at step 3.
- [ ] New app release after the owner merges (old apps still show Post a shift; the server refuses it). **Not merged: owner said to wait.**

**Rollout steps for the owner (in this order):**

1. Install the new app on the admin PC (after the merge and release).
2. **Scheduling → Templates:** make one template per usual week (e.g. "Weekdays 9-5", "Weekend mornings"). Times are on the half hour inside gym hours.
3. **Scheduling → People:** give every coach and front desk person a template from today. Coaches' shifts for the next 8 weeks are written from tomorrow; any day kept because something is booked is named in the notice.
4. Check **Week** for the coming days, add one-off changes (Change a day) where needed, then press **Turn on** at the top. The confirm names anyone still without a schedule; they can't check in or open a drawer until they have one.
5. From then on: the desk checks coaches in from 5 minutes before their shift; Payroll shows Late / Left early to approve; a coach who doesn't come gets **Change coach** on Home before the session starts.
6. To undo quickly: **Turn off** on Scheduling. Everyone can check in again; schedules stay.

**Decided (owner, 2026-11-25): option (b).** A change that starts today (an assignment from today, a change for today) also rewrites today's coach shift, so a coach given today off can't be booked later today; today's new shift starts no earlier than the next half hour; a shift with something booked in it is kept and named; a shift already over stays as history. Template edits and the nightly fill still start tomorrow. `0157` (live), demo the same; db test added. This amends S8.

**Release notes (for the next version):** the admin sets coach and front desk shifts on the new Scheduling page (templates, people, week, one-off changes); check-in and opening a drawer follow the shift once turned on; Payroll pays from the shift start and flags Late / Left early; coaches no longer post shifts; the desk sees each coach's shift and Late, and can move a session to another free coach.
