---
target: front-desk pages, Windows (phone colours only)
total_score: 27
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 3
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\StaffShell.xaml"
target_fingerprint: "sha256:914f6c2a3c463bc946a907d2da5ed2d3ee18155db61fffae7bb5bd26e35377b3"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\StaffShell.xaml"
timestamp: 2026-11-08T08-46-47Z
slug: src-gymmembership-app-staffshell-xaml
---
Method: design review only (detector not applicable, native platform)

Target: front-desk area (StaffShell, desk pages): Home, Payments to verify, Cashier, Check-in, Amenity Status, plus Messages and Settings as the desk. Windows demo "Front desk" (Marco Santos) at 1600 px dark and 1100 px light, one payment waiting, two coaches in, one active and two inactive members, sauna unavailable. Phone light and dark (D22: colours only, no relayout). Not captured: empty queue, long queue, the Amenity Status editor, a payment screenshot.

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Figures with captions; order summary; Active/Inactive with words |
| 2 | Match System / Real World | 3 | Net cash caption reads like a rule book |
| 3 | User Control and Freedom | 3 | Verify confirms; reject, check out, cancel present |
| 4 | Consistency and Standards | 2 | Payments queue is a table on Home, cards on its page; Cashier icon pills unlike chips; "Amenity Status" Title Case |
| 5 | Error Prevention | 3 | Complete payment disabled until the order is whole |
| 6 | Recognition Rather Than Recall | 3 | Cashier member search needs Find then a second picker |
| 7 | Flexibility and Efficiency | 2 | Seven actions to take a payment; QR scanner field is a good shortcut |
| 8 | Aesthetic and Minimalist Design | 2 | Two yellow figures on Home; paragraphs on three pages; one payment in a 440 px card on an empty page; Net cash two panels |
| 9 | Error Recovery | 3 | Banners and field errors |
| 10 | Help and Documentation | 3 | Rules present but paragraph-length |
| **Total** | | **27/40** | **Acceptable** |

## Design Specificity Verdict

Home reads as the gym's front desk (figures that say what to do, payments and coaches tables). The workhorse pages are generic form-and-card scaffolding: Payments to verify is one floating card under a paragraph; Cashier stacks paragraph, icon pills, a three-control member search, tiles and a two-panel Net cash; Amenity Status opens with two sentences. Yellow discipline from 9.5/9.6 not applied: two yellow figures on Home, a yellow Check in on every active member beside a yellow Scan member QR.

## Priority Issues

- [P1] Payments to verify is a card pile, not a queue. Fix: table (Payer, Package, Code, Reference, Sent, Amount, View screenshot / Verify / Reject) with TableBody fallback; outline actions; rule in one sentence. Command: /impeccable layout.
- [P1] Home: two yellow figures and a kicker heading ("Today at the desk", "Tap a number..."). Fix: yellow only on payments waiting while > 0; amenities plain with "1 unavailable"; drop heading and hint; chevron on tappable cards. Command: /impeccable quieter.
- [P1] Cashier: too many steps and words. Fix: app chips for the type; picker placeholder "Choose after Find", Find on Enter; Net cash one panel; one-sentence intro. Command: /impeccable distill.
- [P2] Check-in: yellow Check in per active row plus yellow Scan member QR; dead greyed Check in on inactive rows. Fix: scan is the one yellow; row Check in outline; inactive rows lose the dead button. Command: /impeccable quieter.
- [P2] Leftovers: "Amenity Status" Title Case; Amenity Status two-sentence intro; phone header cuts "Payments to v...", black status bar in light; Cashier member picker has no placeholder. Command: /impeccable polish.

## Persona Red Flags

Desk clerk (speed): seven actions to take a payment; verify queue not scannable; QR scanner field good.
New hire: three paragraphs before acting; "approval alone does not remove cash" needs explaining; Home figures point the right way.
Sam: greyed Check in on inactive members is a dead control; status always has a word.

## Minor Observations

- "Amenity visits" (VerifyAmenityUsagesPage) is unreachable: removed from the menu in the October role redesign, and nothing in the app creates amenity visits any more; page and view model are dead code. 9.7's "(with verify amenity use)" comes from the old page list.
- Phone front-desk bar is text-only (colours-only scope; reads correctly).
- Settings and Messages as the desk need nothing beyond the 9.6 app-wide fixes.

## Questions to Consider

- Does Payments to verify need a figure strip, or is "N waiting" in the header enough?
- What would a three-action payment look like: scan QR, choose package, Complete?
