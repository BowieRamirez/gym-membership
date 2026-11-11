-- 0053 (Phase 7, F4): payments, check-ins and membership terms join the Realtime publication, so the pages that show them (member
-- My membership; desk Home, Payments to verify, Check-in; System admin Overview) hear of a change and reload. As with chat (0017) the
-- app treats a change as a hint only and asks the server again, and Realtime sends a change only to someone whose row rules let them
-- read the row: a member hears only of their own rows, staff of everyone's. No rule changes here.

alter publication supabase_realtime add table public.payments, public.gym_check_ins, public.membership_terms;
