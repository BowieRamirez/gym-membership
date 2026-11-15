-- G30 (owner, 2026-11-15): hiring a coach becomes a request the coach accepts or declines. The two new statuses are added
-- on their own, because Postgres can't use a new enum value in the transaction that adds it; 0074 uses them.
alter type public.coach_hire_status add value if not exists 'pending';
alter type public.coach_hire_status add value if not exists 'declined';
