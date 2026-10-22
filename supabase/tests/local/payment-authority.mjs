// Phase 2 (D3): voiding a payment and deciding/repaying a refund all need the payments:void grant.
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
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`p2-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach'), member = await user();
const archivedAdmin = await user('admin');
await db.query('update profiles set is_active=false where id=$1', [archivedAdmin]);
const monthly = (await db.query(`insert into membership_packages(name,price,duration_days)
  values('Phase Two Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
async function fixture() {
  const uid = await user(), pay = (await as(uid, 'select * from extend_membership($1,null)', [monthly]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  const term = (await as(uid, 'select * from membership_terms'))[0];
  const request = (await as(uid, 'select * from request_membership_refund($1,$2)', [term.term_id, 'Changed my plans']))[0];
  return { uid, pay, request };
}
const voidIt = (actor, pay) => as(actor, 'select void_payment($1,$2)', [pay.payment_id, 'Wrong payment']);
const decide = (actor, r) => as(actor, 'select decide_membership_refund($1,true,$2)', [r.refund_request_id, 'Approved']);
const repay = (actor, r) => as(actor, `select record_membership_refund_paid($1,'cash',null)`, [r.refund_request_id]);
const grant = `from role_permissions where role_id=(select role_id from roles where name='admin')
  and permission_id=(select permission_id from permissions where resource='payments' and action='void')`;

try {
  for (const [name, actor] of [['front desk', desk], ['coach', coach], ['member', member], ['archived admin', archivedAdmin]]) {
    await test(`${name} cannot void, decide or repay`, async () => {
      const { pay, request } = await fixture();
      await assert.rejects(decide(actor, request), /forbidden|inactive/);
      await decide(admin, request);
      await assert.rejects(repay(actor, request), /forbidden|inactive/);
      const other = await fixture();
      await as(admin, 'select decide_membership_refund($1,false,$2)', [other.request.refund_request_id, 'No']);
      await assert.rejects(voidIt(actor, other.pay), /forbidden|inactive/);
      assert.equal((await db.query('select status from payments where payment_id=$1', [pay.payment_id])).rows[0].status, 'verified');
    });
  }
  await test('an admin without the payments:void grant cannot void, decide or repay', async () => {
    const a = await fixture(), b = await fixture();
    await decide(admin, b.request);
    const saved = (await db.query(`select role_id, permission_id ${grant}`)).rows[0];
    await db.query(`delete ${grant}`);
    try {
      await assert.rejects(decide(admin, a.request), /forbidden/);
      await assert.rejects(repay(admin, b.request), /forbidden/);
      await assert.rejects(voidIt(admin, a.pay), /forbidden/);
    } finally {
      await db.query('insert into role_permissions(role_id,permission_id) values($1,$2)', [saved.role_id, saved.permission_id]);
    }
  });
  await test('a permitted admin records who, when and why for refund decisions and voids', async () => {
    const a = await fixture();
    await decide(admin, a.request); await repay(admin, a.request);
    const r = (await db.query('select decided_by,decided_at,decision_reason,paid_by,paid_at from refund_requests where refund_request_id=$1',
      [a.request.refund_request_id])).rows[0];
    assert.equal(r.decided_by, admin); assert.ok(r.decided_at); assert.equal(r.decision_reason, 'Approved');
    assert.equal(r.paid_by, admin); assert.ok(r.paid_at);
    const b = await fixture();
    await as(admin, 'select decide_membership_refund($1,false,$2)', [b.request.refund_request_id, 'No']);
    await voidIt(admin, b.pay);
    const p = (await db.query('select status,voided_by,voided_at,void_reason from payments where payment_id=$1', [b.pay.payment_id])).rows[0];
    assert.equal(p.status, 'voided'); assert.equal(p.voided_by, admin); assert.ok(p.voided_at); assert.equal(p.void_reason, 'Wrong payment');
    const audit = (await db.query(`select actor_id from audit_log where action='payment.void' and entity_id=$1`, [String(b.pay.payment_id)])).rows;
    assert.deepEqual(audit.map(x => x.actor_id), [admin]);
    await assert.rejects(voidIt(admin, b.pay), /already_processed/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nPayment authority: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
