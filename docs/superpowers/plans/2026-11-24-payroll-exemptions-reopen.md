# Payroll exemptions, non-taxable pay, reopen before payout, retro pay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (owner chose native execution for the last plans) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

Created: 2026-11-24
Last updated: 2026-11-24
Status (2026-11-24): U1-U12 confirmed (as recommended); Phases 1-2 built, reviewed and fixed; `0145` live once pushed (see Session log); owner hand checks left. Not merged.
Branch: `hourly-payroll-2026-11-24` (same branch as `2026-11-24-hourly-payroll.md` and `2026-11-24-payroll-deductions.md`). **Owner's rule: not merged into `main` yet.** No PR until the owner asks.

**Goal:** Close the four gaps the owner's payroll rules showed against what was built (2026-11-24 comparison):
1. **Separate switches** for government contributions and for withholding tax, so a minimum wage earner is tax-exempt but still pays SSS, PhilHealth and Pag-IBIG.
2. **Non-taxable pay:** a 13th month / other benefits category, tax-free up to ₱90,000 a year (the app keeps the yearly total), and a de minimis category that is never taxed.
3. **Reopen before payout:** Approve locks a period; it can be reopened (with a reason) until it is **marked as paid out**, which locks it for good.
4. **Retro pay / adjustment** category for corrections after payout.

**Architecture:** Builds on `0142`-`0144`. Migration `0145`. The pay maths stays in `payroll_basis` (taxable pay and contribution bases learn about the new categories; the switches split). The period's existing `paid_at` keeps meaning "locked and frozen" (renamed **Approved** on screen), and a new `paid_out_at` means the money was sent, so every lock check, freeze and test written so far keeps working; Reopen undoes an approval while `paid_out_at` is empty.

**Tech Stack:** Supabase (Postgres migrations), .NET MAUI (Core + App), CommunityToolkit.Mvvm, xUnit, Node database tests.

**Source:** owner, 2026-11-24: pasted payroll rules (locking after payout; reopen before bank disbursement; retro pay in the next cutoff; taxable income = gross − contributions − non-taxable/de minimis; MWE exemption; 13th month and other benefits exempt up to ₱90,000; de minimis limits per BIR RR 29-2025) and "yes plan 1-4". Year-end annualisation and BIR 2316 stay out (U12).

## How to use this plan

Same rules as `2026-11-24-payroll-deductions.md`: decisions first (nothing built until the owner confirms U1-U12); one phase at a time; **skills every phase:** `ponytail` full, `superpowers:test-driven-development` (failing test first, watched failing), `superpowers:systematic-debugging` for bugs, `impeccable` (+ `frontend-design`) for the Payroll page (Windows only, light and dark), `ponytail:ponytail-review` before each commit, `superpowers:verification-before-completion`, a fresh whole-branch review at the end (`superpowers:requesting-code-review`). Push each phase's migration live when done (dry run first) and check live grants. The owner tests the app themself.

| Phase | What | Needs | Status |
|---|---|---|---|
| 1 | Database: two switches, non-taxable categories with the yearly ₱90,000, retro category, Approve / Reopen / Paid out | U1-U12 | Done (`2bf837f` + review fixes) |
| 2 | Payroll page: two switches, new categories, Approve / Reopen / Mark as paid out, statuses, payslip | U1-U12 | Done (`2bf837f` + review fixes) |
| 3 | Checks, review, owner hand checks | | Review done and fixed; owner hand checks left |

## 1. Findings (2026-11-24)

| Item | Today | Consequence |
|---|---|---|
| Switch | `payroll_no_deductions` (0144): one row = no SSS, PhilHealth, Pag-IBIG **and** no tax. | Split into two flags; existing rows keep both off (no change for anyone already switched off). |
| Taxable pay | `payroll_basis`: taxable = gross − contributions taken that half. Every bonus is taxed. | Taxable also subtracts the non-taxable part of the new categories (U4, U5). |
| Contribution base | SSS and Pag-IBIG on the month's gross (base + bonuses), PhilHealth on base pay. | 13th month and de minimis are not "compensation" for SSS/Pag-IBIG: leave them out of that base (U6). |
| Lock | `paid_at` set by `payroll_mark_paid` freezes salary + government lines; every edit checks it (`payroll_draft`). | Keep `paid_at` as "approved/locked"; add `paid_out_at`; Reopen clears `paid_at` and the frozen lines only while `paid_out_at` is empty. |
| Categories | `regular_bonus / overtime / holiday / other` + `sss / philhealth / pagibig / tax` (0144). | Add `thirteenth_month`, `de_minimis`, `retro`. |

## 2. Decisions

All recommended by Claude, **to confirm by the owner**.

