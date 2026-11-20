-- C3 / Q18 (2026-11-20): remove the 30-minute cooldown between whole-hour amenity bookings.
create or replace function public.plus_cooldown(p_end timestamptz) returns timestamptz
language sql immutable parallel safe set search_path='' as $$ select p_end $$;

reindex table public.reservations;
