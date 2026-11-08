---
target: coach pages, Windows and Android
total_score: 25
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 4
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\CoachShell.xaml"
target_fingerprint: "sha256:ced37d95c0cef4d2b7757864e915180d130c51a63d55de4f017250e0cda45216"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\CoachShell.xaml"
timestamp: 2026-11-08T07-18-21Z
slug: src-gymmembership-app-coachshell-xaml
---
Method: design review only (detector not applicable, native platform)

Target: coach area (CoachShell): Dashboard, Sessions, Time requests, Trainees, My shifts, My coach profile, Amenities, Messages, Settings. Windows demo build at 1100 px light and 1600 px dark; Android Pixel 7 emulator light and dark, scrolled. States: coach without a photo, empty day, pending requests, scheduled and past sessions. Not captured: coach with no trainees; font scale 1.3. Compared with the approved mockup docs/mockups/yellow-black.html?screen=coach.

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Status chips with words; calendar check/cross marks have no key |
| 2 | Match System / Real World | 3 | "Open availability" / "Requested by member" read like system labels; dates as 11/9/2026 next to "Mon 9 Nov" |
| 3 | User Control and Freedom | 3 | Cancel shift, cancel request, cancel session all present |
| 4 | Consistency and Standards | 2 | Post a shift on two pages with different pickers and defaults; Month/Week and attendance as pills, not 9.5 chips |
| 5 | Error Prevention | 2 | Phone Trainees: full-width Remove as heavy as Message; phone Time requests clips "Decline" to "Declin" |
| 6 | Recognition Rather Than Recall | 2 | Calendar cells show only marks, no names or times; Sessions puts actionable rows at the bottom |
| 7 | Flexibility and Efficiency | 2 | Sessions opens on All; no quick path to today's sessions |
| 8 | Aesthetic and Minimalist Design | 2 | Yellow per row and per card; photo prompt on two pages; phone dashboard first screen is all KPI tiles |
| 9 | Error Recovery | 3 | Hints under fields; disabled Send explains itself |
| 10 | Help and Documentation | 3 | Short rule lines where they matter |
| **Total** | | **25/40** | **Acceptable** |

## Design Specificity Verdict

The approved mockup's coach Dashboard is specific (yellow = next session; cells carry "09:00 Alex T." and "Shift 18:00"; today a yellow outline; day panel lists sessions then a compact shift form). The build is the old layout in new colours: yellow on "Sessions this month", empty cells with marks, today a black hole in dark. Other coach pages are generic lists and forms; yellow discipline from 9.5 did not reach them.

## Priority Issues

- [P1] Phone Time requests clips action labels ("Approv", "Propose another", "Declin"). Fix: FilterChip in-row actions sized to text and wrapping. Command: /impeccable adapt.
- [P1] Dashboard ignores the approved mockup. Fix: yellow card = next session; captions on figures; cells with session/shift chips; today yellow outline; day panel sessions first then compact shift form; quiet Month/Week segment; phone: compact figure row, attendance as chips. Command: /impeccable layout.
- [P1] Coach Sessions buries the work: oldest first, Mark complete under 10 past rows; photo prompt on top (phone) / side (Windows), already on My coach profile. Fix: scheduled first soonest first, then history newest first (or open on Scheduled); photo prompt only on My coach profile. Command: /impeccable distill.
- [P1] Yellow overload: Trainees Message per card (full width on phone, equal Remove below); Mark complete per session row; profile Check out + Save profile; dark Dashboard three yellows. Fix: one filled action per view, per-row actions as chips/outline, Remove a small outline chip. Command: /impeccable quieter.
- [P2] Leftovers: Android switch lime (values-night colorAccent #E4F58A); Pause hiring on My shifts duplicates the profile switch; 11/9/2026 date format; Windows profile fields stretched about 1450 px (no StackedSideWidth cap); shift titles break mid-time. Command: /impeccable polish.

## Persona Red Flags

Coach between clients (Casey, phone): Sessions opens on the photo prompt then October; Time requests shows "Declin"; dashboard first screen is four tiles.
Alex (Windows): no week view of sessions by name; Sessions filter resets to All.
Sam: icon-only calendar marks with no key; chips 40 dp under 48 dp; status always has a word (good).

## Minor Observations

- Windows "Invite a member" picker: tall box, placeholder stuck to the top.
- Disabled Send invitation in dark is muddy olive.
- Upcoming KPI's "Next: ..." line duplicates what the yellow card should say.
- Settings theme value reads "system" in lower case.

## Questions to Consider

- Phone offers an open slot, Windows invites a specific member (ComposeTitle, StaffViewModels.cs:539). Should both platforms do both?
- On a phone, should the coach Dashboard be just today: next session, then attendance rows, month behind a tab?
- Does Pause hiring belong anywhere except the profile switch?
