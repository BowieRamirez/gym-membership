// G26 (owner, 2026-11-15; 0070): a paid term starts on the first day no other live term covers, so it fills a gap
// (e.g. one left by a refund) instead of queueing after the last term; a term bought ahead that it runs into moves back
// by the overlap, so every paid day is kept. Days below are whole days from now.
import { start, openCashShifts } from './harness.mjs';
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
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`fg-${++serial}@test.io`, JSON.stringify({ full_name: 'Gap Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee');
await openCashShifts(db);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('FG Monthly',1500,30) returning membership_package_id id`)).rows[0].id;

const buy = async (uid) => {
  const pay = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  return pay.payment_id;
};
// lay out a member's terms as [startDay, endDay] pairs from now (each first bought, then moved)
async function memberWith(...spans) {
  const uid = await user();
  const pids = [];
  for (const _ of spans) pids.push(await buy(uid));   // bought one after another, then laid out
  for (const [i, [s, e]] of spans.entries()) {
    const pid = pids[i];
    await db.query(`update membership_terms set starts_at=now()+$1*interval '1 day', ends_at=now()+$2*interval '1 day' where payment_id=$3`, [s, e, pid]);
  }
  return uid;
}
const days = async (uid) => (await db.query(`select round(extract(epoch from starts_at-now())/86400)::int s, round(extract(epoch from ends_at-now())/86400)::int e
  from membership_terms where member_id=$1 and cancelled_at is null order by starts_at`, [uid])).rows.map(r => [r.s, r.e]);
const active = async (uid) => (await db.query('select is_active_member($1) a', [uid])).rows[0].a;

try {
  console.log('\nA paid term fills a gap and pushes later terms back');
  await test('a running term: the new one starts when it ends (as before)', async () => {
    const uid = await memberWith([-25, 5]);
    await buy(uid);
    assert.deepEqual(await days(uid), [[-25, 5], [5, 35]]);
  });
  await test('no running term, one bought ahead in 10 days: starts today, the later one moves back 20 days', async () => {
    const uid = await memberWith([10, 40]);
    await buy(uid);
    assert.deepEqual(await days(uid), [[0, 30], [30, 60]]);
    assert.equal(await active(uid), true);
  });
  await test('a gap longer than the term: nothing moves', async () => {
    const uid = await memberWith([40, 70]);
    await buy(uid);
    assert.deepEqual(await days(uid), [[0, 30], [40, 70]]);
  });
  await test('running, gap, then one ahead: fills after the running term, the later one moves', async () => {
    const uid = await memberWith([-25, 5], [10, 40]);
    await buy(uid);
    assert.deepEqual(await days(uid), [[-25, 5], [5, 35], [35, 65]]);
  });
  await test('two terms ahead back to back both move by the same days, gap between them kept', async () => {
    const uid = await memberWith([10, 40], [45, 75]);
    await buy(uid);
    assert.deepEqual(await days(uid), [[0, 30], [30, 60], [65, 95]]);
  });
  await test('a cancelled term is not in the way and does not move', async () => {
    const uid = await memberWith([-2, 28], [28, 58]);
    await db.query(`update membership_terms set cancelled_at=now() where member_id=$1 and starts_at<now()`, [uid]);
    await buy(uid);
    assert.deepEqual(await days(uid), [[0, 30], [30, 60]]);
    const c = (await db.query(`select round(extract(epoch from starts_at-now())/86400)::int s from membership_terms where member_id=$1 and cancelled_at is not null`, [uid])).rows[0].s;
    assert.equal(c, -2);
  });
  await test('the renewal row records the real start and end', async () => {
    const uid = await memberWith([10, 40]);
    const pid = await buy(uid);
    const r = (await db.query(`select round(extract(epoch from new_starts_at-now())/86400)::int s, round(extract(epoch from new_ends_at-now())/86400)::int e
      from membership_renewals r join payments p using(renewal_id) where p.payment_id=$1`, [pid])).rows[0];
    assert.deepEqual([r.s, r.e], [0, 30]);
  });
  await test('the helper cannot be called by the app role', async () => {
    await assert.rejects(as(desk, `select next_term_start(gen_random_uuid(), now())`), /permission denied/);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exit(failed ? 1 : 0);
}
