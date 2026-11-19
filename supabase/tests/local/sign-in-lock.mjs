// Owner fixes 2026-11-19 Q2: 10 wrong passwords for one account lock its sign-in for 5 minutes; a right password resets the count.
// The sign-in Edge Function asks sign_in_check before trying the password and reports with sign_in_result after. Only the service
// role (the function) may call them. Keyed by the lower-cased email, so an unknown email reveals nothing.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const check = async (email) => (await db.query('select sign_in_check($1) m', [email])).rows[0].m;
const result = async (email, ok) => (await db.query('select sign_in_result($1,$2) m', [email, ok])).rows[0].m;

try {
  await test('9 wrong passwords leave it open; the 10th locks it for 5 minutes', async () => {
    for (let i = 0; i < 9; i++) assert.equal(await result('Ana@Test.io', false), 0);
    assert.equal(await check('ana@test.io'), 0);
    assert.equal(await result('ana@test.io', false), 5);
    assert.equal(await check('ANA@test.io'), 5);
  });

  await test('while locked even the right password is not tried (the function stops at the check)', async () => {
    assert.ok(await check('ana@test.io') > 0);
  });

  await test('after 5 minutes it opens again with a fresh count', async () => {
    await db.query(`update sign_in_attempts set locked_until = now() - interval '1 second' where email = 'ana@test.io'`);
    assert.equal(await check('ana@test.io'), 0);
    assert.equal(await result('ana@test.io', false), 0);   // the first wrong try of a new count
    assert.equal((await db.query(`select wrong from sign_in_attempts where email = 'ana@test.io'`)).rows[0].wrong, 1);
  });

  await test('a right password resets the count', async () => {
    for (let i = 0; i < 5; i++) await result('bo@test.io', false);
    assert.equal(await result('bo@test.io', true), 0);
    assert.equal((await db.query(`select count(*)::int n from sign_in_attempts where email = 'bo@test.io'`)).rows[0].n, 0);
  });

  await test('the app and anon cannot call them or read the table', async () => {
    const r = (await db.query(`select has_function_privilege('authenticated','public.sign_in_check(text)','execute') a,
      has_function_privilege('anon','public.sign_in_result(text,boolean)','execute') b,
      has_table_privilege('authenticated','public.sign_in_attempts','select') c,
      has_function_privilege('service_role','public.sign_in_result(text,boolean)','execute') d`)).rows[0];
    assert.deepEqual([r.a, r.b, r.c, r.d], [false, false, false, true]);
  });
} finally {
  console.log(`\nsign-in-lock: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
