# Hourly and per-session pay on Payroll Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-24
Last updated: 2026-11-24
Status (2026-11-24): plan written; H1-H11 recommended, **waiting on the owner to confirm**. Not built.
Branch: `hourly-payroll-2026-11-24` (cut from `origin/main` at `a464366`, v0.10.0). **Owner's rule: this branch is not merged into `main` yet.** No PR until the owner asks.

**Goal:** Payroll pays each person by their own arrangement instead of one fixed salary for everyone: **Hourly** (approved hours × hourly rate), **Per session** (completed sessions × session rate) or **Fixed** (today's salary per period). Hours come from the records the app already keeps (front desk cash shifts, coach check-in/out), the admin can correct them with a reason while the period is a draft, and Mark as paid freezes the hours, rate and pay so a later rate change never alters a paid period.

**Architecture:** Extend Part 3's payroll (`0136`-`0138`), don't replace it. `staff_salaries` and `role_salaries` gain a pay type; `amount` becomes the rate for that type. Hours already computed by `payroll_period` (closed `cash_shifts` for front desk) and the coach check-in/out table `coach_visits` (0032) become the pay basis. One new table for hour corrections. `payroll_lines` gains `quantity`, `rate` and `pay_type` so the frozen salary line explains itself. Same page (`PayrollPage`), same drawer, same PDF path (`PayrollPdf`), same permission (`reports:revenue`), same audit.

**Tech Stack:** Supabase (Postgres migrations), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** `C:\Users\bowie\Downloads\hourly_based_payroll_recommendation_v2.md` (owner, 2026-11-24), cut down to its own "first implementation" list (section 13): rates per person, approved hours, correct payable-hour maths, gross and net, a per-person breakdown. Paper time sheet (DTR) checks, overtime/holiday/night premiums, statutory deductions and a multi-step approval workflow are left out (H10).

## How to use this plan

Same rules as `2026-11-20-desk-customer-shifts.md`:

- Decisions first. Nothing is built until the owner confirms H1-H11. One phase at a time, in order; each phase ends working and committed.
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
| 1 | Database: pay type and rate, hours as the pay basis, hour corrections, frozen lines | H1-H9, H11 | Not started |
| 2 | Payroll page: pay type on Salaries, hours × rate on the period, corrections in the drawer, PDF | H1-H9, H11 | Not started |
| 3 | Checks, review, owner hand checks | | Not started |

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
| H6 | **Flags (shown, not blocking):** a shift or visit still open at the end of the period (not counted), an auto-closed visit, any single shift or visit over 12 hours. Shown as a warning chip on the row and listed in the drawer and in the Mark as paid confirm. | Recommended |
| H7 | **Frozen on paid:** Mark as paid writes the salary line with `pay_type`, `quantity` (hours or sessions, after corrections) and `rate`, and `amount = round(quantity × rate, 2)` (Fixed: quantity 1). A rate or pay type changed later never changes a paid period. No separate rate-history table: the paid line is the history. | Recommended |
| H8 | **Rate changes on a draft:** a draft uses the person's **current** pay type and rate for the whole period (same as P3 today). Changing a rate mid-period: change it after Mark as paid, or fix the difference with a Bonus/Deduction note. | Recommended |
| H9 | **Rounding and money:** hours to the minute, shown to 2 decimals; pay rounded to the centavo once per person (never per shift). Money `numeric(12,2)`, hours `numeric(8,2)`. Audit: `payroll.salary_set` now records old → new pay type and rate; new `payroll.hours_adjusted` / `payroll.hours_adjustment_removed`. | Recommended |
| H10 | **Out of scope for now:** paper DTR comparison, automatic overtime / holiday / rest day / night differential, SSS / PhilHealth / Pag-IBIG / tax, Draft → For review → Approved steps, monthly salary proration, staff seeing their own payslip. These need the gym's accountant to set the rules; until then, Bonus/Deduction lines with notes cover them (P5). | Recommended |
| H11 | **Mark as paid with no rate:** someone Hourly or Per session with 0 hours/sessions gets ₱0 and is listed; someone with no pay set stays "No salary set" (P2). `net_below_zero` still refuses. | Recommended |

## The Payroll page changes (impeccable brief)

Run `impeccable` before the XAML and again before the commit. Same **operate mode** as Part 3: an admin closing a pay run twice a month at a desk. Keep the existing page, components and sage/lime look; this is a change to existing tables, not a new page.

- **Salaries tab:** each row gets a **Pay type** (Fixed / Hourly / Per session, segment or picker; Per session only for coaches) and the amount label follows it: "₱8,000.00 per period", "₱80.00 per hour", "₱150.00 per session". **Set pay** edits type + amount inline (Save, Cancel). The role defaults at the top get the same pair.
- **Pay period table:** the Hours column becomes the **basis**: "41.50 h × ₱80.00" / "12 sessions × ₱150.00" / "Fixed". Corrected hours show a small "(+1.00 h)" after them. Salary column renamed **Base pay**. A flag chip (H6) on rows with open, auto-closed or very long records. Money right-aligned, tabular figures, as today.
- **Drawer:** the basis line ("Logged 40.50 h · corrections +1.00 h · payable 41.50 h × ₱80.00 = ₱3,320.00"), the flagged records with date and times, **Add hours correction** (± hours + note; ± sessions for Per session) next to Add bonus / Add deduction, remove on each correction while draft. Read-only once paid.
- **Mark as paid confirm:** adds "3 records flagged" with the names, like the no-salary names today.
- **PDF:** each payslip block shows the basis line and the corrections with notes.
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
5. **Per-session coach** → paid by completed sessions only; no-shows, cancelled and unpaid held add-on sessions not counted; check-in hours ignored.
6. **Add or remove a correction on a paid period** → `payroll_paid`, nothing changes.
7. **Old paid periods from Part 3** still show and export the same totals.

## Phase 1: Database (H1-H9, H11)

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
  - `payroll_mark_paid(p_period)`: same locks and refusals; freezes `base_pay` with `pay_type`, `quantity`, `rate`.
  - The basis maths lives in one place (a private helper function both `payroll_period` and `payroll_mark_paid` call), so the draft and the frozen line can't disagree.
- App: `IAdminService` + `Services.cs`, `Models.cs` (`PayrollRow`, `StaffSalary` gain pay type, rate, logged, adjustment, payable, flags; new `PayrollHourAdjustment`), `Foundation.cs` sentences (`per_session_coach_only`), `DemoPayroll.cs` mirror.
- Tests: `supabase/tests/local/payroll.mjs` (extend), `tests/GymMembership.Tests/PayrollTests.cs`.

- [ ] Failing tests first: Review Focus 1-7; existing salaries read as Fixed with the same totals; per session refused for a front desk person; correction without a note refused; auto-closed visit counted and flagged; a shift over 12 h flagged; pay rounded once per person (e.g. 3 shifts of 20 min at ₱80 = ₱80.00).
- [ ] Migration, dry-run push, push live, live grants checked (anon and non-admin refused), `ponytail:ponytail-review`, commit, push branch.

## Phase 2: Payroll page

### Task 2: Salaries tab, period table, drawer, PDF

**Files:** `Views/PayrollPage.xaml` (+ `.xaml.cs`), `PayrollViewModel` in `StaffViewModels.cs` (pay type editing, basis text, flags, add/remove hours), `PayrollPdf` in `RevenueReport.cs`, `PayrollTests.cs` (basis text per pay type; corrections shown; paid period hides Add hours; confirm names flagged people; the PDF payslip lists the basis and corrections).

- [ ] `impeccable` before the XAML (brief above); build; check with `impeccable` (critique and audit) in light and dark on Windows at narrow and wide widths; fix what it finds.
- [ ] View model tests green; Windows build; `ponytail:ponytail-review` on the diff; commit, push branch.

## Phase 3: Checks

- [ ] Whole suite (`dotnet test`, Windows & Android builds, `payroll.mjs`) green.
- [ ] `superpowers:requesting-code-review` on the branch diff; fixes committed.
- [ ] Owner hand checks (Windows): set a front desk person to Hourly ₱80 and a coach to Per session ₱150, open the current period, check the hours against the cash shifts, add a correction, export the PDF, mark a past period paid, change a rate and confirm the paid period didn't change.

## Session log

- 2026-11-24: plan written from the owner's `hourly_based_payroll_recommendation_v2.md`, cut down to its section 13 first implementation on top of Part 3 payroll. Branch `hourly-payroll-2026-11-24` cut from `origin/main` (`a464366`). H1-H11 recommended, waiting on the owner. Not built. Next migration `0142`.
