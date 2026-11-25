# Owner fixes 2026-11-25 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-25
Last updated: 2026-11-25
Status: Fixes 1-2 BUILT and pushed (app only, no migration). Fix 3 (staff scheduling): Phases 3.1-3.6 built, checked and pushed; `0151`-`0157` all live (dry run 2026-11-25: remote up to date); shift check-in rule is **off** until the admin turns it on from Scheduling. S1-S9 confirmed; S8 amended by option (b). Left: owner hand checks, the rollout steps (Phase 3.6), merge and a new app release. **Owner: do not merge until told.** Fix 4 (admin menu with words, Attendance page, Payslips tab, corrections on Attendance) BUILT, `0158` live. Fix 5 (Expenses, Financial reports) BUILT, `0159` live. Layout fixes after Fix 5 (Amenity status margins, compact menu) BUILT. Withholding tax on the month (`0160` live: BIR monthly table, nothing up to ₱20,833, taken in the 16th–end pay) BUILT. All pushed; owner hand checks left. More fixes get added below as the owner sends them.
Branch: `owner-fixes-2026-11-25` (cut from `main` at `df3f54c`, v0.11.0). Owner merges to `main` when done; no push or PR without the owner.
Next migration: `0161`.

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
| 3 | Staff scheduling: admin sets coach and front desk shifts; check-in follows the shift; payroll pays from shift start; desk moves a session to another coach (6 phases) | BUILT (`0151`-`0157` live, rule off; not merged) |
| 4 | Admin menu with words (Workforce Management, Finance & Payroll); Attendance Review page with whose record it is and the money before approving (4 phases) | BUILT (`0158` live; owner hand check left; not merged) |
| 5 | Expenses (add, void with a reason) and Financial reports (month or year: revenue, expenses, payroll, net income, by method, by category, month by month) | BUILT (`0159` live; owner hand check left; not merged) |

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
- [x] Owner screenshot 3 (People tab): long names and the role chip ran into the Schedule column. The Person column is wider (260 → 340) with more gap (24), header and rows alike.
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

### Fix 3 commits (branch `owner-fixes-2026-11-25`)

| Commit | What | Migration |
|---|---|---|
| `c0281e5`, `56c02b4`, `634ffa3` | Plan: Fix 3 decisions S1-S9, phases | — |
| `27ea8c8` | 3.1 templates, assignments, overrides, `shift_for`, coach shift sync, clock-in rule (off); local db harness reads LF | `0151` |
| `b654931` | 3.2 payroll pays from the later of check-in and shift start; Late / Left early | `0152` |
| `f355007` | 3.3 Scheduling page (Week, Templates, People, check-in switch) | `0153` |
| `830c340` | 3.4 coaches stop posting shifts; refusals in words; desk sees shift and Late | `0154` |
| `fd6c5e1` | 3.5 desk moves a booked session to another free coach | `0155` |
| `11968c3` | 3.6 review: keep shifts with coach amenity bookings; option (b) a change starting today rewrites today; editor layout fix | `0156`, `0157` |
| `63dd54a`, `0d27a2f` | Template editor: day tiles, then a table with the days across (owner) | — |
| `a27c8c0` | People tab: wider Person column (owner) | — |

All migrations are live; all commits are on GitHub (pushed 2026-11-25 up to `b789bd6`).

## Fix 4: Admin menu with words and Attendance Review

Owner, 2026-11-25 (three screenshots): a navigation tree from the workforce PDF (section 2) with Workforce Management (Employees, Scheduling, Attendance) and Payroll; "use this hierarchy" (Workforce Management: Employees; Attendance: Attendance Review with a Pending tag, Attendance Records, Attendance Corrections. Finance & Payroll: Payroll: Salary Configuration, Payroll Processing, Payslips, Payroll History); an attendance card "Juan Dela Cruz · Front Desk · Pending · November 25, 2026 · Time-in 8:00 AM · Time-out 5:00 PM" with "Tell what person is that"; and "make the owner understand the consequences": before approving, a table of Recorded hours 4.26, Approved hours 0, Hourly rate ₱130, Potential payable amount ₱553.80. For the System admin and Admin (Windows only).

