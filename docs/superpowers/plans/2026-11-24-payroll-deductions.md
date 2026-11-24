# Government deductions on Payroll (SSS, PhilHealth, Pag-IBIG, withholding tax) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-24
Last updated: 2026-11-24
Status (2026-11-24): plan written; T1-T12 recommended, **waiting on the owner to confirm**. Not built.
Branch: `hourly-payroll-2026-11-24` (same branch as the hourly payroll it builds on, `docs/superpowers/plans/2026-11-24-hourly-payroll.md`). **Owner's rule: not merged into `main` yet.** No PR until the owner asks.

**Goal:** Payroll works out each person's employee share of **SSS, PhilHealth and Pag-IBIG** and their **BIR withholding tax**, shows them as their own lines on the page, the details panel and the payslip, and freezes them when a period is marked paid. A switch per person turns them off (contractors, exempt staff). This is the part H10 of the hourly plan left out.

**Architecture:** Builds on `0142`/`0143`. One rates table holds today's legal numbers (no rates in code). The pay maths stays in one place: `payroll_basis` gains the deduction columns, so the draft, Mark as paid, the page and the PDF agree. Contributions are monthly amounts charged in the **16th–end** period on the whole month's pay (owner chose option 1); tax is charged every period. Draft deductions are computed, not stored; Mark as paid writes them as `deduction` lines with their own categories, so paid periods never change.

