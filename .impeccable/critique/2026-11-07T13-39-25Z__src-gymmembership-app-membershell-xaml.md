---
target: member pages, Windows and Android
total_score: 27
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 3
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\MemberShell.xaml"
target_fingerprint: "sha256:c4d46108831038b65260c0856035eb761377f882200e08978e01fb299b9466e1"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\MemberShell.xaml"
timestamp: 2026-11-07T13-39-25Z
slug: src-gymmembership-app-membershell-xaml
---
Method: design review only (detector not applicable, native platform)

Target: member area (MemberShell): My membership, Packages, Amenities, Book an amenity, Coaches, Sessions, Time requests, Messages, Settings, plus the locked page. Windows at 1100 px (light) and 1600 px (dark); Android Pixel 7 emulator, light and dark, font scale 1.0 and 1.3; states: active member, payment waiting (Windows), new member with no plan, open chat, Settings from the account menu. Not captured: Pay now on Android.

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Status chips with words, "Pay before ..." deadline, notices; a new member's card shows "–" and "Ends None" |
| 2 | Match System / Real World | 3 | Mostly plain words; request kinds "Open availability" / "Requested by member" read like system labels |
| 3 | User Control and Freedom | 3 | Cancel payment, cancel request, keep current time; chat uses an in-page "Back" pill instead of the app bar back arrow |
| 4 | Consistency and Standards | 2 | Same action styled differently per page and platform (day switcher pills vs chips; QR folded on phone, full on Windows; filled vs outline per-card actions) |
| 5 | Error Prevention | 3 | Confirm dialogs on buy, refund, cancel; Send request disabled with the reason shown |
| 6 | Recognition Rather Than Recall | 3 | Labels on everything; rules folded behind links on phones |
| 7 | Flexibility and Efficiency | 2 | Filters exist; no keyboard shortcuts or quick paths on Windows |
| 8 | Aesthetic and Minimalist Design | 2 | Dark Windows: three yellow buttons on Packages, Settings and the Pay now state; 52 px date numerals; stacked side panels push content down at 1100 px |
| 9 | Error Recovery | 3 | Field errors under fields, banners say what to do |
| 10 | Help and Documentation | 3 | Rules shown on Windows, one tap away on phones |
| **Total** | | **27/40** | **Acceptable** |

## Design Specificity Verdict

LLM assessment: the phone screens now feel authored for this gym (yellow highlight card with the lane meter, the QR row for the desk, two-line rows), but the Windows member pages are still generic in places: Packages is three identical price cards with three identical yellow buttons, Settings is a stack of form panels stretched to the window, and Time requests opens on two shouting number cards. The brand works where one thing is yellow; it falls apart wherever every filled button turns yellow in dark.

Deterministic scan: not applicable (native platform).

## Priority Issues

- [P1] New member sees a broken membership card. "Pick a package to get started." then a lone "–", "Ends None" and an empty lane (Android); "Current term ends: None" on Windows. Fix: with no term, the card shows the sentence and the action only; hide the number, the end line and the lane; Windows figure says "No active term". Command: /impeccable harden.
- [P1] Yellow everywhere in dark on Windows. Packages: three yellow "Choose next term"; Settings: Send code, Save theme, Change password all yellow; payment waiting: the yellow card, yellow "Choose a package / extend" and yellow "Choose screenshot and send". Fix: one filled action per view; repeated per-card and per-form actions become outline; "Choose a package / extend" goes quiet while a payment waits. Command: /impeccable quieter.
- [P1] At common 1000-1300 px windows the side panels stack on top and bury the page: My membership's QR panel (about 270 px), Sessions' "Move a session" hint, Time requests' disabled form. Fix: QR as the compact Show row on Windows too; Sessions shows its side panel only while moving a session; Time requests lists requests first. Command: /impeccable layout.
- [P2] Shouty and stretched elements on Windows: "Next session Mon 9 Nov, 9:00 AM" in 52 px numerals duplicating the list; fields stretched to about 950 px (Settings password, Pay now reference number, request form); term dates "13 Oct 2026, 21:33 – 12 Nov 2026, 21:33 (Manila)" wrap. Fix: smaller figure text, cap form width about 520 px, short dates. Command: /impeccable layout, /impeccable typeset.
- [P2] Inconsistent controls: Android chat "Back" pill; Book an amenity Previous/Next as big pills on Windows but chips on phones; Packages cards squeeze the price beside the name. Command: /impeccable polish.

## Persona Red Flags

Jordan (first-timer, new member): opens My membership after sign-up and reads "–" and "Ends None": looks broken at the first moment of trust.
Casey (phone at the desk): QR is one tap away, good; the chip row scrolls sideways at large text, acceptable; the payment-waiting screen on a phone was not seen.
Sam (accessibility): chips are 40 dp, under the 48 dp target; status always has a word beside the dot (good); the QR Show button has a screen-reader hint.

## Minor Observations

- My membership highlight still reads "days left on Monthly / 5" rather than the mockup's "Monthly / 5 days left".
- Settings theme picker spans the whole panel for three choices.
- Own chat bubbles are near-white blocks in dark.

## Questions to Consider

- What if Packages were one comparison panel with one Choose per row instead of three cards?
- Does the member QR need to be on screen at all on a Windows PC, where nobody scans it?
