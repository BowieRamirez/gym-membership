# Hourly and per-session pay on Payroll Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-24
Last updated: 2026-11-25
Status (2026-11-24): H1-H17 confirmed by the owner (as recommended). Phases 1-2 built, reviewed, live-tested and fixed. The branch also carries the follow-ups listed under "Everything on this branch" below. Migrations `0142`-`0146` all live; branch pushed (in sync with `origin`); owner hand checks and a new app release left. **Not merged.** Next migration `0147`.

## Everything on this branch (2026-11-24)

| What | Plan | Database | Commits |
|---|---|---|---|
| **Hourly and per-session pay**: pay type per person and per role (personal rate beats the role default), each cash shift / coach check-in / session approved or rejected before it pays, overlaps counted once, hour corrections, bonus/deduction categories, gross, frozen on paid, hourly periods paid from the day after | this plan (H1-H17) | `0142`, `0143` | `f4fd9c5`, `3f4c641`, `01901ad`, `438b38a` |
| Live test of every payroll function as the owner's admin (13/13) and fixes it found: period heading kept the old end date; selected Bonus/Deduction invisible in dark | this plan, Session log | — | `3f5e636` |
| **Packages page**: members see **Memberships** and **Day pass** as separate sections on the same page; the Day pass button says "Buy day pass" | — (owner request, see Session log) | — | `93ef5e4` |
| **Government deductions**: SSS, PhilHealth, Pag-IBIG once a month in the 16th–end pay; BIR withholding tax every pay; rates in one table; never below ₱0; the 16th–end waits for the 1st–15th; frozen on paid | `2026-11-24-payroll-deductions.md` (T1-T12) | `0144` | `4796f65`, `77c66b4` |
| **Exemptions and the payout steps**: separate switches for contributions and tax (minimum wage earners) with a reason; 13th month / other benefits tax-free up to ₱90,000 a year; de minimis never taxed; Retro pay / adjustment; **Approve → Reopen (with a reason) → Mark as paid out** | `2026-11-24-payroll-exemptions-reopen.md` (U1-U12) | `0145` | `2bf837f`, `6d6eb86` |
| **Paid out tab** on Payroll: every period marked as paid out with its frozen totals (people, base pay, bonuses, gross, deductions, government deductions, net, who approved, who paid out) | — (owner request; built with Gemini, see Session log) | `0146` | `5d7d5d9` |
| **Staff page tabs and a layout fix**: Revenue gets **Revenue / Cash shifts** tabs, Users and roles gets **Active accounts / Deactivated** tabs (both were stacked tables), Payroll Salaries' government-deductions editor gets its own full-width line | — (owner request, see Session log) | — | `8595f8d` |
Branch: `hourly-payroll-2026-11-24` (cut from `origin/main` at `a464366`, v0.10.0). **Owner's rule: this branch is not merged into `main` yet.** No PR until the owner asks.

