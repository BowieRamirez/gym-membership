---
target: "Phase 3: cash-drawer shift (Cashier, Revenue cash shifts, Gym settings float)"
total_score: 32
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\CashierPage.xaml"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\CashierPage.xaml"
timestamp: 2026-11-10T00-05-00Z
slug: src-gymmembership-app-views-cashierpage-xaml
---
Method: design review only (detector not applicable, native platform)

Scope: Plan 2026-11-09, Phase 3, built to the approved mockup docs/mockups/cash-shift.html. Staff pages, Windows only: Cashier (start, different float with note, open drawer, ending short, ending matches, ended) at 1600 dark and 1100 light; Revenue with the Cash shifts table (1600 dark, table) and cards (1100 light, narrow); Gym settings with the opening float (both).

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | The drawer strip shows since when, float, taken, paid back and expected at all times |
| 2 | Match System / Real World | 4 | Float, drawer, counted, over / short: the cashier's own words |
| 3 | User Control and Freedom | 3 | Keep shift open backs out of ending; a mistyped float can only be corrected by a note at the end |
| 4 | Consistency and Standards | 3 | Next-step outline as the Check-in PIN step (ink in light, yellow in dark); the table folds to cards like every other table |
| 5 | Error Prevention | 4 | Complete payment waits for a shift when Cash is chosen; a note is required only when the float or count differs; End shift confirms |
| 6 | Recognition Rather Than Recall | 4 | Expected stays on screen while counting; the difference shows as you type |
| 7 | Flexibility and Efficiency | 3 | Enter starts and ends the shift from the amount boxes |
| 8 | Aesthetic and Minimalist Design | 3 | One panel replaces "Net cash today" rather than adding to it |
| 9 | Error Recovery | 3 | "Start your cash shift on the Cashier page first" names the fix |
| 10 | Help and Documentation | 2 | One caption per new field; nothing explains how a voided cash payment changes expected |
| **Total** | | **32/40** | **Good** |

## Design Specificity Verdict

Built in the incumbent yellow-and-black world from the approved mockup. The product-specific moment is the count: expected in large numerals, the typed count beside it, and over / short in words and colour.

## Priority Issues

- Fixed (P2): "Over / short" and "Note" headers ran together on Revenue; the note column now has its own gap.
- Fixed (P2): the Start-shift outline was yellow on a near-white page in light theme (barely visible); now ink in light, yellow in dark.
- Fixed (P3): "Counted ₱5,000.00: Matches the expected cash." read oddly; a matching count now says "Your count matches the expected ₱5,000.00."
- Fixed (P3): Gym settings' opening-hours caption sat directly under the float caption; spaced apart.
- Open (P3): at 1100 px the order panel stacks under the choices, so "Start your shift above to take cash" is below the fold; the Start-shift card is at the top, so the cause is still in view.

## Minor Observations

- An open shift's row on Revenue shows "–" for counted and over / short, muted, with a yellow "Open" chip.
- The cashier's role is not shown on Revenue (the mockup had "Front desk" under the name); the list function returns the name only.
