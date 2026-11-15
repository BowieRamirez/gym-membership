// G30 (owner, 2026-11-15; 0073/0074): hiring a coach is a request. The coach accepts or declines; a waiting request unlocks
// nothing (messages, sessions, time requests still need an active hire). Members can ask several coaches, one open per coach.
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
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`g30-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('G30 Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
async function member(name = 'Mara Lopez') {
  const uid = await user('member', name);
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  return uid;
}
const ask = async (m, c) => (await as(m, 'select hire_coach($1) id', [c]))[0].id;
const hire = async (id) => (await db.query('select * from coach_hires where coach_hire_id=$1', [id])).rows[0];
const notes = async (uid, title) => (await db.query('select body from user_notifications where user_id=$1 and title=$2', [uid, title])).rows.map(r => r.body);
const later = new Date(Date.now() + 3 * 864e5), later1 = new Date(later.getTime() + 36e5);

try {
  console.log('\nA hire is a request until the coach accepts');
  await test('Hire makes a waiting request and tells the coach', async () => {
    const c = await user('coach'), m = await member('Mara Lopez');
    const h = await hire(await ask(m, c));
    assert.equal(h.status, 'pending');
    assert.deepEqual(await notes(c, 'New hire request'), ['Mara Lopez']);
  });
  await test('a waiting request unlocks nothing', async () => {
    const c = await user('coach'), m = await member();
    await ask(m, c);
    assert.equal((await db.query('select coaches_member($1,$2) ok', [c, m])).rows[0].ok, false);
    await assert.rejects(as(m, 'select request_time($1,$2,$3)', [c, later, later1]), /no_active_hire/);
    assert.deepEqual((await as(m, 'select taken_coaches() id')).map(r => r.id), []);   // the Hired tag means accepted
    assert.equal((await as(m, `select count(*)::int n from chat_threads() where kind='coach'`))[0].n, 0);   // no conversation yet
    assert.equal((await as(c, `select count(*)::int n from chat_threads() where kind='coach'`))[0].n, 0);
  });
  await test('one open request per coach; another coach is fine', async () => {
    const c = await user('coach'), c2 = await user('coach'), m = await member();
    await ask(m, c);
    await assert.rejects(ask(m, c), /hire_already_requested/);
    await ask(m, c2);
  });
  await test('only that coach answers, and only once; accept makes it active and tells the member', async () => {
    const c = await user('coach', 'Lena Cruz'), other = await user('coach'), m = await member();
    const id = await ask(m, c);
    await assert.rejects(as(other, 'select answer_hire($1,true)', [id]), /not_found/);
    await assert.rejects(as(m, 'select answer_hire($1,true)', [id]), /not_found/);
    await as(c, 'select answer_hire($1,true)', [id]);
    await assert.rejects(as(c, 'select answer_hire($1,false)', [id]), /already_processed/);
    assert.equal((await hire(id)).status, 'active');
    assert.deepEqual(await notes(m, 'Hire accepted'), ['Lena Cruz']);
    assert.equal((await db.query('select coaches_member($1,$2) ok', [c, m])).rows[0].ok, true);
    await assert.rejects(ask(m, c), /hire_already_requested/);   // already hired counts as open
  });
  await test('accept is refused when the member has no active membership any more', async () => {
    const c = await user('coach'), m = await member();
    const id = await ask(m, c);
    await db.query(`update membership_terms set starts_at=now()-interval '40 days', ends_at=now()-interval '10 days' where member_id=$1`, [m]);
    await assert.rejects(as(c, 'select answer_hire($1,true)', [id]), /no_active_membership/);
    assert.equal((await hire(id)).status, 'pending');
  });
  await test('decline ends it, tells the member, and the member may ask again', async () => {
    const c = await user('coach', 'Kai Mendoza'), m = await member();
    const id = await ask(m, c);
    await as(c, 'select answer_hire($1,false)', [id]);
    const h = await hire(id);
    assert.equal(h.status, 'declined'); assert.ok(h.ended_at);
    assert.deepEqual(await notes(m, 'Hire declined'), ['Kai Mendoza']);
    await ask(m, c);
  });
  await test('the member cancels their own waiting request only, then may ask again', async () => {
    const c = await user('coach'), m = await member(), m2 = await member();
    const id = await ask(m, c);
    await assert.rejects(as(m2, 'select cancel_hire_request($1)', [id]), /not_found/);
    await assert.rejects(as(c, 'select cancel_hire_request($1)', [id]), /not_found/);
    await as(m, 'select cancel_hire_request($1)', [id]);
    await assert.rejects(as(m, 'select cancel_hire_request($1)', [id]), /already_processed/);
    assert.equal((await hire(id)).status, 'cancelled');
    await assert.rejects(as(c, 'select answer_hire($1,true)', [id]), /already_processed/);
    await ask(m, c);
  });
  await test('archiving either account cancels its waiting requests', async () => {
    const c = await user('coach'), m = await member(), c2 = await user('coach'), m2 = await member();
    const a = await ask(m, c), b = await ask(m2, c2);
    await as(admin, `select archive_account($1,'Left the gym')`, [m]);
    await as(admin, `select archive_account($1,'Left the gym')`, [c2]);
    assert.equal((await hire(a)).status, 'cancelled');
    assert.equal((await hire(b)).status, 'cancelled');
  });
  await test('the new functions are not open to the public', async () => {
    for (const sql of ['select answer_hire(1,true)', 'select cancel_hire_request(1)']) {
      await db.query('begin'); await db.query('set local role anon');
      await assert.rejects(db.query(sql), /permission denied/);
      await db.query('rollback');
    }
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nHire requests: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
