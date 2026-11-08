---
target: front-desk pages, Windows (re-score after 9.7)
total_score: 29
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\StaffShell.xaml"
target_fingerprint: "sha256:b06aa3c1378a116955be9adc508e503c21a40bb7e85cbd91037c28921e327fa2"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\StaffShell.xaml"
timestamp: 2026-11-08T09-10-11Z
slug: src-gymmembership-app-staffshell-xaml
---
Method: design review only (detector not applicable, native platform). Re-score after the 9.7 fixes and polish.

Checked: Windows demo "Front desk" at 1600 px dark and 1100 px light, all seven pages, plus the screenshot panel on Payments to verify and the Amenity status editor open. Android not checked: owner, 2026-11-08, staff pages are Windows only.

| # | Heuristic | Before | After | Why |
|---|-----------|--------|-------|-----|
| 1 | Visibility of System Status | 3 | 3 | Unchanged |
| 2 | Match System / Real World | 3 | 3 | Shorter rules ("It should match the drawer.") |
| 3 | User Control and Freedom | 3 | 3 | Unchanged |
| 4 | Consistency and Standards | 2 | 3 | Payments queue is a table on both Home and its page; Cashier and Amenity status use the app's chips; "Amenity status" in sentence case |
| 5 | Error Prevention | 3 | 3 | Unchanged |
| 6 | Recognition Rather Than Recall | 3 | 3 | Member picker says "Choose after Find" |
| 7 | Flexibility and Efficiency | 2 | 2 | Taking a payment is still several steps |
| 8 | Aesthetic and Minimalist Design | 2 | 3 | One yellow figure on Home, one yellow action on Check-in, one-sentence rules, Net cash one panel under the order |
| 9 | Error Recovery | 3 | 3 | Unchanged |
| 10 | Help and Documentation | 3 | 3 | Unchanged |
| **Total** | | **27** | **29/40** | **Good** |

Left: the payment path is still type, Find, choose, package, method, Complete (a scan-first cashier would be a behaviour change); "Amenity visits" page unreachable, noted for Phase 11 (D27); empty and long queues not captured.