| # | Decision | Answer |
|---|---|---|
| U1 | **Two switches per person** on the Salaries tab: **Contributions** (SSS, PhilHealth, Pag-IBIG) on/off and **Withholding tax** on/off, both on by default. A minimum wage earner = contributions on, tax off. People already switched off in `0144` start with both off. Own switch refused (`own_salary`); audited (`payroll.deductions_set` with which switch, old → new). | Recommended |
| U2 | **Why off** (optional short reason shown as a tag, e.g. "Minimum wage earner", "Contractor") so the accountant sees why tax isn't taken. | Recommended |
| U3 | **New categories:** Bonus **13th month / other benefits** (`thirteenth_month`), Bonus **De minimis** (`de_minimis`), and **Retro pay / adjustment** (`retro`, bonus or deduction, taxable). | Recommended |
| U4 | **13th month / other benefits** are tax-free up to **₱90,000 per person per calendar year**: the app adds up that person's `thirteenth_month` lines in approved periods earlier in the same year plus this period, and only the part above ₱90,000 is taxable. The limit is in the rates table (`thirteenth_month_exempt 90000`). | Recommended |
| U5 | **De minimis** lines are never taxed. The app doesn't check each benefit's own limit (rice ₱2,500/month, laundry ₱400/month, uniform ₱8,000/year, medical ₱12,000/year, gifts ₱6,000/year, leave 12 days/year); the Add form shows these limits as a hint and the admin keeps within them. | Recommended |
| U6 | **Contribution base:** SSS and Pag-IBIG on the month's gross **minus** 13th month and de minimis lines; PhilHealth stays on base pay. Retro pay counts as compensation. | Recommended |
| U7 | **Approve locks; Paid out finishes.** Today's **Mark as paid** becomes **Approve** (same checks and freeze: salary and government lines written, status "Approved, not paid out"). A new **Mark as paid out** (after the money is sent) locks it for good. Old periods already paid count as paid out. | Recommended |
| U8 | **Reopen** an Approved (not paid out) period with a required reason: the frozen salary and government lines are removed, the period becomes a Draft again with every approval, correction and manual line kept, and the pay is worked out again. Audited (`payroll.reopened`, reason). A paid-out period has no Reopen; corrections go in the next period as Retro pay (U3). | Recommended |
| U9 | **Order rules:** a 16th–end period needs its 1st–15th **approved** (as today); a 1st–15th can't be reopened while the same month's 16th–end is approved (its contributions were worked out from it): `second_half_approved`. Paid out needs Approved first. | Recommended |
| U10 | **On the page:** status chips Draft / Approved (not paid out) / Paid out; buttons **Approve** (draft), **Reopen** and **Mark as paid out** (approved), nothing on paid out; the confirm texts say which step is final. Payslip PDF says "Approved, not yet paid out" or "Paid out on …". | Recommended |
| U11 | **Released app (0.10.0):** its "Mark as paid" now approves (it calls the same function), so periods it pays can still be reopened in the new app; it can't mark paid out. Its totals stay right. | Recommended |
| U12 | **Left out:** year-end annualisation and BIR 2316, employer shares, remittance reports, per-benefit de minimis limit checks, minimum wage rules for overtime/holiday/night pay (tax off covers the whole person). | Recommended |

## The Payroll page changes (impeccable brief)

Operate mode, same page and components; run `impeccable` before the XAML and before the commit.

- **Salaries tab:** each row shows "Contributions on · Tax on" with **Change** opening two small switches and an optional reason ("Minimum wage earner"). Off states show a quiet tag ("Tax-exempt: Minimum wage earner").
- **Add bonus or deduction:** categories grouped: Regular bonus, Overtime, Holiday pay, **13th month / other benefits** (hint: "Tax-free up to ₱90,000 a year; ₱x used this year"), **De minimis** (hint lists the limits), **Retro pay / adjustment**, Other.
- **Details panel:** Withholding tax line shows "on ₱x taxable (₱y non-taxable)".
- **Period header and actions:** status chip Draft / Approved · not paid out / Paid out 25 Nov by Ana; actions Approve → (Reopen, Mark as paid out); Reopen asks for a reason; Mark as paid out confirm: "Only do this after the money is sent. Paid-out periods can't be reopened."
- **Anti-goals:** no year-end screens, no per-benefit trackers.

## Global Constraints

- Everything in the deductions plan's Global Constraints, plus: migrations from `0145`. Recreated functions re-revoke and re-grant (live default privileges). Money `numeric(12,2)`.
- Demo gateway mirrors every rule (`DemoPayroll.cs`).

## Review Focus

1. **Tax off, contributions on** (minimum wage earner) → SSS/PhilHealth/Pag-IBIG taken, tax ₱0; contributions off, tax on → tax on gross; both off → nothing; 0144 rows → both off.
2. **13th month across the year:** ₱60,000 approved in June, ₱40,000 in December → ₱10,000 of December's is taxable; a draft's own 13th month counts but other drafts don't; a new year starts at ₱0.
3. **De minimis** never taxed and not in the SSS/Pag-IBIG base; **retro** taxed and in the base.
4. **Approve → Reopen → edit → Approve** gives new frozen lines and no duplicates; Reopen without a reason refused; Reopen after paid out refused; Reopen 1st–15th while 16th–end approved refused.
5. **Mark as paid out** only on Approved; afterwards nothing can change (approvals, lines, corrections, reopen).
6. **Released app** call of `payroll_mark_paid` still approves; old paid periods read as paid out.
7. **Non-admins / anon** refused on every new function.

