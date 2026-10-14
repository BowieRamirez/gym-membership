// Smoke check for the Supabase project an app build talks to. Run by CI before a release build (see docs/specs/ci-cd-pipelines.md, 4.7).
//
//   SUPABASE_URL=... SUPABASE_ANON_KEY=... node scripts/check-supabase.mjs
//
// Level 1 uses only the public anon key and changes nothing. Level 2 runs when SUPABASE_SMOKE_EMAIL and SUPABASE_SMOKE_PASSWORD are
// set: it signs in as a dedicated test member and reads the package list the way the app does. Secrets are never printed.

const base = (process.env.SUPABASE_URL ?? '').replace(/\/+$/, '');
const key = process.env.SUPABASE_ANON_KEY ?? '';
if (!base || !key) { console.error('SUPABASE_URL and SUPABASE_ANON_KEY must be set.'); process.exit(2); }

let failed = 0;

async function call(path, { method = 'GET', body, token } = {}) {
  const res = await fetch(base + path, {
    method,
    headers: { apikey: key, Authorization: `Bearer ${token ?? key}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(15000),
  });
  let json = null;
  try { json = await res.json(); } catch { /* empty or non-JSON body */ }
  return { status: res.status, json };
}

async function check(name, run) {
  try { await run(); console.log(`ok    ${name}`); }
  catch (e) { failed++; console.log(`FAIL  ${name} - ${e.message}`); }
}

const expect = (cond, message) => { if (!cond) throw new Error(message); };

await check('auth service answers', async () => {
  const { status } = await call('/auth/v1/health');
  expect(status === 200, `GET /auth/v1/health returned ${status}; check SUPABASE_URL, the key, and whether the project is paused`);
});

await check('anon key accepted, tables exist and are closed to signed-out users', async () => {
  const { status, json } = await call('/rest/v1/membership_packages?select=name&limit=1');
  expect(json?.message !== 'Invalid API key', 'the anon key was rejected');
  expect(json?.code === '42501', `expected 42501 permission denied for anon, got ${status} ${JSON.stringify(json)?.slice(0, 120)}`);
});

await check('Edge Function create-user is deployed and admin-only', async () => {
  const { status, json } = await call('/functions/v1/create-user', { method: 'POST', body: {} });
  expect(status !== 404, 'create-user is not deployed (404)');
  expect(status === 401 && json?.error === 'forbidden', `expected 401 forbidden for anon, got ${status} ${JSON.stringify(json)?.slice(0, 120)}`);
});

const email = process.env.SUPABASE_SMOKE_EMAIL, password = process.env.SUPABASE_SMOKE_PASSWORD;
if (email && password) {
  let token;
  await check('smoke-test member can sign in', async () => {
    const { status, json } = await call('/auth/v1/token?grant_type=password', { method: 'POST', body: { email, password } });
    expect(status === 200 && json?.access_token, `sign-in returned ${status} ${json?.error_description ?? json?.msg ?? ''}`.trim());
    token = json.access_token;
  });
  if (token) {
    await check('signed-in member can read the package list', async () => {
      const { status, json } = await call('/rest/v1/membership_packages?select=name&limit=1', { token });
      expect(status === 200, `GET membership_packages returned ${status} ${JSON.stringify(json)?.slice(0, 120)}`);
      expect(Array.isArray(json) && json.length > 0, 'no membership packages found; the reference data is missing');
    });
    await call('/auth/v1/logout', { method: 'POST', token }).catch(() => {});   // best effort; the token expires on its own anyway
  }
} else {
  console.log('skip  signed-in check (SUPABASE_SMOKE_EMAIL / SUPABASE_SMOKE_PASSWORD not set)');
}

console.log(failed ? `\n${failed} check(s) failed.` : '\nSupabase is ready.');
process.exit(failed ? 1 : 0);
