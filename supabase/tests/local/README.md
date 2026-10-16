# Local database tests

Applies every file in `supabase/migrations/` to a throwaway Postgres (no Docker needed) and checks the
security rules and payment functions end to end: who can see what, guest locks, payments, discounts,
voids, coaching, expiry jobs. `npm test` runs the baseline rules, Phase 7 security/concurrency checks,
and a populated-history migration suite. All databases are temporary; these commands never push to Supabase.

The harness uses a unique temporary data directory and an available port. On Windows, PostgreSQL
uses synchronous I/O during initialization/startup to avoid embedded worker startup hangs.

Phase 7's history test applies `0018` to original payments and completed renewals, preserves their
paid windows and amounts, labels catalog-derived snapshots as legacy, and verifies that ambiguous
or overlapping history aborts rather than silently changing entitlement.

```bash
cd supabase/tests/local
npm install
npm test
```

Supabase-only pieces (`auth.users`, `auth.uid()`, storage tables, `pg_cron`) are replaced by small stand-ins in
`harness.mjs`. These tests do not replace running against your real Supabase project once it exists.
