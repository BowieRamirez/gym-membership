---
target: "Phase 11: screens changed after 9.9"
total_score: 28
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\App.xaml"
target_fingerprint: "sha256:90888bdfcfdbe8c174ad3cc15f198b9ca2ce25364ba464ef152591b5ebb6239e"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\App.xaml"
timestamp: 2026-11-09T10-27-57Z
slug: src-gymmembership-app-app-xaml
---
Method: design review only (detector not applicable, native platform)

Scope: Phase 11 final check. Screens changed after the 9.9 whole-app critique: Coaches & amenities (both halves), Trainees & shifts (both halves), Refunds & voids (both halves), Book an amenity grid (9.10, D30), registration password checklist, Settings change-password caption, Users add/reset captions (Phase 10). Windows demo build, 1600 px dark and 1100 px light.

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Switch shows the open half clearly; past half-hours on Book an amenity look like empty boxes |
| 2 | Match System / Real World | 3 | Plain words throughout; password checklist says what it checks |
| 3 | User Control and Freedom | 3 | Switch is one tap back; nothing traps |
| 4 | Consistency and Standards | 2 | Same in-card action drawn two ways (pill outline vs chip), sometimes side by side |
| 5 | Error Prevention | 3 | Password checklist ticks live; past cells can't be picked |
| 6 | Recognition Rather Than Recall | 3 | Colour key on the grid omits "past" and "open" |
| 7 | Flexibility and Efficiency | 2 | Still no keyboard shortcuts; evening grid makes you scan 24 dead rows |
| 8 | Aesthetic and Minimalist Design | 3 | Calm; rule sentences on Refunds and Void run ~190 characters wide |
| 9 | Error Recovery | 3 | weak_password and bad_password map to one clear sentence |
| 10 | Help and Documentation | 3 | One-sentence rules on each half |
| **Total** | | **28/40** | **Good** |

## Design Specificity Verdict

LLM assessment: the combined pages inherit Logs & Archive's quiet switch, so they read as one product. The booking grid is the most product-specific screen in the app and the D30 open/booked boxes work. The weak spot is the line between "empty because it's over" and "empty because it's free".

Deterministic scan: not applicable (native platform).

## Priority Issues

1. [P2] Past half-hours on Book an amenity read as open. Past cells are open cells at 30 % opacity: in dark a flat box with an edge, in light a pale grey box with an edge; the key names Yours, booked, cooldown, closed but not past. On an evening visit 24 of 32 rows are past. Fix: draw past cells with no box (transparent fill and border, as "outside"), so only real choices have edges. Command: /impeccable polish.
2. [P2] In-card actions drawn two ways. Coaches card: "Message" is a 2 px pill (outline button), "End hire" next to it a 1 px rounded-rectangle chip, different heights, so the row looks misaligned. Trainees half uses chips for Message and Remove; the My shifts half uses a pill for Cancel shift; Amenities mixes "Buy pass" pill with "Notify me" chip. Fix: in-card secondary actions are chips everywhere (9.6 already made Trainees' Message and Remove chips); keep the pill outline only for a card's single main action, or make both the same height. Command: /impeccable polish.
3. [P3] Rule sentences too wide. Refunds & voids, Cancellations half: one line ~1400 px (~190 characters) at 1600 px; Void half ~830 px. Packages already caps its rule at 760 px. Fix: MaximumWidthRequest 760 on those captions. Command: /impeccable layout.
4. [P3] My shifts at 1600 px: two shift cards on the left, the Post a shift panel pinned at the far right, ~400 px of nothing between. Fix: none required; same side-column pattern as Void. Command: /impeccable layout.

## Persona Red Flags

Jordan (first-timer member, evening): opens Book an amenity at 6 PM, sees a full grid of boxes, taps 9 AM: nothing happens, no word why.
Sam (front desk / System admin on a wide monitor): reads the refund rule as one 190-character line; eye has to travel the full width.

## Minor Observations

- "Pick open slots to book" is a filled button that does nothing until a cell is picked; it reads as instructions in a button.
- Password checklist: a valid password ("gymrat2026") rates "Fair"; fine as encouragement, not a block.
- The key's word "booked" is coloured, but booked cells are grey blocks, not coloured outlines.

## Questions to Consider

- Should today's grid start at the current half-hour instead of 6 AM?
- Is "Message" on a coach card the main action, or one of two equal ones?
