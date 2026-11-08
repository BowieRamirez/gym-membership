---
target: coach pages, Windows and Android (re-score after 9.6 fixes)
total_score: 29
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\CoachShell.xaml"
target_fingerprint: "sha256:ced37d95c0cef4d2b7757864e915180d130c51a63d55de4f017250e0cda45216"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\CoachShell.xaml"
timestamp: 2026-11-08T07-48-03Z
slug: src-gymmembership-app-coachshell-xaml
closed: true
---
Method: design review only (detector not applicable, native platform). Re-score after the 9.6 fixes.

Checked: Windows demo build at 1600 px dark and 1100 px light and dark (Dashboard, Sessions, Time requests, Trainees, My shifts, My coach profile); Pixel 7 emulator dark, and light at font scale 1.3 (Dashboard month and week, Sessions, Trainees, Time requests, My coach profile).

| # | Heuristic | Before | After | Why |
|---|-----------|--------|-------|-----|
| 1 | Visibility of System Status | 3 | 3 | Cells name sessions and shifts; check and X still have no key |
| 2 | Match System / Real World | 3 | 3 | Dates read "Mon 9 Nov 2026" in pickers; request kinds still system-like |
| 3 | User Control and Freedom | 3 | 3 | Unchanged |
| 4 | Consistency and Standards | 2 | 3 | Chips for every in-row action and mark; quiet Month/Week switch; one hiring control |
| 5 | Error Prevention | 2 | 3 | Remove is a small chip; no clipped labels at 1.0 or 1.3 |
| 6 | Recognition Rather Than Recall | 2 | 3 | Calendar names each session; Sessions opens on what needs doing |
| 7 | Flexibility and Efficiency | 2 | 2 | Still no shortcuts |
| 8 | Aesthetic and Minimalist Design | 2 | 3 | One filled yellow action per view; photo prompt only on the profile; phone dashboard starts with the next session and today |
| 9 | Error Recovery | 3 | 3 | Unchanged |
| 10 | Help and Documentation | 3 | 3 | Unchanged |
| **Total** | | **25** | **29/40** | **Good** |

Fixed: (1) clipped phone actions: FilterChip never shrinks in a wrapping FlexLayout (style-level, app-wide); (2) Dashboard rebuilt to the approved mockup (yellow next-session card, captions, named cells, today yellow, day panel sessions then shift form, quiet Month/Week; phones today first); (3) Sessions scheduled first then history newest first, photo prompt only on My coach profile; (4) yellow per row and card removed (Trainees, Sessions, Time requests, profile Check out); (5) Android lime colorAccent replaced by the signal yellow, palette test now scans Android resources; Pause hiring removed from My shifts (profile switch remains); date pickers "ddd d MMM yyyy"; profile form capped at 720 px; shift cards day then hours.

Left: calendar mark key; phone chips 40 dp (under 48); phone open-slot vs Windows invite (Phase 10 note).
