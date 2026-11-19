// Owner fixes 2026-11-19 Q3: a member can start at most 10 package payments in a rolling hour (cancelled or expired ones count),
// so buying and cancelling can't be spammed. The front desk's sales aren't limited.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args); await db.query('commit'); return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`bl-${++serial}@test.io`, JSON.stringify({ full_name: 'Buy Limit Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
try {
  const desk = await user('employee');
  await openCashShifts(db);
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('BL Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
  const buyAndCancel = async (m) => { const p = (await as(m, 'select * from extend_membership($1,null)', [pack]))[0]; await as(m, 'select cancel_pending_payment($1)', [p.payment_id]); };

  await test('the 11th package payment within an hour is refused; nothing is made', async () => {
    const m = await user(null);
    for (let i = 0; i < 10; i++) await buyAndCancel(m);
    await assert.rejects(as(m, 'select * from avail_membership($1,null)', [pack]), /too_many_tries/);
    assert.equal((await db.query(`select count(*)::int n from payments where member_id = $1`, [m])).rows[0].n, 10);
  });

  await test('an hour after the first, a new one is allowed again', async () => {
    const m = await user(null);
    for (let i = 0; i < 10; i++) await buyAndCancel(m);
    await db.query(`update payments set created_at = now() - interval '61 minutes' where payment_id = (select min(payment_id) from payments where member_id = $1)`, [m]);
    assert.equal((await as(m, 'select * from extend_membership($1,null)', [pack]))[0].status, 'pending');
  });

  await test('the front desk is not limited', async () => {
    const m = await user(null);
    for (let i = 0; i < 10; i++) await buyAndCancel(m);
    const p = (await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [m, pack]))[0];
    assert.equal(p.status, 'verified');
  });
} finally {
  console.log(`\nbuy-limit: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
