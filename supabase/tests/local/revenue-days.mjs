// 0058 (owner, 2026-11-12): the Windows Revenue page shows each day's net, payments, refunds and check-ins. One row a day, so a
// 3-month period stays well under the 1,000-row limit of a read, and the figures agree with revenue_summary.
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
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`rd-${++serial}@test.io`, JSON.stringify({ full_name: 'Revenue Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee'), coach = await user('coach');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('RD Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const today = (await db.query(`select (now() at time zone 'Asia/Manila')::date::text d`)).rows[0].d;
const ago = n => (new Date(new Date(today + 'T00:00:00Z').getTime() - n * 86400000)).toISOString().slice(0, 10);
const days = (who, from, to) => as(who, 'select day::text, net, payments, refunds, visits from revenue_days($1,$2)', [from, to]);

// a verified payment on a given Manila day, and its member's check-ins
async function paid(daysAgo) {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(admin, 'select verify_payment($1,true)', [p.payment_id]);
  await db.query(`update payments set verified_at = ($2::date + time '10:00') at time zone 'Asia/Manila' where payment_id=$1`, [p.payment_id, ago(daysAgo)]);
  return { uid, payment: p.payment_id };
}
const visit = (uid, daysAgo) => db.query(`insert into gym_check_ins(member_id, checked_in_at) values ($1, ($2::date + time '18:30') at time zone 'Asia/Manila')`, [uid, ago(daysAgo)]);

try {
  const a = await paid(2), b = await paid(2), c = await paid(0);
  await visit(a.uid, 2); await visit(b.uid, 2); await visit(a.uid, 1); await visit(c.uid, 0);
  // an approved refund on day 1 for a's payment
  await db.query(`insert into refund_requests(term_id, payment_id, member_id, amount, reason, status, decided_by, decided_at)
    select t.term_id, $1, $2, 400, 'Moved away', 'approved', $4, ($3::date + time '12:00') at time zone 'Asia/Manila'
    from membership_terms t where t.payment_id=$1`, [a.payment, a.uid, ago(1), admin]);

  await test('one row for every day in the period, quiet days included, oldest first', async () => {
    const r = await days(admin, ago(3), today);
    assert.deepEqual(r.map(x => x.day), [ago(3), ago(2), ago(1), today]);
    assert.deepEqual(r.map(x => [Number(x.net), Number(x.payments), Number(x.refunds), Number(x.visits)]),
      [[0, 0, 0, 0], [2000, 2, 0, 2], [-400, 0, 400, 1], [1000, 1, 0, 1]]);
  });
  await test('each day agrees with revenue_summary', async () => {
    const sum = await as(admin, 'select day::text, sum(total) t from revenue_summary($1,$2) group by day', [ago(3), today]);
    for (const s of sum) assert.equal(Number((await days(admin, ago(3), today)).find(x => x.day === s.day).net), Number(s.t), s.day);
  });
  await test('Admin reads it too; the front desk, coaches and members are refused', async () => {
    assert.equal((await days(sub, today, today)).length, 1);
    for (const who of [desk, coach, a.uid]) await assert.rejects(as(who, 'select * from revenue_days($1,$1)', [today]), /forbidden|permission/);
  });
  await test('a backwards, missing or over-long period is refused', async () => {
    await assert.rejects(days(admin, today, ago(1)), /bad_input/);
    await assert.rejects(days(admin, null, today), /bad_input/);
    await assert.rejects(days(admin, ago(400), today), /bad_input/);
  });
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exitCode = failed ? 1 : 0;
}
