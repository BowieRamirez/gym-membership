---
target: admin pages, Windows (re-score after 9.8)
total_score: 30
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\UsersPage.xaml"
target_fingerprint: "sha256:ae508cb20b75f08b2659ab4097cf1604ffa89b97489cc9aa422b93a913afd874"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\UsersPage.xaml"
timestamp: 2026-11-08T10-29-03Z
slug: src-gymmembership-app-views-userspage-xaml
---
Method: design review only (detector not applicable, native platform). Re-score after the 9.8 fixes and polish.

Checked: Windows demo System admin, all nine admin pages at 1600 px dark and 1100 px light, plus a package edit open and the Archive tab. Windows only (D27).

| # | Heuristic | Before | After | Why |
|---|-----------|--------|-------|-----|
| 1 | Visibility of System Status | 3 | 3 | Unchanged |
| 2 | Match System / Real World | 2 | 3 | Kinds, methods, counts, roles and log dates in words; payment objects still show an id (needs a database change) |
| 3 | User Control and Freedom | 3 | 3 | Deactivate now reads Activate on inactive items |
| 4 | Consistency and Standards | 2 | 3 | Users, Packages, Amenities, Discounts are tables like Void and Logs; one name per role; tabs show the open one |
| 5 | Error Prevention | 3 | 3 | Destructive chips last in every row |
| 6 | Recognition Rather Than Recall | 2 | 3 | Labelled Actions per account with a spoken name; empty states say so |
| 7 | Flexibility and Efficiency | 3 | 3 | Unchanged |
| 8 | Aesthetic and Minimalist Design | 2 | 3 | One yellow per section; one- or two-sentence rules; no duplicate headings; Archive rows in one panel |
| 9 | Error Recovery | 3 | 3 | Unchanged |
| 10 | Help and Documentation | 3 | 3 | Rules kept, shorter |
| **Total** | | **26** | **30/40** | **Good** |

Left: audit and archive objects show ids or raw archive dates where the log stores no name (database change, out of Phase 9); the Admin (sub-admin) view not captured separately (same pages, fewer of them); the Users actions menu itself not captured (a system dialog).
