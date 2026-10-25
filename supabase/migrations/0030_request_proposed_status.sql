-- Phase 7 (D8): a coach's counter-proposal waits for the member's answer. Added on its own because a new enum value
-- cannot be used in the same transaction that adds it; 0031 uses it.
alter type public.time_request_status add value if not exists 'proposed';
