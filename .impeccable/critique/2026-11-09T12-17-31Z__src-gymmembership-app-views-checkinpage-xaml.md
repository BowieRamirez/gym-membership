---
target: "Phase 2: desk PIN, member pass, settings, check-ins"
total_score: 31
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\CheckInPage.xaml"
target_fingerprint: "sha256:8bc2e7c8cfa7108029f81f96180e543c5a416bf400304ef7f0ecd44a0eaad18e"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\CheckInPage.xaml"
timestamp: 2026-11-09T12-17-31Z
slug: src-gymmembership-app-views-checkinpage-xaml
---
Method: design review only (detector not applicable, native platform)

Scope: Plan 2026-11-09, Phase 2, built to the approved mockup docs/mockups/desk-pin.html. Desk Check-in PIN step (Windows only; front desk 1600 dark and 1100 light, System admin 1100 light with the reason field), member QR pass with countdown (Windows 1600 dark; Pixel 7 light, dark, font 1.3), Settings Desk PIN (Windows 1100 light; Pixel 7 light, dark, font 1.3), Overview check-ins with Recorded by, reason and "By name only" (Windows 1600 dark).

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | The step outline and the countdown say what state things are in |
| 2 | Match System / Real World | 3 | "Desk PIN", "New code in 4 minutes", "Old code" in plain words |
| 3 | User Control and Freedom | 3 | Cancel closes the step and gives the row back |
| 4 | Consistency and Standards | 3 | Selected outline follows the app (ink in light, yellow in dark); Settings boxes have eyes like every password box |
| 5 | Error Prevention | 4 | Nothing recorded until the PIN is right; PIN typed twice in Settings |
| 6 | Recognition Rather Than Recall | 3 | Photo or initials next to the PIN |
| 7 | Flexibility and Efficiency | 3 | Enter in the PIN box checks in |
| 8 | Aesthetic and Minimalist Design | 3 | One extra panel; the reason field shows only for Admins |
| 9 | Error Recovery | 3 | Wrong, locked and no-PIN each say what to do next |
| 10 | Help and Documentation | 3 | One-line captions under each new field |
| **Total** | | **31/40** | **Good** |

## Design Specificity Verdict

Built in the incumbent yellow-and-black world from the approved mockup. The desk step is the product-specific moment: face check plus PIN, typed by the member.

## Priority Issues

- Fixed (P1): the member showed twice while the PIN step was open; the step now stands in for the row.
- Fixed (P1): the countdown read "6 minutes" for a 5-minute pass (server and device clocks); it now counts on the device from when the pass arrives.

## Minor Observations

- Deviation from the mockup: the PIN step sits at the top of the member list, not at the row's exact position (row bindings to the page break while the list reloads, BasePage.Row). After a name search it is the same place.
- The mockup's small progress ring next to the countdown was left out; the words carry it.
- The desk PIN box has no eye on purpose (staff must not read it); PasswordEyeTests names that one exception.

## Questions

- None open.
