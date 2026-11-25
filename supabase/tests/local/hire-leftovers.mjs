// Hiring ended in 0121, but the app still reads taken_coaches (Hired tag, 0071) and can end an old hire (end_hire, 0005). The tests that
// tried to break them went with the hiring tests (2026-11-25); these keep D16's rule that every function the app calls has one.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function asAnon(sql) {
  await db.query('begin');
  try { await db.query('set local role anon'); const r = await db.query(sql); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
async function user(role) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id`,
    [`hl-${++serial}@test.io`, JSON.stringify({ full_name: 'Hire Left' }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const member = await user('member');

try {
  await test('taken_coaches is for signed-in users only', async () => {
    assert.ok(Array.isArray(await as(member, `select * from taken_coaches()`)));
    await assert.rejects(asAnon(`select * from taken_coaches()`), /permission denied/);
  });
  await test('end_hire refuses a hire that is not yours or does not exist', async () => {
    await assert.rejects(as(member, `select end_hire(999999)`), /not_found/);
    await assert.rejects(asAnon(`select end_hire(1)`), /permission denied/);
  });
} finally {
  console.log(`Hire leftovers: ${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exit(1);
}