**Goal:** The admin's menu is grouped the way the work is done, every attendance record says whose it is, and approving one shows the money first.

**Architecture:** Reuse, don't replace.
- Records, approve/reject, flags, corrections and the paid freeze are hourly payroll's (0142-0146, 0152): `payroll_records`, `payroll_review_time`, `payroll_hour_adjustments`. The new page only lists across people; one new read function, no new writes.
- Payroll's three tabs stay one page; the menu's Payroll entries open it on a tab.
- The menu change is in `IconRail.Windows.cs` for the admin roles only (`groupFrontDesk: true` in `StaffShell.xaml.cs`); front desk, coach and member keep the icon strip.

**Tech Stack:** Supabase (Postgres), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests. Migration `0158`.

### Fix 4 decisions

| # | Decision | Status |
|---|---|---|
| W1 | **Menu with words (option A)**, about 240 wide, for System admin and Admin only, Windows only. | Owner, 2026-11-25 |
| W2 | **Attendance Review is its own page (option 1):** every pending record across all staff for the chosen pay period, as the owner's card; Approve opens the money table, then Confirm. The Payroll drawer keeps its own Approve / Reject on the same records. | Owner, 2026-11-25 |
| W3 | Menu tree (owner's picture, the rest of today's pages placed by me): **Overview**: Overview, Front desk (folds Home, Cashier, Membership checking, Amenity status). **Workforce Management**: Employees, Scheduling, Attendance (Attendance Review with Pending count, Attendance Records, Attendance Corrections). **Finance & Payroll**: Payroll (Salary Configuration = Salaries tab, Payroll Processing = Pay period tab, Payslips = Pay period tab with its PDF, Payroll History = Paid out tab), Revenue, Refunds, Discounts. **Gym**: Users and roles, Packages and amenities, Announcements, Damage reports, Gym settings, Logs & Archive. Bottom: Messages, Settings, Sign out and the picture. A System admin sees only the pages their role has today. | Owner approved 2026-11-25 |
| W4 | **Money table** rows: Recorded hours (the record's hours), Approved hours (what this record counts now: its hours if approved, else 0), Rate, Potential payable amount. Rate by the person's pay type: hourly → "₱130 an hour", amount = hours × rate (4.26 × 130 = ₱553.80); per session → the session rate, amount = rate; monthly → "Monthly salary · approving counts hours only", no amount row. Amounts are before deductions (said under the table). | My call (owner may correct) |
| W5 | **Employees** opens Users and roles filtered to coaches and front desk (a `Staff only` filter on that page); no new employee page. | My call (owner may correct) |
| W6 | **Attendance Records** = the same list with every status and a status filter (All, Pending, Approved, Rejected). **Attendance Corrections** = the period's hour corrections (person, hours, note, who, when), read-only; new corrections stay in the Payroll drawer. | My call |
| W7 | A paid-out period is read-only on all three (no Approve / Reject), as in the drawer. Open records (still checked in) show "Still open" and can't be approved. | Same as today |

### Fix 4 skills

- `ponytail` full; `superpowers:test-driven-development` (money table numbers and the record list first); `impeccable` and `frontend-design` for the menu and the page (staff pages Windows only, light and dark); `ponytail:ponytail-review` on each phase; `superpowers:verification-before-completion`.
- Push `0158` live when 4.1 is finished (dry run first), then check live grants (new functions are open to `anon` by default here; revoke explicitly).

### Fix 4 phases

| Phase | What | Status |
|---|---|---|
| 4.1 | Database: `payroll_records_all` | DEPLOYED (`0158` live 2026-11-25) |
| 4.2 | Attendance Review page (Review, Records, Corrections) with the money table | BUILT (app only; owner hand check left) |
| 4.3 | Menu with words for the admin roles | BUILT (app only; owner hand check left) |
| 4.4 | Checks | DONE bar owner hand check (review fixed `a24cdda`) |

#### Phase 4.1: Database (`0158_payroll_records_all.sql`)

- [x] Failing db test `supabase/tests/local/payroll-records-all.mjs`: two staff (one hourly coach, one front desk) with records in one period → one call returns both people's records, each with `user_id`, `full_name`, `role_name`, `pay_type`, `rate` and the same fields as `payroll_records_for` (`source`, `source_id`, `starts_at`, `actual_start`, `expected_start`, `expected_end`, `ends_at`, `hours`, `status`, `reason`, `flags`); a sub-admin without `reports:revenue`, a coach and `anon` are refused; a member is never listed.
- [x] `payroll_records_all(p_period bigint) returns jsonb` = `{records: [...], adjustments: [...]}` (adjustments: the period's hour corrections for everyone, with `full_name`, as `PayrollAdjustmentsAsync` returns per person), `security definer`, checks `reports:revenue`, built on `payroll_records(p_period)` joined to profiles and the period's pay basis; no new write. Test also asserts `adjustments` holds both people's corrections. `revoke all ... from public, anon; grant execute ... to authenticated`.
- [x] Run `node supabase/tests/local/payroll-records-all.mjs` and the other `payroll*.mjs` files → pass.
- [x] Demo: `DemoPayroll` answers `payroll_records_all` from its existing records.
- [x] Commit; dry run → only `0158`; push; check live grants (no `anon` execute).
- Built notes (`b1e9cb5`): records are ordered by name, then start; `role_name` is payroll's role (`coach` or `employee`). Moved forward from 4.2 so the demo has a check: models `PayrollStaffRecord` (inherits `PayrollTimeRecord`, now unsealed), `PayrollAttendance`, optional `UserId`/`FullName` on `PayrollHourAdjustment`, and `PayrollRecordsAllAsync` on the admin service; C# test `Records_all_lists_every_person_with_name_role_rate_and_corrections`. Tests: db `payroll-records-all.mjs` (4), all `payroll*.mjs` and `rules.mjs` (106) pass; 1255 C# tests pass. Deployed 2026-11-25: dry run showed only `0158`; pushed; live: `anon` and `public` can't execute, `authenticated` can (checks `reports:revenue` inside).

#### Phase 4.2: Attendance Review page

Files: create `src/GymMembership.Core/ViewModels/AttendanceReviewViewModel.cs`, `src/GymMembership.App/Views/AttendanceReviewPage.xaml(.cs)`, `tests/GymMembership.Tests/AttendanceReviewViewModelTests.cs`; modify `Services.cs` (service + interface default), `StaffShell.xaml` (new `FlyoutItem Title="Attendance" ClassId="admin" Route="attendance"`), `IconRail.Windows.cs` `PageIcons` (`["Attendance"] = "clipboard-check"`), DI registration. (`AttendanceViewModel` already exists for members; don't reuse the name.)

- [x] Failing tests in `AttendanceReviewViewModelTests`:
  - `Review_lists_pending_records_of_every_person_with_name_and_role` (demo: two people → both cards, each with `FullName`, `RoleText` "Front Desk"/"Coach", date "November 25, 2026", `InText` "8:00 AM", `OutText` "5:00 PM", `StatusLabel` "Pending").
  - `Approve_shows_the_money_first_then_confirm_approves` (hourly ₱130, 4.26 h → rows "4.26 hours", "0 hours", "₱130", "₱553.80"; nothing approved until `ConfirmApprove`; after it the record leaves Review and Pending count drops by one).
  - `Monthly_salary_shows_no_amount` and `Per_session_amount_is_the_session_rate`.
  - `Records_filter_by_status` and `Corrections_list_the_periods_hour_corrections`.
  - `Paid_out_period_is_read_only`.
- [x] `IStaffService.PayrollRecordsAllAsync(int periodId) → AppResult<PayrollAttendance>` where `PayrollAttendance(IReadOnlyList<PayrollStaffRecord> Records, IReadOnlyList<PayrollHourAdjustment> Adjustments)` and `PayrollStaffRecord` is `PayrollTimeRecord` plus `UserId`, `FullName`, `RoleName`, `PayType`, `Rate` (one call, 0158).
- [x] `AttendanceReviewViewModel`: period picker (reuses `PayrollPeriodAsync`), tabs `Review` / `Records` / `Corrections` (`Tab` set by the menu via route query `?tab=review|records|corrections`), `PendingCount`, items with `ApproveCommand` → opens the money panel (`MoneyRows`, `ConfirmApproveCommand`, `CancelCommand`), `Reject` with reason (reuse `PayrollRecordItem`'s reject wording). Approve/Reject call `ReviewPayrollTimeAsync`; refusals through `ErrorText`.
- [x] Page: cards as the owner's picture (initials, name, role, Pending chip top right; date; Time-in / Time-out; shift and Late / Left early line under it), a table on wide windows with the same columns. Money panel as the owner's table, with "Before deductions." under it. `impeccable` pass, light and dark.
- [x] Run `dotnet test` → all pass; Windows build.
- [x] Commit.
- Built notes: page `Attendance` (route `attendance`, `ClassId="admin"` like Payroll, icon `clipboard-check`) after Scheduling; Admin overview gets an Attendance link (the shell tests require one per admin page). Tabs Review (with the pending count in its name) / Records (status picker) / Corrections (table); the period bar as on Payroll. Cards as the owner's picture: initials, name, role, status chip; date and what it is (Check-in, Cash shift #, Session · member); Time-in / Time-out; the shift; Late / Left early. Approve opens a side panel like Payroll's pay details: "Approve for <name>?", the money table, "Before deductions…", Cancel / Approve. **W4 changed:** fixed-salary staff have no attendance records (`payroll_records` lists hourly and per-session pay only), so there is no monthly row; per session reads Recorded "1 session", Approved, Rate "₱150.00 per session", amount = rate. Review focus 1 (no rate set) can't happen for the same reason. Flag words shared with the drawer (`PayrollRecordItem.FlagWords`). The `?tab=` route query is left to 4.3 (the menu is its only user). Tests: `AttendanceReviewViewModelTests` (8); 1264 C# tests pass; Windows builds. Not opened in the app here (owner checks it).

#### Phase 4.3: Menu with words

- [x] Failing test in `IconRailTests`: every admin page title sits in exactly one W3 group (new `IconRail.MenuGroups` map: section → entries), and `Attendance` and `Payroll` have their W3 sub-entries with the route and tab each opens.
- [x] `IconRail.Install(..., groupFrontDesk: true)` builds the labeled menu (width 240, section captions, icon + name per row, sub-entries indented, Front desk and Attendance / Payroll fold open like today's briefcase, current page highlighted, sub-entry of the current tab highlighted); `false` keeps the icon strip unchanged. Keep each button's `AutomationId = "AdminNavigation-" + route` (scripts/Test-WindowsAdminNavigation.ps1).
- [x] Payroll sub-entries open Payroll on its tab (`PayrollViewModel` reads a `tab` query: `salaries`, `period`, `paidout`; Payslips = `period` and scrolls to the PDF button).
- [x] Pending chip on Attendance Review: `PendingCount` for the current period, refreshed on navigation and after an approve/reject; hidden at 0.
- [x] Employees: `UsersViewModel` gets a `StaffOnly` filter (coaches and front desk); the menu's Employees opens Users and roles with it on (route query `?staff=1`). **Users and roles** stays its own entry under Gym, unfiltered.
- [x] `impeccable` pass on the menu, light and dark, short windows (the middle scrolls).
- [x] Run `dotnet test`; Windows build; commit.
- Built notes: the tree is data in Core (`AdminMenu.Sections`, `AdminMenuEntry`; tested against `StaffShell.xaml`), drawn by `IconRail.InstallMenu` (240 wide, same yellow / near-black strip; section captions; Front desk, Attendance and Payroll fold and start closed; the current page on a black (light) or yellow (dark) bar; a fold with a hidden current page is lit). Coach, member and front desk keep the icon strip (`IconRail.Install`; the old briefcase group code is gone). A child entry sets its page's tab through `IOpensTab` (Payroll, Attendance, Users and roles). **Payslips** opens Pay period (the PDF button is on it); no scroll to it. Pending tag: `StatusChip` "Pending" on Attendance Review, from this pay period's pending count at sign-in (`PayrollPeriodAsync`, which opens the period as Payroll does), then from Attendance (`AttendancePendingChanged`). Employees: `UsersViewModel.StaffOnly` + "Showing coaches and front desk only. Show everyone". UI script ids kept (`AdminFrontDeskGroup` expanded/collapsed, `AdminNavigation-<route>` current page); tabbed entries are `AdminNavigation-<route>-<tab>`. Tests: `AdminMenuTests` (4), `IconRailTests` and `WindowsAdminNavigationTests` updated; 1268 C# tests pass; Windows and Android build. Not opened in the app here (owner checks it).

#### Phase 4.4: Checks

- [x] `ponytail:ponytail-review` and a whole-fix review (`superpowers:requesting-code-review`, `7492cc5..6486384`).
- [x] All unit and database tests; Windows build (Android: no change, menu is Windows only). 1269 C# tests pass; Windows and Android build. Database: 84 files, 80 pass; the 4 that fail (`coach-hired-tag`, `hire-requests`, `session-requests`, `structured-requests`) call `hire_coach`, which `0121` (end of hiring, 2026-11-21) dropped; they were already broken before Fix 4. Owner, 2026-11-25: deleted (hiring is gone). 80 database test files remain, all passing.
- [ ] Owner hand check on Windows: menu (System admin and Admin), Attendance Review approve with the money table, Records, Corrections, each Payroll entry opening its tab.
- Review result: no critical; fixed: **(1)** an overlapping record's money table now ends with "Overlaps another record · The shared time is paid once, so the period total is lower" (pay merges overlaps, 0142), test `Overlapping_record_says_the_shared_time_is_paid_once` (failed first); **(2)** the menu's tab is applied when the shell has shown that page (`Navigated`, or at once when already on it), not one dispatcher tick after navigating, so a first visit opens on the right tab. Also fixed: a failed read clears the cards (no last period's cards under a new name); the Pending tag only follows Attendance when it shows this pay period; a page opened from Overview or Home lights its first menu entry; Payroll's tab switch from the menu no longer loads twice; long names on cards cut off inside the card; `Route="payroll"` and `Route="users"` so the tabbed entries have stable ids (`AdminNavigation-payroll-salaries`…). Kept as is: the hourly rate reads "₱130.00" (as the owner's picture, not W4's "an hour"); records stay cards on wide windows too (the owner's card; no table); the Pending tag's sign-in read opens this pay period, as visiting Payroll does. Verified by the reviewer: Review focus 1 can't happen (`check (amount > 0)` on salaries, no salary = fixed = no records); 3 is safe (`payroll_review_time` upserts; Attendance reloads on appearing); 4: every sub-admin has `reports:revenue` (0036).

- Owner, 2026-11-25 (after 4.4): "make the records and review into table form": Review and Records are now a table (Person with initials and role, Date and what it is, Time-in, Time-out, Shift with Late / Left early, Status with the reject reason, Approve / Reject; Reject's reason opens under the row); a narrow window shows the card. `dd245a9`.
- Owner, 2026-11-25: "what's the difference of payslips and payroll process, it shows the same page" → option A: Payroll gets a **Payslips** tab (Pay period | Payslips | Paid out | Salaries): the period bar, one row a person (name, role, worked out from, net pay) and **Print payslip**, which opens a PDF of just that person's payslip (`PayrollPdf.PayslipDocument`, the same row and payslip block as the period PDF). The menu's Payslips opens it; Payroll Processing stays the Pay period tab (approve, pay, period PDF). Test `Payslips_tab_prints_one_persons_payslip`; 1270 C# tests pass; Windows builds.

- Owner, 2026-11-25 (screenshots of the drawer's Corrections box and a correction-request flow) → option A, "move the set correction into Attendance Corrections": Attendance → Corrections now has **Add correction** (person, Hours or Sessions + or −, Why) for people paid by the hour or the session, and **Remove** on each correction while the period is a draft; Payroll's pay details list the corrections read-only and say "Add or remove hour corrections on Attendance → Corrections." Same `payroll_add_hours` / `payroll_remove_hours`, no migration. Times, correction hours and money values no longer use the big numeral style (`5144827`). The request flow (employees ask, admin approves) is option B, not built; a later fix if the owner wants it. Tests: `AttendanceCorrectionTests` (2), `Hours_corrections_need_a_note_and_change_the_basis` now adds through the service; 1272 C# tests pass; Windows builds.

Review focus (cases no step names; test them in the phase that owns the code):
1. A person with no salary set yet → card shows "No rate set · set it in Salary Configuration", Approve still allowed, no amount (4.2).
2. Two records overlap (flag `overlap`) → Recorded hours shows what's counted, with the flag (4.2).
3. Approve from the Payroll drawer while Review is open → Review refreshes on return, no double approve (`payroll_review_time` already idempotent) (4.2).
4. A sub-admin without `reports:revenue` → no Workforce or Payroll entries (4.3).
5. Long names in the menu's page names and cards → truncate, no overlap (owner's People-tab screenshot) (4.2, 4.3).

## Fix 5: Expenses and Financial reports

Owner, 2026-11-25: "Expenses: tracks all money the gym spends to operate (salaries not already in payroll, electricity and water, rent, equipment purchases and repairs, cleaning supplies, internet, maintenance). Avoid recording payroll expenses twice; ideally approved payroll is reflected in financial expenses automatically. Financial Reports: total revenue, total expenses, net income, payroll expenses, revenue by payment method, monthly and yearly summaries." Owner: "implement this" (no questions; calls below are mine). System admin and Admin, Windows only.

**Architecture:** one new table and four functions (`0159`); the report is worked out in the app from the existing `revenue_summary` (verified payments minus approved refunds, by day and method), the new expenses and the approved payroll's gross pay, so Revenue and Financial reports always agree.

| # | Decision | Status |
|---|---|---|
| X1 | **Who:** `reports:revenue` (System admin and Admin), like Revenue and Payroll. | My call |
| X2 | **Categories:** Electricity, Water, Rent, Internet, Equipment, Repairs and maintenance, Cleaning supplies, Salaries (not in payroll), Other. | My call |
| X3 | **An expense:** day spent (today or before), category, amount (₱0.01–₱10,000,000), what it was for (1–200). No edits or deletes: a mistake is **voided** with a reason (kept and shown struck through, left out of totals). Adding and voiding are audited. | My call |
| X4 | **Payroll is never entered as an expense.** The report counts the **gross pay** (base pay + bonuses; government deductions are part of it) of every pay period that is **approved or paid out**, on the period's last day. A draft period isn't counted yet. | My call |
| X5 | **Financial reports:** a month or a whole year. Revenue, Expenses, Payroll and **Net income = Revenue − Expenses − Payroll**; revenue by payment method; expenses by category; for a year, a month-by-month table. | My call |
| X6 | **Menu:** Finance & Payroll gets **Expenses** and **Financial reports** after Revenue. | My call |

Phases: 5.1 database (`0159`: `expenses`, `expense_add`, `expense_void`, `expenses_list`, `payroll_cost`); 5.2 app (models, service, demo, `FinanceReport`, Expenses page, Financial reports page, menu); 5.3 checks.

Built 2026-11-25:
- [x] 5.1 `0159_expenses.sql`: table `expenses` (closed to the app; `crud-rules` pins `----`), `expense_add`, `expense_void` (`already_processed` twice, `not_found`), `expenses_list` (newest first, who added and who voided), `payroll_cost` (gross = salary + bonus lines of periods with `paid_at`, on `ends_on`). Audited `expense.added` / `expense.voided` (labels and the Expenses area added). Tests `expenses.mjs` (6). Deployed: dry run showed only `0159`; pushed; live: `anon` can't execute any of the four, nobody reads the table directly.
- [x] 5.2 App: `Expense`, `PayrollCostDay`; `AdminService` `ExpensesAsync`, `AddExpenseAsync`, `VoidExpenseAsync`, `PayrollCostAsync`; demo emulates all four; `FinanceReport` (Totals, Months, a loss reads "-₱500.00"); `ExpensesViewModel` and **Expenses** page (From/To with what was spent, Add expense form, table with Void and its reason, a voided row faded and struck through); `FinanceViewModel` and **Financial reports** page (Month | Year, ‹ ›, Revenue, Expenses, Payroll, Net income in red when a loss, how it is worked out, Revenue by payment method, Expenses by category, Month by month for a year, ten rows a page like every table). Menu: Finance & Payroll → Payroll, Revenue, **Expenses**, **Financial reports**, Refunds, Discounts; Overview links. Tests `FinanceTests` (3); shell and overview tests updated; 1277 C# tests pass; Windows and Android build.
- Also: the four deleted hiring tests had held the only break attempts for `taken_coaches` and `end_hire`, which the app still calls; `hire-leftovers.mjs` (2) puts them back (`rules.mjs` 106 pass).
- [ ] Owner hand check on Windows (Expenses: add, void; Financial reports: a month with approved payroll, a year).

## Fixes after Fix 5 (owner, 2026-11-25)

| Commit | What |
|---|---|
| `0ea954b` | Owner screenshot (Amenity status): the card sat against the new 240-wide menu and the Booked panel ran off the right edge. Cause: `SideLayout` (the two-column layout of Amenity status, Cashier, Refunds and some member pages) ignored its own `Padding`; only Amenity status sets one, so no other page moved. Fixed once in `SideLayout` (padding in Measure and Arrange; the wide/stacked switch still reads the full width); Amenity status now has 24 at the sides like the other staff pages. |
| `0507c11` | Owner: "compact rows so everything fits". The admin menu's Gym pages were below the visible area (the middle scrolls with no bar). Rows 40 → 32 (fold rows 36 → 28), smaller icons, tighter section headings and gaps, smaller logo; text sizes unchanged. Everything fits at the owner's window height with Front desk open; it scrolls again only when Attendance and Payroll are open too. Coach, member and front desk strips unchanged. |

Checks: 1277 C# tests pass; Windows and Android build. Not opened in the app here (owner checks it).

### Withholding tax on the month (`0160`)

Owner, 2026-11-25 (screenshot): "Only have withholding tax when the salary exceeds 20,833 PHP." Tax was taken every pay on that pay with BIR's semi-monthly table (0144, T3), so a month under ₱20,833 was taxed whenever one half went over ₱10,417 (e.g. ₱15,600 earned in 16–30 Nov was taxed ₱572.70). Now:
- Withholding tax is worked out **once a month, in the 16th–end pay**, on the whole month's taxable pay (both halves, less non-taxable pay, retro deductions and the month's contributions), with **BIR's monthly table** (RR 11-2018 Annex E, 2023 on): nothing up to ₱20,833, then 15% over ₱20,833; ₱1,875 + 20% over ₱33,333; ₱8,541.80 + 25% over ₱66,667; ₱33,541.80 + 30% over ₱166,667; ₱183,541.80 + 35% over ₱666,667.
- The **1st–15th pay has no tax**, like SSS, PhilHealth and Pag-IBIG. Tax a 1st–15th pay already took (approved before `0160`) is taken off the month's tax, so it is never taken twice. Approved and paid-out periods keep their frozen lines.
- `payroll_half_tax` (plpgsql, closed to the app) gives `payroll_basis` the 1st–15th's taxable pay and tax. Demo the same; the pay details say "SSS, PhilHealth, Pag-IBIG and withholding tax are taken in the 16th–end pay, on the month." and show tax only in the 16th–end pay.
- Tests: new `payroll-monthly-tax.mjs` (4, written first: the owner's ₱15,600 case pays no tax); `payroll-deductions.mjs` and `payroll-exemptions.mjs` and the C# payroll tests moved to the monthly rule (e.g. ₱13,000 + ₱13,000: 1st–15th tax 0, 16th–end tax ₱452.55 on ₱23,850). 1278 C# tests pass; all payroll db tests and `rules.mjs` pass; Windows builds. Deployed: dry run showed only `0160`; pushed; live brackets start at 20,833; the new helper is closed.
- Left as is: the frozen tax line's note still reads "Withholding tax, 16–31 Oct 2026, on ₱X taxable", where X is now the month's taxable pay.

**Owner hand checks still open (Windows, System admin and Admin):** the menu (fits, folds, current page lit, each Payroll and Attendance entry opens its tab on first click); Attendance (Review and Records tables, Approve with the money table, an overlapping record, Corrections add and remove); Payroll Payslips tab (Print payslip); Expenses (add, void); Financial reports (a month with approved payroll, a year); Amenity status margins; Employees page (coaches and front desk only, filter, pay, no add form); Cashier Membership (new member + package + Record payment / GCash in one go; a used email stops before any payment); Payroll tax (16–30 Nov: a month under ₱20,833 shows ₱0 tax; the 1st–15th pay has no tax; pay details say tax is taken in the 16th–end pay). Tax method worth confirming with the accountant.

### Cashier Membership: account, package and payment in one go

Owner, 2026-11-25: "when I register a member using Cashier I can't give them membership and have payment option to proceed the account creation. The membership in Cashier must be: when creating the account the cashier sets what package for the account, then proceeds to payment. The one we have now is just creation of the account."

Root cause: Membership showed the register form and the package cards together, but **Record payment** needed a chosen member, so registering was a separate step; after it, the new account was picked by **searching its email**, and that search can read a profile list cached from before the account existed (the gateway cleared its 10 s cache before a server function ran, not after), so no customer was chosen and only the account was made.

Fix:
- Membership is one flow: **New member** details, choose a package, then **Record payment** (cash) or **GCash / QR Ph**. One confirm ("Register Nora Cruz and record this payment?"); the account is made first (a refused email stops there and nothing is paid), picked as the customer **by its id**, then the payment goes through as before. The temporary password is in the final message (and stays on screen while an online payment is open). The separate Register member button is gone; the order says "New member: <name>".
- `SupabaseGateway.InvokeFunctionAsync` also clears the read cache **after** the function returns, so no page reads a list from before a server function's change.
- Tests: `Membership_registers_the_account_with_its_package_and_payment_in_one_go` (failed first: Record was off without a member), `Registering_an_email_already_in_use_is_refused_and_nothing_is_paid`, `A_new_member_can_pay_online_and_keeps_the_temporary_password_on_screen`. 1279 C# tests pass; Windows builds. App only, no migration.

### Employees is its own page

Owner, 2026-11-25 (screenshot of Employees showing Users and roles with its add-account form and members): "why is Employees just a copy of Users and roles? Make this page show only employees and coaches with a filter, and remove the add account."

Cause: W5 had the menu's Employees open Users and roles with a staff-only filter (a tab sent through `IOpensTab`); the page and its add form were Users and roles', and the filter didn't take.

Fix: **Employees** is its own page (`EmployeesPage`, `EmployeesViewModel`, route `employees`, icon `user-round`): coaches and front desk only; **All | Coaches | Front desk**; a table of name and sign-in email, role, **pay** (from Salary Configuration, e.g. "₱130.00 per hour", or "No pay set") and status (cards on a narrow window); a line saying accounts are added on Users and roles and pay is set on Payroll → Salary Configuration; no add form and no account actions. Users and roles loses the staff-only filter (`StaffOnly`, Show everyone) and `IOpensTab`; the menu's Users and roles entry stays under Gym. Overview gets an Employees link. Tests: `Employees_lists_coaches_and_front_desk_with_their_pay_and_a_filter`; menu, shell and overview tests updated; 1280 C# tests pass; Windows builds. App only.

### No tab bars on Payroll and Attendance

Owner, 2026-11-25 (screenshots of Attendance's Review / Records / Corrections and Payroll's four tabs): "remove the 4 buttons in Payroll, they must just access it in the nav bar, and the 3 buttons in Attendance." Both tab bars are gone; the menu's Payroll and Attendance entries open each part (as before), and the **page title says which part**, in the menu's words: Payroll Processing, Payslips, Payroll History, Salary Configuration; Attendance Review, Attendance Records, Attendance Corrections (`SectionTitle`). Links inside the pages (e.g. Set salaries, a paid-out period) still switch parts. The pending count is in Attendance's period bar ("Pending · N records to review"). `PayrollPaidOutTests` no longer looks for the removed tab button. 1280 C# tests pass; Windows builds. App only.

