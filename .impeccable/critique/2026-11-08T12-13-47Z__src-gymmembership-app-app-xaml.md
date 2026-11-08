---
target: 9.9 all Phase 9 screens
total_score: 28
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 2
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\App.xaml"
target_fingerprint: "sha256:90888bdfcfdbe8c174ad3cc15f198b9ca2ce25364ba464ef152591b5ebb6239e"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\App.xaml"
timestamp: 2026-11-08T12-13-47Z
slug: src-gymmembership-app-app-xaml
---
Method: design review only (detector not applicable, native platform)

# 9.9 cross-screen critique, all Phase 9 screens
Windows: 38 pages at 1600 px dark, 34 at 1100 px light (member, coach, front desk, System admin). Phone (Pixel 7, dark): 12 member and 8 coach screens.

| # | Heuristic | Score | Key Issue |
|---|---|---|---|
| 1 | Visibility of System Status | 3 | status chips, current page, highlight cards consistent |
| 2 | Match System / Real World | 3 | times switch between 9:00 AM and 19:56; "Choose after Find" inside a field |
| 3 | User Control and Freedom | 3 | solid cancel / go-back |
| 4 | Consistency and Standards | 2 | inner box in fields, doubled picker labels, filled vs outline card actions, two clock formats |
| 5 | Error Prevention | 3 | confirms before money and archive actions |
| 6 | Recognition Rather Than Recall | 3 | labels everywhere |
| 7 | Flexibility and Efficiency | 2 | no keyboard shortcuts |
| 8 | Aesthetic and Minimalist Design | 3 | one-filled-action broken on Amenities and Coaches |
| 9 | Error Recovery | 3 | plain messages |
| 10 | Help and Documentation | 3 | one-line rules per page |
| Total | | 28/40 | Good |

Design specificity: authored (yellow/black, HF monogram, yellow figure card system, one chip/pill language); leftover is texture from 9.5-9.8 built separately.

Priority issues:
1. [P1] Every Windows text field draws a second #313131 box inside its #242424 Field frame (WinUI 6% fill; Platforms/Windows/App.xaml overrides do not take effect). Fix: shared Windows-only transparent input background.
2. [P1] Pickers with a Title show it as a header plus an empty box on Windows (Cashier "Choose after Find", coach invite "Choose a member"). Fix: Windows handler maps Title to placeholder text.
3. [P2] Two clock and date formats: Overview "08 Nov 2026 · Checked 19:56", "05 Nov", "₱ 0.00"; booking grid "06:00"; booking times "14:00-15:00"; term/refund dates "dd MMM yyyy, HH:mm"; check-in times. Fix: "h:mm tt" and "d MMM" on screen; CSV/PDF keep sortable.
4. [P2] In-card actions: Packages outline, but Amenities two filled Buy pass and Coaches filled Message + Hire. Fix: outline.
5. [P3] "days left on Monthly" lowercase label above the figure. Fix: "Days left on Monthly".

Persona red flags: Alex (desk) Cashier Find then empty-looking picker, no shortcuts; Sam doubled picker announcement, inner field boxes; Casey phone Sessions filter clips "Cancelled or no-show", two yellow Buy pass.

Minor: phone Sessions filter row clipped; My membership payments table gap at 1600 px; Barlow small period reads like a comma (font, leave); coach profile peso sign outside field; not captured: phone light, sub-admin.
