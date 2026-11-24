-- Owner, 2026-11-25: 0121 (E8) removed Time requests, including respond_time_request, but left request_reschedule behind. The Sessions
-- page kept sending reschedule requests that nothing could approve. The new coach booking (0120) has no reschedule step: a session's
-- time changes by cancelling it and booking again. Old time_requests rows stay for history (0121).
drop function if exists public.request_reschedule(bigint, timestamptz, timestamptz, text);
