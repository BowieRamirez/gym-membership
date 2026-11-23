// 0131 (owner, 2026-11-21): a member check-in outside opening hours is refused `gym_closed`, like a coach's (0032).
// A queued offline check-in is judged by the time it happened, not the time it reaches the server.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

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
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`h-${++serial}@test.io`, JSON.stringify({ full_name: 'Hours Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('H Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
async function paid() {
  const uid = await user();
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  await as(uid, `select set_desk_pin('1234')`);
  return uid;
}
// opening hours that do / don't include the current Manila time (whole half hours, as the table requires)
const manilaMinutes = Math.floor(((Date.now() / 60000) + 8 * 60) % 1440);
const hhmm = m => `${String(Math.floor(m / 60) % 24).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
async function openNow() { await db.query(`update gym_settings set opens_at='00:00', closes_at='24:00' where id=1`); }
async function closedNow() {
  // a 1-hour window that ends at least 30 minutes before now, or starts after now when it's early in the day
  const start = manilaMinutes >= 120 ? Math.floor((manilaMinutes - 90) / 30) * 30 - 30 : Math.ceil((manilaMinutes + 60) / 30) * 30;
  await db.query(`update gym_settings set opens_at=$1, closes_at=$2 where id=1`, [hhmm(start), hhmm(start + 60)]);
}
const manual = uid => as(desk, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_pin=>'1234') id`, [uid]);

try {
  await test('inside opening hours: the desk checks a member in', async () => {
    await openNow();
    const uid = await paid();
    assert.ok((await manual(uid))[0].id);
  });
  await test('outside opening hours: refused gym_closed, nothing recorded', async () => {
    const uid = await paid();
    await closedNow();
    await assert.rejects(manual(uid), /gym_closed/);
    await assert.rejects(as(admin, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_reason=>'Late')`, [uid]), /gym_closed/);
    assert.equal((await db.query('select count(*)::int n from gym_check_ins where member_id=$1', [uid])).rows[0].n, 0);
    await openNow();
  });
  await test('a queued offline check-in is judged by when it happened', async () => {
    await openNow();
    const uid = await paid();
    await db.query(`update membership_terms set starts_at = now() - interval '3 hours' where member_id=$1`, [uid]);
    const at = new Date(Date.now() - 3600000).toISOString();
    const off = ref => as(desk, `select record_check_in($1,'offline',$2,$3,'1234') id`, [uid, at, ref]);
    const ref = randomUUID();
    const id = (await off(ref))[0].id;
    await closedNow();
    assert.equal((await off(ref))[0].id, id, 'resending an already-saved offline check-in still returns it');
    await assert.rejects(off(randomUUID()), /gym_closed/);
    await openNow();
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exit(failed ? 1 : 0);
}
