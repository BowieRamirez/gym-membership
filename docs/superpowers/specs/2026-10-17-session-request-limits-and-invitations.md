# Session request limit and targeted coach invitations

> Historical 0021 implementation record. The owner subsequently applied 0021 live. The acceptance/rebooking behavior below is superseded by `2026-10-18-active-session-request-guard.md`: an active scheduled booking now blocks new requests/invitations for that pair. The separate 0022 correction is local only, not yet applied live.

## Owner-approved behavior

The owner approved proceeding after the recommendation of one pending member-created new-session request **per coach**, not globally. A member may request different hired coaches independently. Approval, rejection or withdrawal frees the slot immediately; a booked session need not finish first. Rescheduling remains governed by the existing one-pending-reschedule-per-session rule. Incoming coach invitations and old open slots do not consume the member-created request slot.

Windows coaches choose a currently hired, active-account member and send an invitation addressed to that person. The recipient approves/rejects through Time requests; only acceptance creates a session. Server checks still require an active membership. Invitations are not automatically booked, and are not broadcast to the coach's other trainees.

The existing Windows-first UI scope is retained: only Windows enables the member picker, invitation labels, pending-request explanation and Refresh requests control. Android retains its existing open-slot compose layout and labels. The shared command and database still enforce the member duplicate rule on every platform, including old clients after deployment. Android can already see/respond to targeted invitations through its existing pending-request list.

## Minimal implementation

- `0021_session_request_limits_and_invitations.sql`: unique partial index on member/coach for pending member-originated requests with no training_session_id; atomic protection even for simultaneous calls to the original request_time RPC.
- The migration aborts on existing duplicate groups without cancelling or deleting requests. `supabase/preflight/0021_preflight.sql` is the read-only list to review before deployment.
- `request_member_time(member, start, end)`: validates active sender/coach, locks the active coaching hire, verifies the recipient's active membership, inserts a targeted request and notifies only that member. The lock serializes with end_hire, which already cancels pending requests.
- Existing RLS already restricts targeted requests to the coach/recipient. Existing respond_time_request already checks the exact recipient, locks the request, creates one session on acceptance, and preserves overlap protection. No broader policy or approval-function rewrite is needed.
- Original open-slot API remains available for older/Android clients. No new table or dependency.
- SessionService returns the coach's active-account hired trainees and exposes the new RPC. DemoGateway mirrors both rules.
- RequestsViewModel disables duplicate member submissions for the selected coach, requires a selected member for Windows coach invitations, revalidates selections after refresh and refreshes the pending list after a server-detected duplicate. Async commands prevent double-click reentry.
- UI reuses the existing Barlow typography, panel/field styles and left-aligned request form. No restyling of unrelated screens.

## Verification plan

1. Regression checks fail before implementation; run the full xUnit suite afterwards.
2. Disposable local PostgreSQL: populated duplicate preflight, per-pair limit, approval/rejection/cancellation freeing a slot, truly concurrent requests on two connections, direct-write denial, recipient privacy and acceptance/rejection, invalid/ended hires, inactive/expired accounts, reschedule independence and legacy open slots.
3. Existing database suites still run. Adapt old overlap-test workflows to withdraw the failed overlapping request before sending the next request, preserving their overlap assertions.
4. Windows/Android Debug builds. Windows UI Automation in an isolated demo copy: pending hint and disabled send, cancellation re-enables sending, resubmission locks again, coach picker requires selection and sends a targeted invitation, selected member sees it.
5. Author correctness/security and ponytail complexity review. No independent review is implied.

## Recorded verification

- 485 xUnit tests pass; new request tests first failed for missing implementation. Tests cover pending guards/release, exact recipient actions, picker selection, preserving the Android form, selection refresh and friendly unique-index errors.
- Full local database run: 110 original checks, 31 Phase 7 checks, populated-history migration checks and 17 new session-request checks all pass. New concurrency checks use separate connections; invitation/end-hire checks confirm the competing transaction actually waits for the relationship lock in both orders.
- Windows and Android Debug builds pass with the existing media-picker warning. Existing unrelated chat-test analyzer warning remains.
- Windows UI Automation via `scripts/Test-WindowsSessionRequests.ps1` passes the member blocked/cancel/send/refresh workflow and coach picker/required selection/send/recipient receipt in an isolated demo copy. No live-backed test or financial write was performed.
- Author correctness/security and ponytail complexity self-review performed, not an independent review. Existing services, policy, approval RPC and native controls are reused; no new abstraction/dependency is needed. Visual/light-dark checks and live deployment remain unverified.

## Deployment boundary

Implementation and testing are local only. Do not apply a live migration, perform real financial tests, commit or push without owner authorization. Before deploying, run the read-only preflight and review any duplicate request IDs with the owner. Resolve extras through existing authorized cancel/reject operations; do not silently select a survivor or delete history. Review the migration/dry-run and obtain explicit approval to apply 0021 before testing invitations against the real project. Until deployed, the new coach invitation RPC will show the app's server-update-needed message on a live backend at 0020. The local demo can be tested without deploying.

## Known limits

One pending request is not a time-based rate limiter: a member may cancel and submit again. Already booked sessions do not consume the slot. Coach invitation frequency is unchanged/unlimited; only member-originated new-session requests were requested to be limited. Pending requests have no new automatic expiry. A stale list needs Refresh requests or reopening the page after the other person responds. Existing admin list/query caps also apply to request lists; the database index remains authoritative even if an old pending row is outside a fetched list. Device layout/light-dark and real-backed behavior require owner confirmation after deployment.
