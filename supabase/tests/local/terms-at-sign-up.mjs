// Phase 11 of the 2026-11-12 plan, G24/G25 (owner, 2026-11-15): a member who signs up must agree to the Terms and Policy; the server
// saves which version and when, and refuses a self sign-up without it. Accounts an admin makes (a role in app metadata) aren't asked.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
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
// a self sign-up as Supabase Auth makes it: no role in app metadata
const signUp = (meta) => db.query(`insert into auth.users(email, raw_user_meta_data, raw_app_meta_data) values($1, $2, '{"provider":"email"}') returning id`,
  [`ts-${++serial}@test.io`, JSON.stringify({ first_name: 'Terry', last_name: 'Santos', full_name: 'Terry Santos', ...meta })]).then(r => r.rows[0].id);

try {
  console.log('\nSigning up');
  await test('a self sign-up without the terms version is refused', async () => {
    await assert.rejects(signUp({}), /terms_required/);
    await assert.rejects(signUp({ terms_version: '  ' }), /terms_required/);
  });
  await test('with it, the profile keeps the version and when it was agreed', async () => {
    const id = await signUp({ terms_version: '2026-11-15' });
    const p = (await db.query('select terms_version, terms_accepted_at from profiles where id=$1', [id])).rows[0];
    assert.equal(p.terms_version, '2026-11-15');
    assert.ok(Date.now() - p.terms_accepted_at < 60000, 'no time saved');
  });
  await test('an account an admin makes (role in app metadata) is not asked', async () => {
    const id = (await db.query(`insert into auth.users(email, raw_user_meta_data, raw_app_meta_data) values('staff-made@test.io', '{"full_name":"Desk Person"}', '{"role":"employee"}') returning id`)).rows[0].id;
    assert.equal((await db.query('select terms_version from profiles where id=$1', [id])).rows[0].terms_version, null);
  });
  await test('an account made by create-user is not asked: the Admin API inserts it without a role, then writes the role', async () => {
    const id = (await db.query(`insert into auth.users(email, raw_user_meta_data, raw_app_meta_data) values('two-step@test.io', $1, '{"provider":"email"}') returning id`,
      [JSON.stringify({ username: 'two-step@test.io', full_name: 'Two Step', first_name: 'Two', last_name: 'Step', must_change_password: true })])).rows[0].id;
    await db.query(`update auth.users set raw_app_meta_data = raw_app_meta_data || '{"role":"coach"}'::jsonb where id=$1`, [id]);
    assert.equal((await db.query('select terms_version from profiles where id=$1', [id])).rows[0].terms_version, null);
  });
  await test('an account made in the Supabase dashboard (no name, no role) is not asked either, as 0059 left it', async () => {
    const id = (await db.query(`insert into auth.users(email, raw_app_meta_data) values('dashboard@test.io', '{"provider":"email"}') returning id`)).rows[0].id;
    assert.equal((await db.query('select terms_version from profiles where id=$1', [id])).rows[0].terms_version, null);
  });

  console.log('\nWho can see or change it');
  await test('a member reads their own acceptance but cannot change it', async () => {
    const id = await signUp({ terms_version: '2026-11-15' });
    assert.equal((await as(id, 'select terms_version from profiles where id=$1', [id]))[0].terms_version, '2026-11-15');
    await assert.rejects(as(id, `update profiles set terms_version='forged' where id=$1`, [id]), /permission denied/);
    await assert.rejects(as(id, `update profiles set terms_accepted_at=now() - interval '1 year' where id=$1`, [id]), /permission denied/);
  });
  await test('another member cannot see it', async () => {
    const a = await signUp({ terms_version: '2026-11-15' }), b = await signUp({ terms_version: '2026-11-15' });
    assert.equal((await as(b, 'select terms_version from profiles where id=$1', [a])).length, 0);
  });
  await test('the app role cannot call the two sign-up helpers', async () => {
    const id = await signUp({ terms_version: '2026-11-15' });
    await assert.rejects(as(id, 'select require_sign_up_terms()'), /permission denied|trigger functions can only be called/);
    await assert.rejects(as(id, 'select save_sign_up_terms()'), /permission denied|trigger functions can only be called/);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exit(failed ? 1 : 0);
}
