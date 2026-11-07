---
target: member pages, Windows and Android (after fixes)
total_score: 29
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\MemberShell.xaml"
target_fingerprint: "sha256:c4d46108831038b65260c0856035eb761377f882200e08978e01fb299b9466e1"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\MemberShell.xaml"
timestamp: 2026-11-07T14-00-59Z
slug: src-gymmembership-app-membershell-xaml
---
Method: design review only (detector not applicable, native platform)

Re-score after the five priority fixes of the 13:39 run. Same target and views, plus payment waiting and new member on both platforms.

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | New member's card now says what to do and offers it; payment deadline shown |
| 2 | Match System / Real World | 3 | Request kinds still read like system labels |
| 3 | User Control and Freedom | 3 | Chat has a proper back arrow on phones |
| 4 | Consistency and Standards | 3 | Chips for filters and day switching on both platforms; QR folded everywhere |
| 5 | Error Prevention | 3 | Unchanged |
| 6 | Recognition Rather Than Recall | 3 | Unchanged |
| 7 | Flexibility and Efficiency | 2 | Still no shortcuts on Windows |
| 8 | Aesthetic and Minimalist Design | 3 | One filled action per view; stacked panels stop at a readable width; quieter figures |
| 9 | Error Recovery | 3 | Unchanged |
| 10 | Help and Documentation | 3 | Unchanged |
| **Total** | | **29/40** | **Good** |

Remaining (not in the chosen scope): highlight wording "days left on Monthly / 5" vs the mockup's "Monthly / 5 days left"; full-width theme picker; near-white own chat bubbles in dark; chips at 40 dp under the 48 dp target; no keyboard shortcuts.
