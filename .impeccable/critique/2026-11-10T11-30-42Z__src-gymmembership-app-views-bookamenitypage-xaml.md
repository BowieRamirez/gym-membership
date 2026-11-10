---
target: Phase 5 amenity booking screens
total_score: 28
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\BookAmenityPage.xaml"
target_fingerprint: "sha256:0874e9134b831e7e8d9b2ac19fe281fa87bc57b18697ecb412f341eed0d3c3be"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\BookAmenityPage.xaml"
timestamp: 2026-11-10T11-30-42Z
slug: src-gymmembership-app-views-bookamenitypage-xaml
---
Method: design review only (detector not applicable, native platform)

Target: Phase 5 screens: member Book an amenity (cards, booking sheet, My bookings, "I didn't use it"), coach Amenities ("I run this"), desk Payments to verify (booking rows, Booking refund claims), Refunds & voids (booking refunds), Coaches (Amenities half removed). Captured on Windows (1600 dark, 1100 light) and the Pixel 7 (dark, light at font 1.3).

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Hold deadline, status chip and notice say where a booking stands; no count of free times per day on the month. |
| 2 | Match System / Real World | 3 | "Book and pay", "Take cash", "I didn't use it" are the desk's words; a pass-type amenity shows "for 30 days" inside a per-session booking flow. |
| 3 | User Control and Freedom | 3 | Close, scrim tap, Go back on every confirm, Cancel on the row. |
| 4 | Consistency and Standards | 3 | Dashboard calendar and Cashier chips reused; the "I run this" switch uses the platform switch, as every switch in the app. |
| 5 | Error Prevention | 3 | Only times and lengths that fit are offered; clash reloads the day with the reason. |
| 6 | Recognition Rather Than Recall | 3 | The summary repeats the choice; days with a booking carry a dot. |
| 7 | Flexibility and Efficiency | 2 | 32 time chips to scan; no "next free time" shortcut. |
| 8 | Aesthetic and Minimalist Design | 3 | Calm, one yellow action per state; the time grid is a long scroll on a phone. |
| 9 | Error Recovery | 3 | Server reasons mapped to plain text (too soon, too late to cancel, claim window closed). |
| 10 | Help and Documentation | 2 | The 1-hour and 24-hour rules show only in captions at the moment they apply. |
| **Total** | | **28/40** | **Good** |

Design specificity: authored for this gym, built from the app's own yellow/black language, the coach Dashboard's month and the Cashier's chips; the desk claim row puts what the member said next to what the desk checks, which is specific to this job.

What's working: the sheet keeps the total and the one action fixed at the bottom on the phone; booking rows are real two-line phone rows; the desk table reads left to right as the decision is made.

Priority issues
- [P2] Time grid is a wall of 32 chips (phone: two screens). Fix: group by morning / afternoon / evening, or hide times that can't be booked. Command: /impeccable layout.
- [P2] Fixed during this run: the "I didn't use it" reason box opened above the list, off-screen on a phone. Now under the list and scrolled into view.
- [P2] Fixed during this run: faded times on today had no reason when paying first. Now "Times less than 1 hour from now can't be held online."
- [P3] The desk's "Amenity now" shows the status at the time of checking, not at the time of the booking (there is no status history). Command: /impeccable clarify (label) or a status log later.
- [P3] Phone time chips leave an uneven right edge (4 fixed-width chips in 360 px). Command: /impeccable layout.

Persona red flags
- Casey (distracted mobile): long time grid; the fixed bottom bar keeps the action reachable.
- Sam (accessibility): fixed during this run: day cells and pay options were tap-only Borders; now buttons with descriptions, read once.

Questions: Should the time grid group by part of day? Should the desk see an amenity status log for claims?