**Goal:** Payroll pays each person by their own arrangement instead of one fixed salary for everyone: **Hourly** (approved hours × hourly rate), **Per session** (completed sessions × session rate) or **Fixed** (today's salary per period). Hours come from the records the app already keeps (front desk cash shifts, coach check-in/out); **a record only counts once the admin approves it** (a raw log never becomes payable time by itself), overlapping records are never counted twice, the admin can correct totals with a reason while the period is a draft, and Mark as paid freezes the hours, rate and pay so a later rate change never alters a paid period.

**Architecture:** Extend Part 3's payroll (`0136`-`0138`), don't replace it. `staff_salaries` and `role_salaries` gain a pay type; `amount` becomes the rate for that type. Hours already computed by `payroll_period` (closed `cash_shifts` for front desk) and the coach check-in/out table `coach_visits` (0032) become the pay basis. Two new tables: hour corrections, and a review status per shift/visit (no row = Pending; the shift and visit tables themselves are not touched). `payroll_lines` gains `quantity`, `rate` and `pay_type` so the frozen salary line explains itself. Same page (`PayrollPage`), same drawer, same PDF path (`PayrollPdf`), same permission (`reports:revenue`), same audit.

**Tech Stack:** Supabase (Postgres migrations), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** `C:\Users\bowie\Downloads\hourly_based_payroll_recommendation_v2.md` (owner, 2026-11-24), cut down to its own "first implementation" list (section 13): rates per person, approved hours, correct payable-hour maths, gross and net, a per-person breakdown. Paper time sheet (DTR) checks, overtime/holiday/night premiums, statutory deductions and a multi-step approval workflow are left out (H10).

## How to use this plan

Same rules as `2026-11-20-desk-customer-shifts.md`:

- Decisions first. Nothing is built until the owner confirms H1-H17. One phase at a time, in order; each phase ends working and committed.
- **Skills, every phase:**
  - `ponytail` full mode for the whole session (smallest diff, reuse before writing).
  - `superpowers:test-driven-development` for every rule: the failing test first, watched failing.
  - `superpowers:systematic-debugging` for any bug met on the way: reproduce before fixing.
  - `impeccable` (and `frontend-design` per CLAUDE.md) for every screen change. Payroll is a staff page: **Windows only**, light and dark, narrow and wide widths.
  - `ponytail:ponytail-review` on each phase's diff before its commit.
  - `superpowers:verification-before-completion` before any "done".
  - A fresh whole-branch review at the end (`superpowers:requesting-code-review`).
- Push each phase's migrations live when the phase is finished (dry run first) and check live grants (`supabase-recreated-function-grants`). Commits pushed to `origin/hourly-payroll-2026-11-24`; nothing merges into `main`.
- The owner tests the app themself: unit tests, DB tests and builds only; no click-testing or live checks unless asked.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Database: pay type and rate (personal rates survive role defaults), shift/visit/session approval, overlap-free hours as the pay basis, hour corrections, categories, gross, frozen lines | H1-H9, H11-H17 | Done (`f4fd9c5`) |
| 2 | Payroll page: pay type on Salaries, hours × rate on the period, approve/reject records and corrections in the drawer, categories, gross, PDF | H1-H9, H11-H17 | Done (`3f4c641`) |
| 3 | Checks, review, owner hand checks | | Review done and fixed; owner hand checks left |

## 1. Findings (2026-11-24)


| Item | Today | Consequence |
|---|---|---|
| Pay | `staff_salaries` (user → `amount` per period) and `role_salaries` (`employee` / `coach` default, 0137, copied to everyone with that role). Fixed per period, hours never change pay (P1). | Add `pay_type` to both; `amount` keeps its column and becomes "rate for that pay type". Existing rows become `fixed`, so nothing changes for anyone until the admin switches them. |
| Front desk hours | `payroll_period` already sums closed `cash_shifts` (`closed_at - opened_at`) by the Manila date of `opened_at`. | This becomes the hourly basis (H2). Open shifts are not counted (flagged, H6). |
| Coach hours | `payroll_period` sums completed `training_sessions` hours; `coach_visits` (0032) has the desk's check-in/out per coach; visits still open at closing time are auto-closed (`auto_closed`). | Hourly coach = `coach_visits` hours (H3). Per-session coach = completed sessions count (the existing count, no-shows and unpaid held sessions excluded). |
| Bonus / deduction lines | `payroll_lines` kinds `salary | bonus | deduction`, note required for bonus and deduction, draft only, audited. | Unchanged. Overtime or holiday pay can be a Bonus line with a note until real rules exist (H10). |
| Mark as paid | Locks the period (`for update`), refuses `payroll_paid`, `period_not_ended`, `net_below_zero`, freezes a `salary` line per person from `staff_salaries`. | The frozen line becomes the computed pay, with `quantity`, `rate`, `pay_type` written next to it (H7). |
| Breakdown | Row → side drawer with the person's lines; `PayrollPdf` prints a payslip block per person. | The drawer and payslip show "41.50 h × ₱80.00 = ₱3,320.00" and the corrections (H5). |
| Access, audit | `reports:revenue` (Admin, System admin); `audit()` with readable payroll names (0138); no IPs (0083). | Same permission; new audit actions (H9). |

## 2. Decisions

All recommended by Claude, **to confirm by the owner**.

| # | Decision | Answer |
|---|---|---|
| H1 | **Pay type per person:** **Fixed** (amount per period, today's behaviour), **Hourly** (rate per hour) or **Per session** (rate per completed session; coaches only). Set on the Salaries tab per person, and as the role default (Front desk / Coach) like 0137. Existing salaries become Fixed. | Recommended |
| H2 | **Front desk hours = closed cash shifts** in the period (open → close, by the Manila date the shift opened). This is the only time-in/out the desk has. *Owner to confirm it matches when they actually work* (e.g. no one works long before opening or after closing the drawer); if not, corrections (H5) cover the gap. | Recommended, to confirm |
| H3 | **Hourly coach hours = desk check-in/out (`coach_visits`)**, closed visits in the period, by the Manila date of check-in. Time at work, not only time in sessions. Auto-closed visits count up to the closing time they were closed at, and are flagged (H6). | Recommended |
| H4 | **No double pay:** one pay type per person, so a per-session coach is never also paid hourly for the same time. Per session counts the same sessions the reference count shows today (completed, in the period, not an unpaid held add-on). | Recommended |
| H5 | **Hour corrections:** on a draft the admin adds **+ or − hours** for a person with a required note (1-200 characters, e.g. "Forgot to open the drawer 8-9 am on 3 Nov"). Removable while draft. The original shift and visit records are never edited. Payable hours = logged hours + corrections, never below 0. Per-session people get **+ or − sessions** the same way. | Recommended |
| H6 | **Flags (shown, not blocking):** a shift or visit still open at the end of the period (not counted), an auto-closed visit, any single shift or visit over 12 hours, records that overlap (H13). Shown as a warning chip on the row and listed in the drawer and in the Mark as paid confirm. | Recommended |
| H7 | **Frozen on paid:** Mark as paid writes the salary line with `pay_type`, `quantity` (hours or sessions, after corrections) and `rate`, and `amount = round(quantity × rate, 2)` (Fixed: quantity 1). A rate or pay type changed later never changes a paid period. No separate rate-history table: the paid line is the history. | Recommended |
| H8 | **Rate changes on a draft:** a draft uses the person's **current** pay type and rate for the whole period (same as P3 today). Changing a rate mid-period: change it after Mark as paid, or fix the difference with a Bonus/Deduction note. | Recommended |
| H9 | **Rounding and money:** hours to the minute, shown to 2 decimals; pay rounded to the centavo once per person (never per shift). Money `numeric(12,2)`, hours `numeric(8,2)`. Audit: `payroll.salary_set` now records old → new pay type and rate; new `payroll.hours_adjusted` / `payroll.hours_adjustment_removed`. | Recommended |
| H10 | **Out of scope for now:** paper DTR comparison, automatic overtime / holiday / rest day / night differential, SSS / PhilHealth / Pag-IBIG / tax, Draft → For review → Approved steps, monthly salary proration, staff seeing their own payslip. These need the gym's accountant to set the rules; until then, Bonus/Deduction lines with notes cover them (P5). | Recommended |
| H11 | **Mark as paid with no rate:** someone Hourly or Per session with 0 hours/sessions gets ₱0 and is listed; someone with no pay set stays "No salary set" (P2). `net_below_zero` still refuses. | Recommended |
| H12 | **Each shift and visit is approved before it pays** (source document: "a raw time log must not automatically become payable time"). For every **Hourly** person, each closed cash shift / coach visit in the period (and for every **Per session** coach, each completed session, H15) is **Pending** until the admin marks it **Approved** or **Rejected** (reason required, 1-200 characters). Only Approved records count; Pending and Rejected show in the drawer but add 0 h. Quick path: **Approve all** for a person (or the whole period) approves their Pending records **that have no flag** (H6); flagged ones must be approved one by one. The decision can be changed while the period is a draft and is locked once paid. **Mark as paid is refused while any Hourly or Per session person has a Pending record** (`time_pending`, naming the person), so nothing is forgotten. Fixed people need no review (Fixed ignores hours). Status is kept in its own table (no row = Pending), so `cash_shifts` and `coach_visits` are never edited. Audit: `payroll.time_approved`, `payroll.time_rejected`, `payroll.time_reopened` (record, person, old → new status, reason). | Owner asked to add, 2026-11-24 |
| H13 | **Overlapping time counts once.** Before summing, a person's Approved records in the period are merged where they overlap (two shifts 8:00-12:00 and 11:00-14:00 = 6 h, not 7 h). Overlapping records are also flagged (H6) so the admin sees why the total is lower. Done in the one basis helper so the draft and the frozen line agree. | Owner asked to add, 2026-11-24 |
| H14 | **A personal rate beats the role default.** Today `set_role_salary` (0137) overwrites everyone in the role, even someone given their own amount. Now each `staff_salaries` row has `follows_role boolean`: setting a role default updates (and creates) only rows that follow the role; setting a person's pay (`set_staff_salary`) makes their row personal (`follows_role = false`); **Use role default** on the Salaries tab sets it back to following the role and copies the current default. Migration: existing rows whose type and amount equal their role's default become `follows_role = true`, the rest stay personal. The Salaries tab shows "Role default" or "Personal" next to each rate. | Owner's task list, 2026-11-24 |
| H15 | **Per-session coaches' sessions are approved too.** Each completed session in the period (the same sessions counted today: completed, not a no-show or cancelled, not an unpaid held add-on) is Pending until the admin Approves or Rejects it (reason required), exactly like shifts and visits (H12): same table (`source = 'training_session'`), same drawer list (date, start → end, member or walk-in name, status), same **Approve all**, same `time_pending` block on Mark as paid. Pay counts **approved** sessions only. | Owner's task list, 2026-11-24 |
| H16 | **Bonus and deduction categories.** Every Bonus or Deduction line has a category: **Regular bonus**, **Overtime adjustment**, **Holiday pay adjustment** or **Other adjustment** (all four allowed on both kinds), plus the required note (P5). `payroll_lines.category`; existing lines become Other adjustment. Shown in the drawer, the table's tooltip, the audit row and the payslip. | Owner's task list, 2026-11-24 |
| H17 | **Gross pay is shown.** Gross = base pay + bonuses; Net = gross − deductions. The period table gets a **Gross** column before Deductions, the top bar shows total gross next to total net, and the payslip shows base pay, each bonus, **Gross pay**, each deduction, **Net pay**. Frozen on Mark as paid like the rest. | Owner's task list, 2026-11-24 |

## The Payroll page changes (impeccable brief)

Run `impeccable` before the XAML and again before the commit. Same **operate mode** as Part 3: an admin closing a pay run twice a month at a desk. Keep the existing page, components and sage/lime look; this is a change to existing tables, not a new page.

- **Salaries tab:** each row gets a **Pay type** (Fixed / Hourly / Per session, segment or picker; Per session only for coaches) and the amount label follows it: "₱8,000.00 per period", "₱80.00 per hour", "₱150.00 per session". **Set pay** edits type + amount inline (Save, Cancel). The role defaults at the top get the same pair. Each person's rate shows a quiet "Role default" or "Personal" tag; a personal one has **Use role default** (H14).
- **Pay period table:** the Hours column becomes the **basis**: "41.50 h × ₱80.00" / "12 sessions × ₱150.00" / "Fixed". Corrected hours show a small "(+1.00 h)" after them. Salary column renamed **Base pay**; columns are Base pay · Bonuses · **Gross** · Deductions · **Net pay**, and the top bar shows total gross beside the total net (H17). A flag chip (H6) on rows with open, auto-closed or very long records. Money right-aligned, tabular figures, as today.
- **Pending count:** rows with Pending records show "3 to approve" (warning chip); the quiet line under the total says "5 shifts to approve" and **Approve all unflagged** sits next to it (H12).
- **Drawer:** the basis line ("Approved 40.50 h · corrections +1.00 h · payable 41.50 h × ₱80.00 = ₱3,320.00"), then a compact list of the person's shifts/visits (or, for a per-session coach, sessions with the member or walk-in name) in the period (date, in → out, hours, flag, status chip Pending / Approved / Rejected) with **Approve** and **Reject** (reason box) on each, and **Approve all unflagged** above the list. Overlapping records are marked "overlaps, counted once". **Add hours correction** (± hours + note; ± sessions for Per session) next to Add bonus / Add deduction, remove on each correction while draft. Read-only once paid.
- **Mark as paid:** disabled with "Approve or reject 5 shifts first" while anything is Pending (the server refuses `time_pending` too). The confirm adds "3 records flagged" with the names, like the no-salary names today.
- **Add bonus / Add deduction:** a category picker (Regular bonus, Overtime adjustment, Holiday pay adjustment, Other adjustment) above the amount and note; each line in the drawer shows its category (H16).
- **PDF:** each payslip block shows name, role, period, pay type and rate, the basis line, the corrections with notes, each bonus and deduction with its category and note, **Gross pay** and **Net pay** (H17).
- **Anti-goals:** no clock-in screen, no timesheet grid per day, no charts, nothing on member, coach or desk screens.

## Global Constraints

- Everything in `2026-11-20-desk-customer-shifts.md` Global Constraints (Part 3), plus: migrations from `0142`. Every new or recreated function revokes `public, anon, authenticated` and grants `authenticated`, gated inside by `require_permission('reports:revenue')`. Check live grants after the push.
- A paid period never changes. Existing paid lines (no `quantity` / `rate`) still read as Fixed.
- Demo gateway mirrors every rule (`DemoPayroll.cs`).

## Review Focus

1. **Front desk, coach, member or anon** calls any payroll function or reads the new table → refused / no rows.
2. **Hourly front desk with two closed shifts (4 h and 3.5 h) and one open shift** → 7.50 h payable, open shift flagged and not counted.
3. **Rate changed after a period is paid** → paid period's line, rate and quantity unchanged; the next draft uses the new rate.
4. **A correction of −10 h on someone with 6 h** → payable 0 h, pay ₱0, not negative.
5. **Per-session coach** → paid by approved completed sessions only; no-shows, cancelled and unpaid held add-on sessions not counted; check-in hours ignored.
6. **Add or remove a correction on a paid period** → `payroll_paid`, nothing changes.
7. **Old paid periods from Part 3** still show and export the same totals.
8. **Hourly person with 3 shifts: 1 Approved, 1 Pending, 1 Rejected** → only the Approved shift counts; Mark as paid refused `time_pending` naming them until the Pending one is decided.
9. **Approve all** → approves only unflagged Pending records; flagged ones stay Pending.
10. **Approve or reject on a paid period**, or a record outside the period, or a record of someone else's period → refused, nothing changes.
11. **Two approved shifts 8:00-12:00 and 11:00-14:00** → 6.00 h, both flagged as overlapping; a shift fully inside another adds nothing.
12. **Reject without a reason** → refused.
13. **Ana has a personal ₱90/h, the Front desk default changes to ₱80/h** → Ana stays ₱90, everyone following the role gets ₱80; **Use role default** on Ana → ₱80 and she follows the role again.
14. **Per-session coach with 3 completed sessions, 2 approved, 1 pending** → paid for 2; Mark as paid refused `time_pending` until the third is decided; a rejected session pays nothing.
15. **Bonus or deduction without a category, or with an unknown one** → refused; old Part 3 lines show as Other adjustment.
16. **Base ₱3,000, bonuses ₱500, deductions ₱200** → gross ₱3,500, net ₱3,300, the same on the page, the frozen period and the PDF.

## Phase 1: Database (H1-H9, H11-H17)

### Task 1: Pay type, corrections, computed pay

**Files:** migration `0142_hourly_payroll.sql`:
- `staff_salaries` and `role_salaries`: `pay_type text not null default 'fixed' check (pay_type in ('fixed','hourly','per_session'))`; `per_session` only for a coach (checked in the setter).
- `payroll_lines`: nullable `pay_type`, `quantity numeric(8,2)`, `rate numeric(12,2)`; salary lines written by Mark as paid fill them.
- New `payroll_hour_adjustments` (`adjustment_id`, `period_id`, `user_id`, `hours numeric(8,2) check (hours <> 0)` (sessions for per-session people), `note` 1-200 required, `created_by`, `created_at`). RLS on, no grants.
- Functions (each `require_permission('reports:revenue')`):
  - `set_staff_salary(p_user, p_amount, p_pay_type default 'fixed')` and `set_role_salary(p_role, p_amount, p_pay_type default 'fixed')`: drop and recreate with the new argument; audit old → new type and amount.
  - `payroll_staff()` / `payroll_role_salaries()`: return `pay_type`.
  - `payroll_period(p_starts_on)`: per person also returns `pay_type`, `rate`, `logged` (hours or sessions by H2/H3/H4), `adjustment`, `payable` (≥ 0), `base_pay` (`round(payable × rate, 2)`, or the fixed amount), and `flags` (H6); once paid, all from the frozen line. Net = base pay + bonuses − deductions.
  - `payroll_add_hours(p_period, p_user, p_hours, p_note)` / `payroll_remove_hours(p_adjustment)`: draft only (`payroll_paid`), audited.
  - `payroll_adjustments_for(p_period, p_user)` and `payroll_flags_for(p_period, p_user)` for the drawer (or fold into `payroll_lines_for`; pick the smaller diff).
  - `payroll_mark_paid(p_period)`: same locks and refusals, plus `time_pending:<name>` while any Hourly person has a Pending record; freezes `base_pay` with `pay_type`, `quantity`, `rate`.
  - The basis maths lives in one place (a private helper function both `payroll_period` and `payroll_mark_paid` call), so the draft and the frozen line can't disagree. It counts only Approved records and merges overlapping intervals per person before summing (H13; gaps-and-islands over `tstzrange`, or `range_agg` if the live Postgres has it).
- H14: `staff_salaries.follows_role boolean not null default false` (backfilled as in H14); `set_role_salary` touches only `follows_role` rows (and inserts missing ones as following); `set_staff_salary` sets `follows_role = false`; new `use_role_salary(p_user)` (audited `payroll.salary_set` with old → new).
- H16: `payroll_lines.category text check (category in ('regular_bonus','overtime','holiday','other'))`, required for bonus and deduction, null for salary; backfill existing bonus/deduction lines to `other`; `payroll_add_line` takes `p_category`; `payroll_lines_for` returns it.
- H17: `payroll_period` returns `gross` per row and `total_gross`; the frozen period computes the same from its lines.
- New `payroll_time_reviews` (`source text check (source in ('cash_shift','coach_visit','training_session'))`, `source_id bigint`, primary key `(source, source_id)`, `status text check (status in ('approved','rejected'))`, `reason` required for rejected (1-200), `reviewed_by`, `reviewed_at`). RLS on, no grants. No row = Pending.
- Functions: `payroll_records_for(p_period, p_user)` (the person's shifts/visits in the period: times, hours, flags, status, reason) for the drawer; `payroll_review_time(p_period, p_source, p_source_id, p_status, p_reason)` (`approved`, `rejected` or `pending` to reopen; the record must belong to the period and to an Hourly person (shift/visit) or a Per session coach (session); draft only, `payroll_paid`; audited); `payroll_approve_all(p_period, p_user default null)` (unflagged Pending only; returns how many; audited once with the count).
- App also: `PayrollTimeRecord` model, `Foundation.cs` sentence for `time_pending`.
- App: `IAdminService` + `Services.cs`, `Models.cs` (`PayrollRow`, `StaffSalary` gain pay type, rate, logged, adjustment, payable, flags; new `PayrollHourAdjustment`), `Foundation.cs` sentences (`per_session_coach_only`), `DemoPayroll.cs` mirror.
- Tests: `supabase/tests/local/payroll.mjs` (extend), `tests/GymMembership.Tests/PayrollTests.cs`.

- [x] Failing tests first: Review Focus 1-16; existing salaries read as Fixed with the same totals; per session refused for a front desk person; correction without a note refused; auto-closed visit counted and flagged; a shift over 12 h flagged; pay rounded once per person (e.g. 3 shifts of 20 min at ₱80 = ₱80.00).
- [x] Migration, dry-run push, push live, live grants checked (anon and non-admin refused), `ponytail:ponytail-review`, commit, push branch.

## Phase 2: Payroll page

### Task 2: Salaries tab, period table, drawer, PDF

**Files:** `Views/PayrollPage.xaml` (+ `.xaml.cs`), `PayrollViewModel` in `StaffViewModels.cs` (pay type editing, basis text, flags, add/remove hours), `PayrollPdf` in `RevenueReport.cs`, `PayrollTests.cs` (basis text per pay type; corrections shown; paid period hides Add hours and Approve/Reject; Mark as paid disabled while records are Pending; Reject needs a reason; Approve all leaves flagged records Pending; Role default / Personal tag and Use role default; category required on Add bonus/deduction; gross = base + bonuses on the row, the total and the payslip; confirm names flagged people; the PDF payslip lists the basis and corrections).

- [x] `impeccable` before the XAML (brief above); build; check with `impeccable` (critique and audit) in light and dark on Windows at narrow and wide widths; fix what it finds.
- [x] View model tests green; Windows build; `ponytail:ponytail-review` on the diff; commit, push branch.

## Phase 3: Checks

- [x] Whole suite (`dotnet test`, Windows & Android builds, `payroll.mjs`) green.
- [x] `superpowers:requesting-code-review` on the branch diff; fixes committed.
- [ ] Owner hand checks (Windows): set a front desk person to Hourly ₱80 and a coach to Per session ₱150, open the current period, check the hours against the cash shifts, approve some shifts, reject one with a reason, try Mark as paid with one still Pending (refused), approve a per-session coach's sessions, give one person a personal rate then change the role default (personal rate kept), add a bonus with a category and check gross, add a correction, export the PDF, mark a past period paid, change a rate and confirm the paid period didn't change.

## Release notes for the next version

- Payroll pays Fixed, Hourly or Per session; shifts, check-ins and sessions are approved before they pay; corrections, categories and gross on the page and payslip.
- Payroll takes SSS, PhilHealth and Pag-IBIG (once a month, in the 16th–end pay) and BIR withholding tax (every pay), with separate switches per person for contributions and tax (e.g. minimum wage earners). Rates from the 2026 tables: **have the accountant confirm them before the first real payroll.**
- New bonus categories: 13th month / other benefits (tax-free up to ₱90,000 a year), De minimis (never taxed), Retro pay / adjustment.
- "Mark as paid" is now **Approve** (locks the period); an approved period can be **reopened** with a reason until it is **marked as paid out** (after the money is sent). Periods paid before this release count as paid out.
- Members' Packages page shows Memberships and the Day pass in separate sections.
- Payroll has a **Paid out** tab listing every paid-out period and its totals.
- Revenue has **Revenue / Cash shifts** tabs; Users and roles has **Active accounts / Deactivated** tabs.
- **Update the admin app before using these.** On 0.10.0: its "Mark as paid" approves; it can't approve shifts (so it can't pay hourly people), reopen, mark paid out or set the two switches (its single switch turns both off/on); its totals already include the government deductions. Fixed-only payroll still works on 0.10.0.
- Hourly and per-session periods are approved from the day after the period ends (fixed-only periods: on the last day, as before); a 16th–end is approved only after the same month's 1st–15th.

## Session log

- 2026-11-24: plan written from the owner's `hourly_based_payroll_recommendation_v2.md`, cut down to its section 13 first implementation on top of Part 3 payroll. Branch `hourly-payroll-2026-11-24` cut from `origin/main` (`a464366`). H1-H11 recommended, waiting on the owner. Not built. Next migration `0142`.
- 2026-11-24: owner compared the plan with the source document and asked to add (1) per-shift approval and (2) no double-counting of overlaps: H12 and H13 added (new `payroll_time_reviews` table, Approve / Reject / Approve all, `time_pending` blocks Mark as paid, overlapping approved time merged). Still left out on purpose: breaks, paper DTR, rate history table, extra statuses, per-person payment records, premiums and statutory deductions, fuller summary (see H10).
- 2026-11-24: owner checked the plan against their own Payroll task list (Phases 1-9) and asked to add the four gaps: H14 personal rates survive role defaults (`follows_role`), H15 per-session coaches' sessions approved like shifts, H16 bonus/deduction categories (Regular bonus, Overtime, Holiday pay, Other), H17 gross pay on the page, totals and payslip.
- 2026-11-24: owner: H1-H17 as recommended, start building. Phase 1 (`0142_hourly_payroll.sql`, services, demo mirror; `payroll-hourly.mjs` 14/14, `payroll.mjs` updated for H14) and Phase 2 (Payroll page, view model, payslip PDF) built and committed (`f4fd9c5`, `3f4c641`). Rulings recorded in `.superpowers/sdd/2026-11-24-hourly-payroll/progress.md`: H14 done as "no personal row = follows the role" (no `follows_role` column); `p_pay_type` / `p_category` default so the released v0.10.0 app keeps working; open shifts are flagged, not counted, never blocking; the impeccable check was done on the code, not the running app (owner tests the app).
- 2026-11-24: fresh whole-branch review: no Critical. Fixed: (1) a period with hourly or per-session people can be marked paid only from the day after it ends (on the last day itself, a shift later that day would have been locked out and never paid; fixed-only periods keep Part 3's last-day rule); (2) a paid period no longer shows "to approve" for shifts closed after paying. Deferred minors listed in the ledger (null status error text, Use role default audit when already following, correction suffix without a unit, paid "Approved X h" recomputed from live records, v0.10.0 admins see a generic message for `time_pending`).
- 2026-11-24: `0142_hourly_payroll.sql` pushed live to `avxcitoddadbblxuhiik` (dry run first, only `0142`). Live grants checked read-only: every payroll function refuses anon; the five private helpers (`payroll_role`, `payroll_pay`, `payroll_records`, `payroll_basis`, `payroll_draft`) refuse everyone; the two new tables have RLS on with no policies, like `payroll_lines`. Live `staff_salaries` had 0 rows after the H14 cleanup, so everyone follows their role default. Local: unit 1259/1259, DB suite 744 ok, Windows & Android builds. Left: owner hand checks; ship a new app version before admins rely on hourly pay (v0.10.0 can't approve shifts). Next migration `0143`.
- 2026-11-24: owner asked to fix the deferred minors too. `0143_payroll_review_minors.sql`: a missing status is `bad_input`; Use role default on someone already following the role changes and logs nothing; a paid period's "Approved" hours come from the frozen line (payable minus corrections). App: the row's correction shows its unit ("(+1.5 h)"), demo mirrors all three. The 0.10.0 generic message can't be fixed server-side: release note added above.
- 2026-11-24: `0143` pushed live (dry run first, only `0143`); grants checked: `payroll_basis` / `payroll_pay` / `payroll_records` refuse everyone, `payroll_review_time` / `use_role_salary` signed-in only (admin check inside). Unit 1259/1259, DB suite 747 ok, Windows build. Next migration `0144`.
- 2026-11-24: owner asked for a live test of every payroll function as their admin account (throwaway staff + 1-15 Jan 2025 only). 13/13 live function checks passed: refusals for desk and signed-out users, pay types and per-session-for-coaches-only, Use role default (role defaults untouched), pending / warnings (overlap, over 12 h, auto-closed), approve / reject with reason / back to pending / missing status, time_pending, Approve all skipping flagged, overlap counted once, hourly coach from check-ins, per-session coach from approved sessions, corrections (note, floor at 0, remove), categories, gross/net, current period refused, Mark as paid freezing type/hours/rate and locking, Logs & Archive names. Screens checked in the app built from this branch: period table, paid read-only drawer, draft drawer (Approve all, reject without/with reason, correction without/with note, Holiday bonus), Salaries tab and Use role default. Found and fixed: the period heading kept the old end date when stepping back from a 16th-end period ("1-31 Jan", older Part 3 bug), and the selected Bonus/Deduction button was hard to see in dark mode. Not clicked on screen: Set pay (blocked as it could hit a real row; covered by the function test) and Export PDF (saves to Downloads; covered by unit tests). Left on live: paid test period 1-15 Jan 2025 (#ids in the plan's live test, includes your real staff at their Jan 2025 rates: hourly people at 0 h, test three ₱13,000 fixed), period row 16-31 Jan 2025 (draft), test shifts/visits/sessions of the archived PAYROLLTEST accounts. Live role defaults at the time: Front desk ₱130/h, Coach ₱100/h (set by the owner).
- 2026-11-24: owner asked to organise the members' Packages page: Memberships (shortest first) and the Day pass as separate sections on the same page, Day pass card says "Buy day pass"; the Day pass section stays hidden for members with a membership (0098). App only (`PackagesViewModel.Memberships / DayPasses`, `MembershipPackage.BuyText`, `PackagesPage.xaml`), test `Packages_shows_memberships_and_day_passes_apart`; checked in a demo copy (member with and without a membership). Commit `93ef5e4`. Phone layout, light mode and 1.3 font scale left for the owner.
- 2026-11-24: follow-up plans on this branch, both built and live: `2026-11-24-payroll-deductions.md` (government deductions, `0144`) and `2026-11-24-payroll-exemptions-reopen.md` (two switches, 13th month / de minimis / retro, Approve → Reopen → Paid out, `0145`). See "Everything on this branch" at the top. Live: `0001`-`0145` applied (checked with `supabase migration list`). Next migration `0146`.
- 2026-11-25: owner added a **Paid out** tab to Payroll with Gemini (`payroll_paid_out_periods()` in `0146_payroll_paid_out_list.sql`: admin-only, read-only, frozen totals per paid-out period; view model, service, demo mirror, `payroll-paid-out.mjs` 2/2, `PayrollPaidOutTests.cs`). The app showed "needs a server update" because `0146` had not been pushed; checked the migration (safe, grants right), tests (unit 1288/1288), Windows build, then pushed `0146` live (dry run first) and checked grants (anon refused, signed-in only with the admin check). Committed `5d7d5d9`. Next migration `0147`.
- 2026-11-25: owner asked why a period wasn't on the Paid out tab: it had only been **Approved**; it showed up after **Mark as paid out** (no bug, no change). Owner then asked (screenshot + `Untitled document (9).pdf`): (1) Payroll Salaries' government-deductions editor was squeezed into the Last changed column at narrow widths, so it now gets its own full-width line under the row; (2) Revenue: **Revenue / Cash shifts** tabs at the top instead of the shifts table under the charts (owner chose two tabs over separate Shortage / Over tabs), and the Cash shifts tab says it uses the dates from the Revenue tab; (3) Users and roles: **Active accounts / Deactivated** tabs, the Deactivated table now has column headings, and Name incomplete shows on Active only. App only (`IsShiftsTab` on `RevenueViewModel`, `IsDeactivatedTab` on `UsersViewModel`, XAML reusing the Payroll segment style). Unit 1288/1288, Windows build. Committed and pushed `8595f8d`. Light/dark and narrow-width checks left for the owner. Next migration `0147`.
