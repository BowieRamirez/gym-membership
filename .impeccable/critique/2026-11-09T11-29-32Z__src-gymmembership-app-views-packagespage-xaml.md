---
target: "Phase 1: Discounts and Packages"
total_score: 31
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\PackagesPage.xaml"
target_fingerprint: "sha256:23f0d54144f1573554255b66c56cca096ecbcde66de3130c35577c655f8f2495"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\PackagesPage.xaml"
timestamp: 2026-11-09T11-29-32Z
slug: src-gymmembership-app-views-packagespage-xaml
---
Method: design review only (detector not applicable, native platform)

Scope: Plan 2026-11-09, Phase 1. Discounts (Windows only: no Edit on a used code, "Show on Packages" switch in Add and Edit, "Shown on Packages" caption per code) and member Packages (Windows and Android: "Codes you can use" with a Use code button that fills the code box). Before captures: Discounts 1600 dark, Packages 1100 light. After: Discounts 1600 dark with Edit open, Packages 1100 light and 1600 dark, Pixel 7 light, dark and font 1.3.

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | "Shown on Packages" under the code says which codes are public; Use code fills the box in view |
| 2 | Match System / Real World | 3 | "Codes you can use", "Use code": member words |
| 3 | User Control and Freedom | 3 | A used code can still be turned off; Use code is undoable by editing the box |
| 4 | Consistency and Standards | 3 | Switch row copied from Logs & Archive; Use code is the FilterChip used on table actions |
| 5 | Error Prevention | 4 | Edit is gone on used codes; the database refuses edits anyway (discount_locked) |
| 6 | Recognition Rather Than Recall | 3 | Members no longer have to know a code exists |
| 7 | Flexibility and Efficiency | 3 | One tap instead of copy and paste |
| 8 | Aesthetic and Minimalist Design | 3 | Section hidden when there are no codes; card height matches the code box |
| 9 | Error Recovery | 3 | discount_locked has its own sentence with the way out |
| 10 | Help and Documentation | 3 | Page intro states the lock rule once |
| **Total** | | **31/40** | **Good** |

## Design Specificity Verdict

Additive refinement in the incumbent world; nothing new invented. The codes sit between the code box and the packages, so the relation reads without a label beyond "Codes you can use".

## Priority Issues

- Fixed in this pass (P1): on Android a column FlexLayout clipped the code card (caption and button cut off). Now the app's CardGrid, checked at font 1.3.

## Minor Observations

- Pre-existing, not changed: Edit on an old unlimited code opens with "Max uses 1" (the field needs some number). Saving only the date keeps it unlimited (tested), but the field reads oddly.
- Pre-existing: the "on" Switch in dark theme is a grey track; same everywhere in the app.
- Spec deviation for the owner: F5 said "Copy"; built as "Use code" that fills the box on the same page.

## Questions

- Keep "Use code", or switch to a clipboard Copy as F5 worded it?
