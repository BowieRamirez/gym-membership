# Terms and Policy at sign-up (short design)

Date: 2026-11-15. Owner request; decisions G24 (text drafted from the app's rules) and G25 (option 1) in
`docs/superpowers/plans/2026-11-12-owner-fixes.md`.

## Why
A member who signs up should agree to the gym's terms, including the 7-day refund rule (G23), so a dispute can point to what they
accepted. Reading the terms is optional; agreeing is required.

## What the member sees (Register page, Windows and phone)
```
  Repeat password
  [••••••••          ]

  [ ] I agree to the Terms and Policy        <- "Terms and Policy" is a link; the box is a real CheckBox
      (unticked + Create account:  "Tick the box to agree to the Terms and Policy.")

  [ Create account ]
```
- "Terms and Policy" opens the text: a scrollable side sheet on Windows, a full screen on the phone, with a Close button (and the
  phone's Back). Reading it does not tick the box; closing returns to the form with everything typed kept.
- Create account with the box unticked shows the field error above and creates nothing.

## Approaches
1. **Terms in the app, acceptance saved at sign-up (chosen by the owner).** The text is one file shipped with the app
   (`Resources/Raw/terms.md`, shown as plain headed sections) with a version (`2026-11-15`). Sign-up sends `terms_version`; the server
   saves it and the time on the profile, and refuses a self sign-up without it. New wording = new app build.
2. Terms in the database, edited in Gym settings: no app release to change wording, but a new admin screen and versioning. Not now.
3. A web page link: needs hosting, breaks offline. Rejected.

## The terms text (G24)
Drafted only from how the app works today: who the gym is and how to reach it (gym name from Gym settings, filled in by the owner);
accounts and sign-in; packages and terms (start after the last paid term, no upgrades or automatic charges); paying (QR with proof, or
cash/GCash at the desk; a payment is verified by staff; unpaid QR expires); refunds (G22/G23: only in a term's first 7 days, the unused
days counted when staff approve, repaid by hand); check-in (QR, desk PIN, password, ID at the desk); amenity bookings (down payment,
balance at the desk, cancellation refunds as now); coaches and messages; gym closing days; conduct and account archiving; personal
data the app keeps (name, email, phone, photo, check-ins, payments, messages), why, who sees it (staff), and how to ask for it to be
corrected or removed. Marked **draft, to be checked by the owner or someone qualified** (Data Privacy Act); no claims the app can't
back up.
c
## Builder's calls (owner can correct)
- Server: migration `0069`. `profiles` gains `terms_version text` and `terms_accepted_at timestamptz`, filled from sign-up metadata.
  A **self sign-up** (no role in app metadata) without `terms_version` is refused (`terms_required`), like the last-name check (0059).
  Accounts made by an admin (staff, coaches, members added on Users and roles) are not asked here; noted as open for the owner.
- Existing accounts are not asked again (no version on file; owner may want a "please accept" step later).
- Demo mirrors the refusal; the version is one constant shared by the app and the page.
- UI through `impeccable` (shape, mockup in `docs/mockups/register-terms.html` for approval first), checked on Windows and the Pixel 7,
  light and dark, font 1.3. Copy through `impeccable clarify`.
- Tests first: database (refused without, saved with, admin-made accounts unaffected, anon can't read others' acceptance), xUnit
  (register refuses unticked, sends the version; demo refuses without).
