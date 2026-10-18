# Windows coach summary overview

## Scope

The owner requested a coach Overview containing summaries only, without quick-access actions. Continue the existing Windows-first scope. The page is inserted first and selected on Windows coach sign-in. Android retains Sessions as its first page and its existing navigation/layout. Admin and employee pages are not changed.

## Design

Reuse Barlow/Barlow Condensed, the existing Title/Heading/Caption styles and Panel borders. Existing light palette: Chalk #EDEFF1, Surface #FFFFFF, Ink #1F2226; dark palette: Chalk #1A1D20, Surface #24282C, Ink #ECEEF0. No new colors, icon system, decoration or animation.

Left-aligned heading, checked-at time and one Refresh overview control. A full-width Sessions summary gives the coaching day priority. Six supporting summaries use equal columns (three/two/one as the window narrows), matching existing panel spacing. No cards are clickable, no page-opening actions, no quick-access section and no write actions. Refresh reloads the summaries only. Existing sidebar navigation handles all other actions.

## Summaries

- Sessions: today's non-cancelled sessions belonging to this coach, upcoming/in-progress count and next member/time. Past completed/no-show sessions do not become upcoming sessions.
- Time requests: incoming member requests/reschedules awaiting the coach; sent invitations, sent reschedules and generic open slots are distinct. Only pending requests are counted.
- Trainees: distinct members with an active coaching hire; this is not a promise that each member has an active gym membership.
- My shifts: scheduled shifts that have not ended and the next/current time range.
- Attendance: current local calendar month's session count and rate, reusing AttendanceCalendar. Cancellations are excluded; excused and unmarked sessions are excluded from the rate denominator. No marks means no rate, not 0%.
- Messages: coaching-message unread count and conversations with unread messages, excluding front-desk support. No message read/delivery mutation is called.
- My coach profile: whether hiring is open/paused, specialty and hourly rate.

All dates/times use the device timezone, matching the coach attendance calendar. Calendar month boundaries are converted to UTC through the existing calendar helper. Counts reflect the service's loaded records and retain existing server query limits; the page says so explicitly rather than promising database-wide totals. No new service interface, database migration, permission or dependency is required.

Each summary handles failed reads independently: Unavailable replaces stale values and never becomes a misleading zero. Refresh or reopening retries all sections. Refresh cannot overlap another refresh. No background timer or Realtime subscription is added.

## Verification

- Two Windows navigation/read-only-page regression checks failed before implementation. Full xUnit suite now passes: **503 tests, zero failures** (10 new coach-overview checks).
- Unit coverage: read-only operations, seven sections/empty states, local-date/month UTC boundaries, own-session/cancellation filtering, incoming versus sent requests, the calendar attendance denominator, distinct trainees, current shifts, support-chat exclusion, hiring profile, partial failures/recovery, refresh reentry and actual demo services retaining unread messages.
- Windows and Android Debug builds pass. Only the existing media-picker deprecation warning and unrelated chat-test analyzer warning remain.
- `scripts/Test-WindowsCoachOverview.ps1` passes against an isolated Windows demo build with its copied Supabase config removed. Coach sign-in lands on Overview, all seven values load, refresh and navigation away/back work, no quick-access actions appear and admin still lands on its own Overview. It does not touch the owner's running Visual Studio instance or the repository Supabase config.
- Author correctness/security and ponytail complexity self-review, not an independent review: native Shell/Grid/Label controls and existing services; no navigation shortcuts, mutation commands or speculative shared-dashboard abstraction.
- No database checks rerun for this page-only feature; no schema/RPC changes. No live backend reads/writes, commit, push or merge. Detailed light/dark/narrow-window visual checks and live-backed owner confirmation remain separate from build/UI Automation evidence.

## Related deployment update

Before this request, the owner showed successful live application of migration 0022 and confirmed the active-session request guard works. That deployment does not need to be repeated for this Overview feature.