**Tech Stack:** Supabase (Postgres migrations), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** owner, 2026-11-24: "add the tax that's left out in H10", then: tax **and** SSS/PhilHealth/Pag-IBIG; on/off per person; option 1 (whole month's contributions in the second half). Rates checked 2026-11-24 against several published 2026 guides (not the official circulars): SSS 15% of the monthly salary credit (MSC), employee 5%, MSC ₱5,000-₱35,000; PhilHealth 5%, employee 2.5%, floor ₱10,000, ceiling ₱100,000; Pag-IBIG employee 2% (1% at ₱1,500 or less) on at most ₱10,000; BIR semi-monthly table (RR 11-2018 Annex E, TRAIN rates from 2023). **The gym's accountant should confirm these before the first real payroll.**

## How to use this plan

Same rules as `2026-11-24-hourly-payroll.md`:

- Decisions first. Nothing is built until the owner confirms T1-T12. One phase at a time, in order; each phase ends working and committed.
- **Skills, every phase:**
  - `ponytail` full mode for the whole session (smallest diff, reuse before writing).
  - `superpowers:test-driven-development` for every rule: the failing test first, watched failing. Every bracket edge gets a test (₱10,417 tax line, ₱5,250 SSS step, ₱10,000 PhilHealth floor, ₱1,500 Pag-IBIG tier, the caps).
  - `superpowers:systematic-debugging` for any bug met on the way.
  - `impeccable` (and `frontend-design` per CLAUDE.md) for every screen change. Payroll is a staff page: **Windows only**, light and dark.
  - `ponytail:ponytail-review` on each phase's diff before its commit.
  - `superpowers:verification-before-completion` before any "done".
  - A fresh whole-branch review at the end (`superpowers:requesting-code-review`).
- Push each phase's migrations live when the phase is finished (dry run first) and check live grants (`supabase-recreated-function-grants`). Commits pushed to `origin/hourly-payroll-2026-11-24`; nothing merges into `main`.
- The owner tests the app themself: unit tests, DB tests and builds; live checks only when asked (as in the hourly plan's live test: throwaway staff, an old period).

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Database: rates table, per-person switch, deductions in the pay maths, frozen on paid | T1-T12 | Not started |
| 2 | Payroll page: switch on Salaries, deduction lines in the table, details panel, confirm and payslip | T1-T12 | Not started |
| 3 | Checks, review, owner hand checks | | Not started |

## 1. Findings (2026-11-24)

| Item | Today | Consequence |
|---|---|---|
| Pay maths | `payroll_basis` (0142/0143) returns base pay, bonuses, gross, deductions (manual lines only), net per person; `payroll_period` and `payroll_mark_paid` read it. | Add the deduction columns there; everything else follows. |
| Manual deductions | `payroll_lines` kind `deduction` with categories `regular_bonus / overtime / holiday / other` (H16). | Frozen government deductions become `deduction` lines with new categories `sss / philhealth / pagibig / tax`; the Add form never offers them. |
| Periods | 1st–15th and 16th–end, Manila dates; paid periods locked. | The 16th–end period reads the same month's 1st–15th (frozen lines if paid, the draft basis if not). |
| Roles | Front desk, coach, and admins with a salary (P2). | The switch is per person on the Salaries tab; default on. |
| Old periods | Paid periods from Part 3 and the hourly test (e.g. 1–15 Jan 2025) have only manual lines. | Paid periods are never recomputed (T9). |

## 2. Decisions

All recommended by Claude, **to confirm by the owner** (T1 and T4 already chosen by the owner).

| # | Decision | Answer |
|---|---|---|
| T1 | **Contributions once a month, in the 16th–end period**, on the whole month's pay: the 1st–15th period's figures plus this period's. 1st–15th payslips show no SSS / PhilHealth / Pag-IBIG. | Owner, 2026-11-24 (option 1) |
| T2 | **What each is based on (monthly):** SSS and Pag-IBIG on the month's **gross** (base pay + bonuses); PhilHealth on the month's **base pay** ("basic salary"). Employee shares only: **SSS** 5% of the MSC (pay below ₱5,250 → MSC ₱5,000; then ₱500 steps, x,250-x,749.99 → x,500; ₱34,750 and up → ₱35,000) = ₱250-₱1,750; **PhilHealth** 2.5% of base pay held between ₱10,000 and ₱100,000 = ₱250-₱2,500; **Pag-IBIG** 1% if the month's pay is ₱1,500 or less, else 2%, on at most ₱10,000 = up to ₱200. | Recommended |
| T3 | **Withholding tax every period** on that period's **taxable pay** = gross − the contributions deducted in that period, using the BIR semi-monthly table: ≤ ₱10,417 → ₱0; ₱10,417-₱16,667 → 15% of the excess over ₱10,417; ₱16,667-₱33,333 → ₱937.50 + 20% over ₱16,667; ₱33,333-₱83,333 → ₱4,270.70 + 25% over ₱33,333; ₱83,333-₱333,333 → ₱16,770.70 + 30% over ₱83,333; above → ₱91,770.70 + 35% over ₱333,333. Rounded to the centavo. | Recommended |
| T4 | **Switch per person: "Government deductions" on/off** on the Salaries tab, on by default (new staff too). Off = no SSS, PhilHealth, Pag-IBIG or tax for that person. An admin can't switch their own (`own_salary`, as P2). Audited (`payroll.deductions_set`, old → new). | Owner, 2026-11-24 |
| T5 | **Rates in one table** (`payroll_deduction_rates`, one row: SSS rate and MSC limits/step, PhilHealth rate/floor/ceiling, Pag-IBIG rates/tier/cap, the tax brackets as JSON, `effective_from`, source note), filled with the 2026 numbers above. Changed by a migration when the law changes; the Salaries tab shows a one-line "Rates used" note. No edit screen now (can be added later). | Recommended |
| T6 | **Draft vs paid:** on a draft the deductions are computed live (they follow pay changes and approvals); **Mark as paid** writes them as `deduction` lines (categories `sss`, `philhealth`, `pagibig`, `tax`, note e.g. "SSS, Nov 2026, MSC ₱13,000") so a paid period never changes. Manual lines can't use these categories (`bad_input`). | Recommended |
| T7 | **The 16th–end period waits for the 1st–15th:** Mark as paid on a 16th–end period is refused while the same month's 1st–15th is unpaid and anyone on it has deductions on (`first_half_unpaid`), so the month total is final. | Recommended |
| T8 | **Never below ₱0:** government deductions are taken in the order SSS, PhilHealth, Pag-IBIG, tax, each only up to what is left of gross after the manual deductions; anything cut is shown as a warning "Deductions cut to keep pay above ₱0" on the row and in the details panel (`net_below_zero` stays for manual lines). | Recommended |
| T9 | **Who and when:** every pay type (Fixed, Hourly, Per session) with deductions on; only **draft periods** from this release on. Paid periods are never recomputed; a 16th–end draft whose 1st–15th was paid before this release uses that period's frozen gross/base pay. | Recommended |
| T10 | **On the page:** the table's Deductions column becomes manual + government; the row shows a small "Gov't ₱x" line under it; the details panel lists SSS, PhilHealth, Pag-IBIG and Withholding tax with what each was based on; the period header adds "Gov't deductions ₱x"; the Mark as paid confirm names the period's total; the payslip PDF lists each government line between Gross and Net. | Recommended |
| T11 | **Left out on purpose:** employer shares (not taken from pay) and remittance reports/forms (R-3, RF-1, MCRF, BIR 1601-C, 2316); 13th-month and de minimis tax exemptions (₱90,000 ceiling); year-end annualised tax adjustment; minimum-wage exemption rules (use the switch); SSS for coaches paid as contractors (use the switch). | Recommended |
| T12 | **Released app (0.10.0):** it keeps working: government deductions are included in each row's `deductions` and `net`, so its totals are right; only the new app shows them line by line. | Recommended |

## The Payroll page changes (impeccable brief)

Run `impeccable` before the XAML and again before the commit. **Operate mode**, same page and components as the hourly plan; no new visual style.

- **Salaries tab:** each row gets a compact **Gov't deductions** switch (On / Off) next to the pay, with its label for screen readers; off rows show a quiet "No deductions" tag. Under Role defaults, one caption line: "SSS, PhilHealth and Pag-IBIG are taken once a month in the 16th–end pay; tax every pay. Rates from 2026 tables, confirm with your accountant."
- **Pay period table:** Deductions shows manual + government; under it a caption "Gov't ₱1,234.50" when there is any. A "Deductions cut" warning chip (T8). The header adds "Gov't deductions ₱x" beside Gross.
- **Details panel:** a "Government deductions" block after Bonuses and deductions: each line with its amount and what it was based on ("SSS · MSC ₱13,000", "PhilHealth · ₱12,400 base pay", "Pag-IBIG · ₱10,000", "Withholding tax · ₱6,120.50 taxable"), or "Off for this person" / "Taken in the 16th–end pay" on a 1st–15th period. Read-only (the switch lives on Salaries).
- **Mark as paid:** the confirm adds "Government deductions ₱x"; a `first_half_unpaid` refusal names the 1st–15th period with a link to step to it.
- **PDF payslip:** Base pay, bonuses, Gross pay, manual deductions, then SSS, PhilHealth, Pag-IBIG, Withholding tax, then Net pay.
- **Anti-goals:** no rate editing screen, no employer-share columns, no tax forms.

## Global Constraints

- Everything in `2026-11-24-hourly-payroll.md` Global Constraints, plus: migrations from `0144`. Every new or recreated function revokes `public, anon, authenticated` (helpers from everyone) and grants `authenticated` where clients call it, gated by `require_permission('reports:revenue')`. Check live grants after the push.
- Money `numeric(12,2)`, rounded to the centavo once per line.
- Demo gateway mirrors every rule (`DemoPayroll.cs`).

## Review Focus

1. **Bracket edges:** taxable ₱10,417.00 → ₱0; ₱10,417.01 → ₱0.00 (rounds); ₱16,667 → ₱937.50; monthly pay ₱5,249.99 / ₱5,250 / ₱34,750 → MSC ₱5,000 / ₱5,500 / ₱35,000; base pay ₱8,000 → PhilHealth ₱250 (floor); ₱120,000 → ₱2,500 (ceiling); pay ₱1,500 → Pag-IBIG 1% = ₱15; ₱20,000 → ₱200 (cap).
2. **1st–15th period:** tax only, no contributions; 16th–end: whole month's contributions + its own tax, with taxable = its gross − those contributions.
3. **Hourly person approves more hours after the 1st–15th draft** → the 16th–end draft's contributions follow; once the 1st–15th is paid they use its frozen figures.
4. **Mark as paid 16th–end while 1st–15th is unpaid** (someone with deductions on) → `first_half_unpaid`, nothing written.
5. **Switch off** → no government lines in draft or on paying; switching back on changes drafts only; an admin can't switch their own.
6. **Low pay** (₱500 gross, deductions on) → government deductions cut so net ≥ ₱0, warning shown, Mark as paid succeeds.
7. **Paid period** → frozen `sss / philhealth / pagibig / tax` lines; changing rates, pay or the switch afterwards changes nothing; manual Add with those categories refused.
8. **Released app shape** → `deductions` and `net` include government deductions; old paid periods unchanged.
9. **Front desk, coach, member, anon** → can't read rates or the switch table, can't call the switch function.

## Phase 1: Database (T1-T12)

### Task 1: Rates, switch, deductions in the pay maths

**Files:** migration `0144_payroll_deductions.sql`:
- `payroll_deduction_rates` (single row, `id boolean primary key default true check (id)`): `sss_rate 0.05`, `sss_msc_min 5000`, `sss_msc_max 35000`, `sss_msc_step 500`, `philhealth_rate 0.025`, `philhealth_floor 10000`, `philhealth_ceiling 100000`, `pagibig_low_rate 0.01`, `pagibig_rate 0.02`, `pagibig_low_limit 1500`, `pagibig_cap 10000`, `tax_brackets jsonb` (semi-monthly: `[{over, base, rate}]`), `effective_from date`, `source text`. RLS on, no grants; read through a function.
- `payroll_no_deductions` (`user_id` pk → profiles, `set_by`, `set_at`). RLS on, no grants.
- `payroll_categories`: allow `sss / philhealth / pagibig / tax` on `payroll_lines.category` (drop and re-add the check); `payroll_add_line` refuses them.
- Private helpers: `payroll_sss(monthly numeric)`, `payroll_philhealth(base numeric)`, `payroll_pagibig(monthly numeric)`, `payroll_tax(taxable numeric)` (immutable math reading the rates row).
- `payroll_basis`: add `deductions_on`, `sss`, `philhealth`, `pagibig`, `tax`, `gov_total`, `gov_cut boolean`; month figures for a 16th–end period from the same month's 1st–15th (`payroll_basis` of that period if draft, its frozen lines if paid); apply T8; `deductions` and `net` include government amounts (T12). Paid periods: read the frozen lines (T9).
- `payroll_period`: return the new fields and `total_gov`; `payroll_records`/others unchanged.
- `payroll_mark_paid`: `first_half_unpaid` (T7); insert the four lines per person (non-zero only) with notes (T6).
- `set_payroll_deductions(p_user uuid, p_on boolean)` (+ audit), `payroll_staff()` returns `deductions_on`, `payroll_deduction_rates_info()` returns the rates row for the caption.
- App: `IAdminService` + `Services.cs`, `Models.cs` (`PayrollRow` gov fields, `StaffSalary.DeductionsOn`, category labels), `Foundation.cs` sentence (`first_half_unpaid`), `DemoPayroll.cs` mirror (same math, one shared C# copy of the rates).
- Tests: `supabase/tests/local/payroll-deductions.mjs`, `tests/GymMembership.Tests/PayrollDeductionsTests.cs`.

- [ ] Failing tests first: Review Focus 1-9.
- [ ] Migration, dry-run push, push live, live grants checked, `ponytail:ponytail-review`, commit, push branch.

## Phase 2: Payroll page

### Task 2: Salaries switch, table, details panel, confirm, PDF

**Files:** `Views/PayrollPage.xaml`, `PayrollViewModel` and items in `StaffViewModels.cs`, `PayrollPdf` in `RevenueReport.cs`, tests in `PayrollDeductionsTests.cs` (switch saves and reloads; row caption and warning; panel lines and "Taken in the 16th–end pay" on a 1st–15th; confirm total; `first_half_unpaid` sentence and step link; payslip lines in order).

- [ ] `impeccable` before the XAML (brief above); build; check on Windows light and dark at narrow and wide widths; fix what it finds.
- [ ] View model tests green; Windows build; `ponytail:ponytail-review`; commit, push branch.

## Phase 3: Checks

- [ ] Whole suite (`dotnet test`, Windows & Android builds, DB suite) green.
- [ ] `superpowers:requesting-code-review` on the branch diff; fixes committed.
- [ ] Owner hand checks (Windows): open a 1st–15th draft (tax only), a 16th–end draft (month's contributions + tax), switch one person off, try paying the 16th–end before the 1st–15th, pay both, export the PDF and compare a payslip with the accountant's own computation.

## Session log

- 2026-11-24: owner asked to add the tax H10 left out; chose tax + SSS/PhilHealth/Pag-IBIG, on/off per person, and option 1 (whole month's contributions in the 16th–end period). Rates checked against published 2026 guides (sources in the chat; accountant to confirm). Plan written on `hourly-payroll-2026-11-24`. T1-T12 recommended, waiting on the owner. Not built. Next migration `0144`.
