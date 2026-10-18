# One active booking per coach/member pair

## Owner-approved correction

Once a coach/member request is accepted, that pair already has an active booking. Neither person may send another new booking request/invitation for that pair until all their scheduled sessions are explicitly completed, cancelled or marked no-show. The scheduled date passing alone does not release the guard. Requests to other coaches/members remain independent.

Rescheduling and cancellation remain available on the existing session. A reschedule changes that session only after the other person approves; it does not create another session or release the guard. Rejection/withdrawal of an unaccepted request still releases the member's existing one-pending-request slot. This supersedes the earlier 0021 specification's statement that acceptance immediately permits another request.

The owner reported successful live application of `0021_session_request_limits_and_invitations.sql`. Never rewrite that migration. This correction requires the separate `0022_active_session_request_guard.sql`, prepared locally only.

## Implementation

- Keep the 0021 partial unique index for pending member-originated requests per coach.
- An internal, client-inaccessible SECURITY DEFINER helper takes a transaction-level advisory lock for the coach/member pair and checks for any scheduled session. It returns `active_coaching_session` on conflict.
- Both new-request RPCs use the helper after existing authorization/membership/hire checks.
- Approval of any new session also uses it, including older targeted invitations and generic open slots. Different pending requests cannot concurrently create two sessions for the same pair. Existing overlap constraints continue to protect different pairs sharing a coach/member.
- Approval checks the caller before acquiring the pair lock, then locks and rechecks the pending request. Pair-before-request lock order prevents competing approvals from acquiring the locks in reverse order. Rejections and reschedules retain their original behavior.
- No new tables, public API signatures, dependencies or policy expansion. Helper execution is revoked from PUBLIC, anon and authenticated. Existing request RPC permissions are preserved by CREATE OR REPLACE.
- DemoGateway mirrors the guard inside its existing RPC lock. SessionService returns visible scheduled sessions; the form disables new submissions for the selected pair and handles stale server conflicts with a refresh.
- Windows adds an explanatory hint directing users to Sessions. Android's layout, picker behavior and open-slot compose labels are unchanged; shared backend rules apply to all clients.

## Existing data and deployment

The migration does not delete, cancel, reschedule or rewrite any existing booking/request. It does not create a unique index on existing scheduled sessions, so legacy multiple bookings do not require destructive cleanup. Any such pair must resolve all scheduled sessions before making a new booking. Older pending requests may remain visible; their new-booking acceptance is blocked while another session is active, but authorized rejection/withdrawal remains available.

No special data-cleanup preflight is required for 0022. Review the migration and `npx supabase db push --linked --dry-run` output before a separately authorized live push. Local app guards are not a substitute for deploying 0022: a live database at 0021 still allows requests from stale/older clients. No live database commands, live test messages/payments, commit, push or merge were performed for this correction.

## Verification

- New xUnit regression tests were run first: four failed on the old implementation. Final full suite: **493 passed, 0 failed**. Coverage includes active bookings, both directions, old requests/open slots, cancellation/completion/no-show release, same-session rescheduling and Windows command state.
- Full disposable PostgreSQL run: **110 existing checks + 31 Phase 7 checks + 26 session-request checks**, all pass, plus the populated-history migration suite. Separate-connection checks cover concurrent approvals and a send blocked behind an uncommitted acceptance; direct table writes/helper execution are denied. Scheduled past sessions still block new requests until explicitly resolved.
- Existing overlap tests now use a different coach/member pair to preserve real overlap-conflict coverage instead of being short-circuited by the new same-pair guard. Existing request tests explicitly resolve the demo seed booking when testing only pending requests.
- A pre-existing revenue test's fixed date collided with moving demo seed dates after midnight. Only its test clock/day was isolated from seed receipts; revenue production behavior and assertions are unchanged.
- Windows and Android Debug builds pass with the existing media-picker deprecation warning. The existing unrelated chat-test analyzer warning remains.
- Isolated Windows demo UI Automation passes: member active/pending guards, withdrawal/resubmission/refresh; coach accepts a member request, selects that member and cannot send again; Reschedule remains accessible; cancelling the session allows another invitation; the selected member receives it. The smoke script reselects the member after page navigation because the compose selection resets. No live backend was used.
- Author correctness/security and ponytail complexity self-review, not an independent review. Reused native controls and existing services; no general-purpose scheduling abstraction or additional dependency. Detailed light/dark and live-backed device checks remain owner verification after deployment.

## Limits retained

This is a booking-state guard, not a cooldown/rate limiter. Before a booking is accepted, coach invitations retain their existing frequency rules; members still have one pending new-session request per coach. Cancelling and re-requesting is allowed. Generic coach open-slot posting stays available, but a member who already has an active session with that coach cannot claim another. The Windows form may need Refresh requests/reopening after another device changes a booking; server enforcement remains authoritative.
