// Cash plan 2026-11-17 (0084): a member picks cash for a package; it waits for the desk, which approves it into the drawer
// or rejects it. Online stays the default for apps that send no p_method.
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
  [`c-${++serial}@test.io`, JSON.stringify({ full_name: 'Cash Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
try {
  const desk = await user('employee'), desk2 = await user('employee');
  const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('C Monthly',1000,30) returning membership_package_id id`)).rows[0].id;

  await test('old apps (no p_method) still pay online', async () => {
    const p = (await as(await user(), 'select * from extend_membership($1,null)', [pack]))[0];
    assert.equal(p.method, 'qr');
  });

  await test('cash is pending cash with a deadline and no checkout', async () => {
    const m = await user();
    const p = (await as(m, `select * from extend_membership($1,null,'cash')`, [pack]))[0];
    assert.equal(p.method, 'cash'); assert.equal(p.status, 'pending'); assert.ok(p.expires_at);
    await assert.rejects(as(m, `select * from avail_membership($1,null,'cash')`, [pack]), /already_availed|renewal_pending/);
  });

  await test('gcash and bank are refused for packages', async () => {
    await assert.rejects(as(await user(), `select * from extend_membership($1,null,'gcash')`, [pack]), /bad_input/);
  });

  await test('approving needs an open shift; approved cash goes into the drawer and starts the term', async () => {
    const m = await user();
    const p = (await as(m, `select * from extend_membership($1,null,'cash')`, [pack]))[0];
    await assert.rejects(as(desk2, 'select verify_payment($1,true)', [p.payment_id]), /no_open_shift/);
    await openCashShifts(db);
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    const v = (await db.query('select status,cash_shift_id,receipt_no from payments where payment_id=$1', [p.payment_id])).rows[0];
    assert.equal(v.status, 'verified'); assert.ok(v.cash_shift_id); assert.ok(v.receipt_no);
    assert.equal((await db.query('select count(*)::int n from membership_terms where member_id=$1', [m])).rows[0].n, 1);
  });

  await test('rejecting needs a reason', async () => {
    const p = (await as(await user(), `select * from extend_membership($1,null,'cash')`, [pack]))[0];
    await assert.rejects(as(desk, `select verify_payment($1,false,'')`, [p.payment_id]));
    await as(desk, `select verify_payment($1,false,'Did not come in')`, [p.payment_id]);
    assert.equal((await db.query('select status from payments where payment_id=$1', [p.payment_id])).rows[0].status, 'rejected');
  });

  await test('anon cannot call the new functions', async () => {
    const r = (await db.query(`select has_function_privilege('anon','public.extend_membership(bigint,text,payment_method)','execute') a,
      has_function_privilege('authenticated','public.with_package_method(payments,payment_method)','execute') b`)).rows[0];
    assert.equal(r.a, false); assert.equal(r.b, false);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Cash for packages: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
