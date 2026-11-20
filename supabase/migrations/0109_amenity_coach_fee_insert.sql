-- B1 (2026-11-20): 0014 restricted INSERT on amenities to six columns and 0064 added coach_fee with UPDATE only.
grant insert (coach_fee) on public.amenities to authenticated;
