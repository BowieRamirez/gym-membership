---
target: admin pages, Windows
total_score: 26
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 3
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\UsersPage.xaml"
target_fingerprint: "sha256:884e40c7aa889ec7211deaeee28046d03197a1e6d5ee773a4ffbf25d82ecba18"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\UsersPage.xaml"
timestamp: 2026-11-08T09-15-51Z
slug: src-gymmembership-app-views-userspage-xaml
---
Method: design review only (detector not applicable, native platform)

Target: admin pages (StaffShell, admin and super items): Users and roles, Packages and amenities, Discounts, Cancellations & refunds, Void a payment, Gym settings, Revenue, Logs & Archive; Overview colour check only. Windows only (D27). Demo System admin at 1600 px dark and 1100 px light. Not captured: Users actions menu and editors, package/amenity/discount editors, Archive tab, the Admin (sub-admin) view.

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Status chips with words; figures with captions |
| 2 | Match System / Real World | 2 | "membership · qr · 1 payment(s)", "walk_in", "cash"; log "2026-11-05 17:12", Object "5" |
| 3 | User Control and Freedom | 3 | Deactivate, Archive, Void reversible or confirmed |
| 4 | Consistency and Standards | 2 | Card piles vs tables; front-desk role named three ways; Logs tabs show no current tab |
| 5 | Error Prevention | 3 | Destructive actions confirm |
| 6 | Recognition Rather Than Recall | 2 | Account actions hidden behind "⋮"; Deactivated accounts empty bar with no words |
| 7 | Flexibility and Efficiency | 3 | Filters, CSV and PDF export |
| 8 | Aesthetic and Minimalist Design | 2 | Five yellows on Packages; five buttons per amenity card; 3-4 sentence paragraphs; Revenue yellow card plus yellow button |
| 9 | Error Recovery | 3 | Banners and field errors |
| 10 | Help and Documentation | 3 | Rules written down at paragraph length |
| **Total** | | **26/40** | **Acceptable** |

## Design Specificity Verdict

Overview (approved standard, colours fine), Void and Logs read as a back office. Packages and amenities, Discounts and Users are the generic dashboard kit the spec forbids: identical cards with button rows (five per amenity, yellow Edit per package) and "⋮" per account. Database words leak through (walk_in, qr, payment(s), system admin, employee, object id 5).

## Priority Issues

- [P1] Packages and amenities (and Discounts): card grid with button walls. Fix: tables, row actions as chips, Add the one yellow per section. Command: /impeccable layout.
- [P1] Codes on screen: Revenue kinds/methods and "payment(s)", Void methods, Users role words, Logs timestamps and object ids, front-desk role "employee"/"Employee"/"Front desk". Fix: display names, plural counts, title-form roles, app date words. Command: /impeccable clarify.
- [P1] Users and roles: card pile with hidden actions, empty Deactivated bar. Fix: table (Name, Email, Role, Status, Actions), empty sentence. Command: /impeccable layout.
- [P2] Logs & Archive tabs show no current tab. Fix: the quiet segmented switch. Command: /impeccable polish.
- [P2] Paragraphs and extra yellow: Revenue three lines plus yellow Show revenue beside the yellow figure; Refunds duplicate heading and four sentences; Void three sentences; Users two. Fix: one sentence each, Show revenue outline. Command: /impeccable distill.

## Persona Red Flags

Owner at month-end: decoding "walk_in · cash · 1 payment(s)"; raw log timestamps and ids; reset password behind each "⋮".
New sub-admin: five buttons per amenity, Archive beside Deactivate, session price and photo next to destructive actions.
Sam: "⋮" spoken name not verified; Logs tabs give no current state.

## Minor Observations

- Gym settings opening-hours pickers lack Opens / Closes labels.
- Users Add form: Role wraps to its own row at 1100 px.

## Questions to Consider

- Does an amenity need session price, photo and pass on the list, or only in its editor?
- Should "Front desk" be the role's one name everywhere, including Add user?
