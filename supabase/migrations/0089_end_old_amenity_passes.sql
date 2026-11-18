-- Owner, 2026-11-17: amenity bookings are per session only. 0066 (G19) stopped selling timed passes but let passes already bought
-- keep booking until they ended; this ends them now (one live pass, ending 18 Nov anyway). Unused sessions are untouched, and no new
-- timed pass can be made (0066's guard), so nothing else changes.
update public.user_amenities set status = 'expired' where not is_session and status = 'active';
