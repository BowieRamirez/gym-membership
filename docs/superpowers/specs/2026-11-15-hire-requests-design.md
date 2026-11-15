# Hire requests: the coach accepts or declines (G30)

Owner, 2026-11-15: "member hires coach and it will go to coaches to accept the job in sessions; it must not be hired on the
spot, members have to wait for the coaches to accept. And there must be a list of members in the coach Sessions page to see
his accepted jobs." Owner chose: move the members list from My shifts to Sessions; go with the proposed defaults.

## Why
The deal and the payment are between the member and the coach (G29). A coach should agree to take a member on before that
member gets the coach's messages, sessions and time requests.

## Goals
- Hire sends a request. Nothing is unlocked until the coach accepts.
- The coach sees requests on their **Sessions** page and accepts or declines each one.
- The coach's **Sessions** page lists the members they have accepted (moved from My shifts, which no longer shows them).
- The member sees "Requested, waiting for the coach" on that coach's card and can **Cancel request**.
- The member is notified when the coach accepts or declines; the coach is notified of a new request.

## Non-goals
- No reason on decline, no expiry of unanswered requests (owner's defaults).
- No change to how a hire ends (End hire / Remove), messaging, sessions or time requests: they keep needing an *active* hire.
- No payment handling: payment stays between member and coach.

## Anti-goals
- A pending request must never unlock messaging, sessions or time requests.
- Existing active hires must not be disturbed or need re-accepting.

## Rules (defaults the owner accepted)
- A member can have requests out to several coaches at once, but only one open request or hire per coach.
- A coach who has paused "Taking new members" can't be requested (as hiring today). In the gym or not doesn't matter (G29).
- An active membership is needed to request (as hiring today), checked again when the coach accepts.
- An unanswered request stays waiting until the coach answers or the member cancels.
- Archiving either account cancels its open requests (they are not commitments that block archiving).

## Approaches considered
1. **A "pending" status on the existing hire row (chosen).** `coach_hire_status` gains `pending` and `declined`; Hire
   inserts a pending row; Accept turns it active. Everything that grants access already checks `status = 'active'`, so a
   pending row unlocks nothing without touching those rules. Smallest change, one table, history kept in place.
2. A separate `hire_requests` table, copied into `coach_hires` on accept. Cleaner separation, but two tables to keep in step,
   more policies and functions, and the history splits across them.
3. App-only "request" via messages. Not enforceable on the server; rejected.

## Design
**Database (0073 statuses, 0074 the rest)**
- `coach_hire_status` adds `pending`, `declined`. `coach_hires.status` default stays `active` for old callers; `hire_coach`
  now inserts `pending`. A unique index allows one open (pending or active) row per member and coach.
- `hire_coach(coach)`: same checks as today; inserts `pending`; notifies the coach "New hire request". Refuses
  `hire_already_requested` when the member already has a pending or active row with that coach.
- New `answer_hire(hire_id, accept bool)`: only that coach, only while `pending`; accept checks the member still has an
  active membership (`no_active_membership` otherwise), sets `active` and `hired_at = now()`; decline sets `declined`,
  `ended_at = now()`. Notifies the member "Hire accepted" / "Hire declined". Second answer refused `already_processed`.
- `cancel_hire_request(hire_id)`: only that member, only while `pending`; sets `cancelled`, `ended_at = now()`.
- `taken_coaches()` keeps counting only active hires (the "Hired" tag means accepted).
- Archiving an account sets its pending requests to `cancelled`.
- All new functions: authenticated only; break attempts in the tests (D16).

**Member Coaches page**
- Hire button sends the request (confirm sentence: "Send a hire request to {coach}? They will accept or decline it.").
- While pending: a "Requested" chip and the line "Waiting for {first name} to accept.", and **Cancel request** instead of
  Hire. After a decline the card is back to Hire (notification says it was declined).

**Coach Sessions page** (coach only; members' Sessions page unchanged)
- **Hire requests** section, only when there are any: name, photo, date asked, **Accept** / **Decline**.
- **Your members** section: the accepted members (today's "Your trainees" content: next session, sessions completed,
  Message, Remove), moved here from My shifts.
- My shifts no longer shows the members list.
- Layout and visuals come from an impeccable mockup the owner approves before the screens change; Windows and phone.

## Calls made while writing this (not asked of the owner)
- Postgres can't use a new enum value in the transaction that adds it, so the two statuses go in `0073` and the rest in `0074`.
- After a decline or cancel the member may request the same coach again (one *open* row per pair, not one ever).
- A coach who pauses "Taking new members" can still answer requests already waiting.

## Left to the builder
- Exact placement and order of the two sections relative to the sessions table and calendar (in the mockup).
- Demo mode mirrors every rule; demo seed keeps Alex hired by Lena and adds one pending request to Lena for the screens.

## Checks
- DB: requests unlock nothing; accept/decline/cancel rules and who may call them; one open row per member and coach;
  archive cancels requests; break attempts. Existing tests that hire through `hire_coach` then use the session features get
  their coach to accept first.
- xUnit: member and coach flows in demo mode; wording.
- Windows (and phone for the member and coach screens) checked after the mockup is approved.
