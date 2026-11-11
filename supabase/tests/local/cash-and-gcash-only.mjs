// Owner, 2026-11-11 (before Phase 8): the desk takes Cash or GCash only. Bank transfer is refused for any new payment or refund
// repayment; payments and refunds already recorded as bank transfer stay as history and can still be voided or changed otherwise.
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
async function user(role = 'member', name = 'Test Person') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`pay-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin'), desk = await user('employee', 'Desk Dana');
  await openCashShifts(db);
  const monthly = (await db.query(`insert into membership_packages(name,price,duration_days) values('Monthly',1500,30) returning membership_package_id`)).rows[0].membership_package_id;

  await test('the desk can no longer take a bank transfer', async () => {
    await assert.rejects(as(desk, `select * from record_counter_payment('walk_in','bank_transfer',null,null,'Bank Ben','BT-1',null)`), /bad_input/);
  });

  await test('cash and GCash still work at the desk', async () => {
    await as(desk, `select * from record_counter_payment('walk_in','cash',null,null,'Cash Cara',null,null)`);
    await as(desk, `select * from record_counter_payment('walk_in','gcash',null,null,'G Gina','REF-G-1',null)`);
  });

  await test('a refund cannot be repaid by bank transfer, but by cash or GCash it can', async () => {
    const member = await user('member', 'Refund Rita');
    const pay = (await as(member, 'select * from extend_membership($1,null)', [monthly]))[0];
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
    const term = (await as(member, 'select * from membership_terms'))[0];
    const req = (await as(member, 'select * from request_membership_refund($1,$2,$3)', [term.term_id, 'Moving away', 'change_of_mind']))[0];
    await as(admin, 'select decide_membership_refund($1,true,$2)', [req.refund_request_id, 'Approved']);
    await assert.rejects(as(admin, `select record_membership_refund_paid($1,'bank_transfer','BT-9')`, [req.refund_request_id]), /bad_input/);
    await as(admin, `select record_membership_refund_paid($1,'gcash','REF-R-1')`, [req.refund_request_id]);
  });

  await test('a bank transfer recorded before stays and can still be updated', async () => {
    const insert = `insert into payments(payment_code,kind,method,status,amount,walk_in_name,reference_no,recorded_by)
      values($1,'walk_in','bank_transfer','pending',150,'Old Transfer','BT-OLD',$2) returning payment_id`;
    await assert.rejects(db.query(insert, ['GYM-NEWBT', desk]), /bad_input/);   // even written straight to the table
    await db.query(`alter table payments disable trigger payments_no_bank_transfer`);   // stands in for a row from before 0054
    const old = (await db.query(insert, ['GYM-OLDBT', desk])).rows[0].payment_id;
    await db.query(`alter table payments enable trigger payments_no_bank_transfer`);
    // an update that names the method (same value) runs the guard, and passes: the row was bank transfer already
    await db.query(`update payments set method = method, reference_no = 'BT-OLD-2' where payment_id=$1`, [old]);
    assert.equal((await db.query(`select reference_no from payments where payment_id=$1`, [old])).rows[0].reference_no, 'BT-OLD-2');
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nCash and GCash only: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
