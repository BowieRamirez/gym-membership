// Phase 11 (D13): first and last name are stored apart and full_name is always built from them, so every screen that shows
// full_name keeps working. Old names are split once (first word, the rest). A member marks their email confirmed only from a
// session that was opened with an emailed code, never from a password sign-in. A changed sign-in email updates the username.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

let oldJuan, oldMadonna;
const { server, client: db } = await start({ beforeMigration: async (db, name) => {
  if (!name.startsWith('0035_')) return;
  const add = async (email, full) => (await db.query(`insert into auth.users(email,raw_user_meta_data) values($1,$2) returning id`,
    [email, JSON.stringify({ full_name: full })])).rows[0].id;
  oldJuan = await add('old-juan@test.io', 'Juan dela Cruz');
  oldMadonna = await add('old-madonna@test.io', 'Madonna');
} });
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = [], claims = {}) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    await db.query(`select set_config('request.jwt.claims', $1, true)`, [JSON.stringify({ sub: uid, ...claims })]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function user(meta) {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data) values($1,$2) returning id`,
    [`p11-${++serial}@test.io`, JSON.stringify(meta)])).rows[0].id;
}
const nameOf = async uid => (await db.query('select first_name, last_name, full_name from profiles where id=$1', [uid])).rows[0];
const now = () => Math.floor(Date.now() / 1000);
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

try {
  await test('existing names are split once: first word, then the rest; a one-word name keeps an empty last name', async () => {
    assert.deepEqual(await nameOf(oldJuan), { first_name: 'Juan', last_name: 'dela Cruz', full_name: 'Juan dela Cruz' });
    assert.deepEqual(await nameOf(oldMadonna), { first_name: 'Madonna', last_name: '', full_name: 'Madonna' });
  });
  await test('sign-up with first and last name builds full_name from them', async () => {
    const id = await user({ first_name: 'Mary Ann', last_name: 'Santos', full_name: 'Mary Ann Santos' });
    assert.deepEqual(await nameOf(id), { first_name: 'Mary Ann', last_name: 'Santos', full_name: 'Mary Ann Santos' });
  });
  await test('sign-up with only a full name (an older app) is split the same way', async () => {
    const id = await user({ full_name: 'Pedro Penduko' });
    assert.deepEqual(await nameOf(id), { first_name: 'Pedro', last_name: 'Penduko', full_name: 'Pedro Penduko' });
  });
  await test('a person renames themselves with set_my_name and full_name follows', async () => {
    const id = await user({ full_name: 'Ana Reyes' });
    await as(id, 'select set_my_name($1,$2)', ['Ana Maria', 'Reyes-Cruz']);
    assert.deepEqual(await nameOf(id), { first_name: 'Ana Maria', last_name: 'Reyes-Cruz', full_name: 'Ana Maria Reyes-Cruz' });
  });
  await test('names with digits or symbols, or blank parts, are refused', async () => {
    const id = await user({ full_name: 'Bad Input' });
    await assert.rejects(as(id, 'select set_my_name($1,$2)', ['Ana1', 'Reyes']));
    await assert.rejects(as(id, 'select set_my_name($1,$2)', ['Ana', '']));
    await assert.rejects(as(id, 'select set_my_name($1,$2)', [' ', 'Reyes']));
    assert.equal((await nameOf(id)).full_name, 'Bad Input');
  });
  await test('changing only full_name (an older app) splits it again', async () => {
    const id = await user({ full_name: 'Old Name' });
    await as(id, `update profiles set full_name='Rosa Maria Lopez' where id=$1`, [id]);
    assert.deepEqual(await nameOf(id), { first_name: 'Rosa', last_name: 'Maria Lopez', full_name: 'Rosa Maria Lopez' });
  });
  await test('a session opened with an emailed code confirms the sign-in email', async () => {
    const id = await user({ full_name: 'Code Person' });
    const email = (await as(id, 'select confirm_my_email() e', [], { amr: [{ method: 'otp', timestamp: now() }] }))[0].e;
    assert.equal(email, `p11-${serial}@test.io`);
    assert.equal((await db.query('select confirmed_email from profiles where id=$1', [id])).rows[0].confirmed_email, email);
  });
  await test('a recovery code session also confirms the email', async () => {
    const id = await user({ full_name: 'Reset Person' });
    await as(id, 'select confirm_my_email()', [], { amr: [{ method: 'recovery', timestamp: now() }] });
    assert.ok((await db.query('select confirmed_email from profiles where id=$1', [id])).rows[0].confirmed_email);
  });
  await test('a password session, or a code session older than 15 minutes, cannot confirm', async () => {
    const id = await user({ full_name: 'Password Person' });
    await assert.rejects(as(id, 'select confirm_my_email()', [], { amr: [{ method: 'password', timestamp: now() }] }), /code_not_verified/);
    await assert.rejects(as(id, 'select confirm_my_email()', [], { amr: [{ method: 'otp', timestamp: now() - 16 * 60 }] }), /code_not_verified/);
    await assert.rejects(as(id, 'select confirm_my_email()'), /code_not_verified/);
    assert.equal((await db.query('select confirmed_email from profiles where id=$1', [id])).rows[0].confirmed_email, null);
  });
  await test('nobody can write confirmed_email directly', async () => {
    const id = await user({ full_name: 'Sneaky Person' });
    await assert.rejects(as(id, `update profiles set confirmed_email='x@y.io' where id=$1`, [id]), /permission denied/);
  });
  await test('a changed sign-in email becomes the username, so the old address is free again', async () => {
    const id = await user({ full_name: 'Moving Person' });
    const old = `p11-${serial}@test.io`;
    await db.query(`update auth.users set email='Moved@Test.io' where id=$1`, [id]);
    assert.equal((await db.query('select username from profiles where id=$1', [id])).rows[0].username, 'moved@test.io');
    await db.query(`insert into auth.users(email,raw_user_meta_data) values($1,'{"full_name":"New Person"}')`, [old]);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  if (failed) process.exitCode = 1;
}