## Phase 1: Database (U1-U12)

### Task 1

**Files:** migration `0145_payroll_exemptions_reopen.sql`: `payroll_no_deductions` → add `contributions_off boolean`, `tax_off boolean`, `reason text` (existing rows both true; a row with both false is deleted); `payroll_deduction_rates.thirteenth_month_exempt`; categories + `thirteenth_month / de_minimis / retro`; `payroll_basis` (switches, U4-U6); `payroll_periods.paid_out_at / paid_out_by` (backfill = `paid_at`/`paid_by`); `payroll_reopen(p_period, p_reason)`, `payroll_mark_paid_out(p_period)`; `set_payroll_deductions(p_user, p_contributions, p_tax, p_reason)` (old 2-argument call kept as a wrapper for the released app); `payroll_staff` / `payroll_period` new fields (`contributions_on`, `tax_on`, `deductions_reason`, `nontaxable`, `status`, `paid_out_at`, `paid_out_by_name`); `payroll_mark_paid` unchanged in meaning (approve). App: services, models, `Foundation.cs` sentences (`second_half_approved`, `not_approved`, `paid_out`), demo mirror. Tests: `supabase/tests/local/payroll-exemptions.mjs`, `tests/GymMembership.Tests/PayrollExemptionsTests.cs`.

- [x] Failing tests first: Review Focus 1-7.
- [ ] Migration, dry-run push, push live, live grants checked, `ponytail:ponytail-review`, commit, push branch.

## Phase 2: Payroll page

### Task 2

**Files:** `PayrollPage.xaml`, `PayrollViewModel` and items in `StaffViewModels.cs`, `PayrollPdf`, tests (two switches and reason; category hints incl. "₱x used this year"; status chip and buttons per status; Reopen reason required; paid-out confirm text; payslip status line).

- [x] `impeccable` before the XAML; build; Windows light and dark, narrow and wide; fix findings.
- [x] View model tests green; Windows build; `ponytail:ponytail-review`; commit, push branch.

## Phase 3: Checks

- [x] Whole suite (`dotnet test`, Windows & Android builds, DB suite) green.
- [x] `superpowers:requesting-code-review` on the branch diff; fixes committed.
- [ ] Owner hand checks (Windows): set one person "tax off, minimum wage earner"; add a 13th month bonus over ₱90,000 across two periods; add a de minimis and a retro line; Approve, Reopen with a reason, change something, Approve again, Mark as paid out; confirm the payslip and that a paid-out period can't be reopened.

## Session log

- 2026-11-24: owner compared the built payroll with their payroll rules; gaps: one all-or-nothing switch (minimum wage earners), no non-taxable pay (13th month ₱90,000, de minimis), no reopen before payout, no retro category. Owner: "yes plan 1-4". Plan written; U1-U12 recommended, waiting on the owner. Not built. Next migration `0145`.
- 2026-11-24: owner: U1-U12 as recommended, start building. Built `0145_payroll_exemptions_reopen.sql` and the app (`2bf837f`): two switches with a reason, 13th month (₱90,000 a year) / de minimis / retro categories, contribution base without 13th month and de minimis, Approve → Reopen (reason) → Mark as paid out, status chip and buttons, category hints, payslip status line. Checked in a demo copy (no live data): two switches ("Contributions on · Tax off (Minimum wage earner)"), Approve confirm wording, first_half_unpaid refusal, Approved panel with Reopen and Mark as paid out.
- 2026-11-24: fresh review, no Critical. Fixed in `0145` before it went live: a Retro deduction now lowers taxable pay and the SSS / Pag-IBIG base; the ₱90,000 13th-month cap counts every other approved period of the year, so approving out of order or reopening can't beat it; Reopen locks the month's 16th–end row against a simultaneous Approve; switches log real changes only; the 13th month hint follows the open person's numbers; the unused one-click switch was removed; first_half_unpaid says "Approve … first". Noted, not changed: an approved period's panel shows today's non-taxable figure (frozen amounts never change); the released 0.10.0 app's single switch overwrites a "tax off only" setting; Reopen has no confirm dialog (the required reason stands in). Next migration `0146`.
- 2026-11-24: `0145` pushed live (dry run first, only `0145`). Live grants checked: every new or recreated payroll function refuses anon; `payroll_basis` refuses everyone; client functions signed-in only (admin check inside); both `set_payroll_deductions` versions present. Periods paid before this release read as paid out (0 approved-not-paid-out). Unit 1284/1284, DB suite 767 ok (exemptions 11/11), Windows build. Next migration `0146`.
