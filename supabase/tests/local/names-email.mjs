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
async function staff(role) {
  return (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
    ['p11-' + (++serial) + '@test.io', JSON.stringify({ full_name: 'Staff Person' }), JSON.stringify({ role })])).rows[0].id;
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
  await test('nobody can change their own name: no function, and the columns are locked for signed-in users', async () => {
    const id = await user({ full_name: 'Ana Reyes' });
    await assert.rejects(as(id, 'select set_my_name($1,$2)', ['Ana Maria', 'Reyes']), /does not exist/);
    for (const column of ['first_name', 'last_name', 'full_name'])
      await assert.rejects(as(id, `update profiles set ${column}='Changed' where id=$1`, [id]), /permission denied/);
    assert.deepEqual(await nameOf(id), { first_name: 'Ana', last_name: 'Reyes', full_name: 'Ana Reyes' });
  });
  await test('staff are locked too, including a system admin editing the table directly', async () => {
    for (const role of ['employee', 'subadmin', 'admin']) {
      const id = await staff(role);
      await assert.rejects(as(id, "update profiles set full_name='Changed Name' where id=$1", [id]), /permission denied/);
    }
  });
  await test('other columns a person may edit still work', async () => {
    const id = await user({ full_name: 'Phone Person' });
    await as(id, "update profiles set phone='09170000000' where id=$1", [id]);
    assert.equal((await db.query('select phone from profiles where id=$1', [id])).rows[0].phone, '09170000000');
  });
  await test('a system admin corrects a name with a reason, full_name follows, and the audit log keeps old, new, reason and who', async () => {
    const admin = await staff('admin'), id = await user({ full_name: 'Maria Sntos' });
    await as(admin, 'select correct_name($1,$2,$3,$4)', [id, ' Maria ', ' Santos ', ' Typo at sign-up ']);
    assert.deepEqual(await nameOf(id), { first_name: 'Maria', last_name: 'Santos', full_name: 'Maria Santos' });
    const log = (await db.query("select actor_id, details from audit_log where action='user.correct_name' and entity='profiles' and entity_id=$1", [id])).rows;
    assert.equal(log.length, 1);
    assert.equal(log[0].actor_id, admin);
    assert.deepEqual(log[0].details, { from: 'Maria Sntos', to: 'Maria Santos', reason: 'Typo at sign-up' });
  });
  await test('a system admin can correct their own name', async () => {
    const admin = await staff('admin');
    await as(admin, 'select correct_name($1,$2,$3,$4)', [admin, 'Dana', 'Reyes', 'Typo']);
    assert.equal((await nameOf(admin)).full_name, 'Dana Reyes');
  });
  await test('only a system admin may correct: admin, front desk, coach and member are refused, and nothing changes', async () => {
    const id = await user({ full_name: 'Target Person' });
    for (const role of ['subadmin', 'employee', 'coach', 'member']) {
      const actor = await staff(role);
      await assert.rejects(as(actor, 'select correct_name($1,$2,$3,$4)', [id, 'New', 'Name', 'Reason']), /forbidden|permission/);
    }
    await assert.rejects(as(id, 'select correct_name($1,$2,$3,$4)', [id, 'New', 'Name', 'Reason']), /forbidden|permission/);
    assert.equal((await nameOf(id)).full_name, 'Target Person');
  });
  await test('anonymous cannot call it', async () => {
    await db.query('begin'); await db.query('set local role anon');
    await assert.rejects(db.query("select correct_name(gen_random_uuid(),'A','B','C')"), /permission denied/);
    await db.query('rollback');
  });
  await test('a correction needs valid names and a reason of 1 to 500 characters', async () => {
    const admin = await staff('admin'), id = await user({ full_name: 'Bad Input' });
    for (const [first, last, reason, why] of [['Ana1', 'Reyes', 'Typo', /23514/], ['Ana', '', 'Typo', /bad_name/], [' ', 'Reyes', 'Typo', /bad_name/],
        [null, 'Reyes', 'Typo', /bad_name/], ['Ana', 'Reyes', '   ', /bad_correction_reason/], ['Ana', 'Reyes', null, /bad_correction_reason/], ['Ana', 'Reyes', 'x'.repeat(501), /bad_correction_reason/]])
      await assert.rejects(as(admin, 'select correct_name($1,$2,$3,$4)', [id, first, last, reason]), e => why.test(e.message) || why.test(e.code));
    assert.equal((await nameOf(id)).full_name, 'Bad Input');
  });
  await test('hostile text in a correction is refused or stored as plain text', async () => {
    const admin = await staff('admin'), id = await user({ full_name: 'Plain Text' });
    const attack = "x'; drop table profiles; --";
    await assert.rejects(as(admin, 'select correct_name($1,$2,$3,$4)', [id, attack, 'Reyes', 'Typo']));
    await as(admin, 'select correct_name($1,$2,$3,$4)', [id, 'Plain', 'Texts', attack]);
    const log = (await db.query("select details->>'reason' as reason from audit_log where action='user.correct_name' and entity_id=$1", [id])).rows[0];
    assert.equal(log.reason, attack);
  });
  await test('an unknown or archived account cannot be corrected', async () => {
    const admin = await staff('admin'), gone = await user({ full_name: 'Gone Person' });
    await assert.rejects(as(admin, "select correct_name(gen_random_uuid(),'A','B','C')"), /not_found/);
    await db.query('update profiles set is_active=false where id=$1', [gone]);
    await assert.rejects(as(admin, 'select correct_name($1,$2,$3,$4)', [gone, 'New', 'Name', 'Reason']), /record_archived/);
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
