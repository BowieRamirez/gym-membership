# Local database tests

Applies every file in `supabase/migrations/` to a throwaway Postgres (no Docker needed) and checks the
security rules and payment functions end to end: who can see what, guest locks, payments, discounts,
voids, coaching, expiry jobs.

```bash
cd supabase/tests/local
npm install
npm test
```

Supabase-only pieces (`auth.users`, `auth.uid()`, storage tables, `pg_cron`) are replaced by small stand-ins in
`harness.mjs`. These tests do not replace running against your real Supabase project once it exists.
