// 0147 (live system test, 2026-11-25): (1) assign_role could make anyone a System admin, and revoke_role then refused to take it
// back (super_admin_protected); (2) coach access paid online after the membership was refunded took the money and gave nothing.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function asRole(role, uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query(`set local role ${role}`);
    if (uid) await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const as = (uid, sql, args) => asRole('authenticated', uid, sql, args);
const service = (sql, args) => asRole('service_role', null, sql, args);
const user = async (role, name) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`lf-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
await openCashShifts(db);
const admin = await user('admin', 'Sys Admin'), desk = await user('employee', 'Desk Dana');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days,coach_addon_price,published_at) values('X Monthly',1000,30,200,now()) returning membership_package_id id`)).rows[0].id;
const payment = async id => (await db.query('select * from payments where payment_id=$1', [id])).rows[0];
async function memberWithTerm() {
  const uid = await user('member', 'Mia Member');
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  const term = (await db.query('select * from membership_terms where member_id=$1', [uid])).rows[0];
  return { uid, term };
}
let xids = 0;

try {
  await test('1: assign_role refuses the System admin role; other roles still work', async () => {
    const m = await user('member', 'Max Member');
    await assert.rejects(as(admin, `select assign_role($1,'admin')`, [m]), /bad_input|forbidden/);
    assert.equal((await db.query(`select count(*) n from user_roles ur join roles r using(role_id) where ur.user_id=$1 and r.name='admin'`, [m])).rows[0].n, '0');
    await as(admin, `select assign_role($1,'coach')`, [m]);
    assert.equal((await db.query(`select count(*) n from user_roles ur join roles r using(role_id) where ur.user_id=$1 and r.name='coach'`, [m])).rows[0].n, '1');
  });

  await test('2: refunding the membership expires its unpaid coach access payment', async () => {
    const m = await memberWithTerm();
    const ca = (await as(m.uid, 'select * from buy_coach_access(null)'))[0];
    assert.equal(ca.status, 'pending');
    const r = (await as(m.uid, `select * from request_membership_refund($1,'Moving away')`, [m.term.term_id]))[0];
    await as(admin, `select decide_membership_refund($1,true,'Ok')`, [r.refund_request_id]);
    assert.equal((await payment(ca.payment_id)).status, 'expired');
  });

  await test('2: coach access paid online after the refund becomes an approved refund, not lost money', async () => {
    const m = await memberWithTerm();
    const ca = (await as(m.uid, 'select * from buy_coach_access(null)'))[0];
    const xid = `ps-lf-${++xids}`;
    await service('select save_online_checkout($1,$2,$3)', [ca.payment_id, xid, 'https://checkout.test/x']);
    const r = (await as(m.uid, `select * from request_membership_refund($1,'Moving away')`, [m.term.term_id]))[0];
    await as(admin, `select decide_membership_refund($1,true,'Ok')`, [r.refund_request_id]);
    await service('select confirm_online_payment($1,$2,$3)', [xid, 200, 'GCASH']);
    const refund = (await db.query(`select * from refund_requests where payment_id=$1 and kind='online'`, [ca.payment_id])).rows[0];
    assert.ok(refund, 'an online refund was opened');
    assert.deepEqual([refund.status, Number(refund.amount)], ['approved', 200]);
    assert.equal((await db.query('select coach_access_at from membership_terms where term_id=$1', [m.term.term_id])).rows[0].coach_access_at, null);
  });

  await test('2: a cancelled term leaves other members\' coach access payments alone', async () => {
    const a = await memberWithTerm(), b = await memberWithTerm();
    const pb = (await as(b.uid, 'select * from buy_coach_access(null)'))[0];
    const r = (await as(a.uid, `select * from request_membership_refund($1,'Moving away')`, [a.term.term_id]))[0];
    await as(admin, `select decide_membership_refund($1,true,'Ok')`, [r.refund_request_id]);
    assert.equal((await payment(pb.payment_id)).status, 'pending');
  });
  // 0150 (owner, 2026-11-25): approving a membership refund also refunds the unused days of paid coach access
  const paidCoachAccess = async m => {
    const ca = (await as(m.uid, 'select * from buy_coach_access(null)'))[0];
    await as(desk, 'select verify_payment($1,true)', [ca.payment_id]);
    return ca;
  };
  const refundTerm = async (m, approve = true) => {
    const r = (await as(m.uid, `select * from request_membership_refund($1,'Moving away')`, [m.term.term_id]))[0];
    await as(admin, `select decide_membership_refund($1,$2,'Ok')`, [r.refund_request_id, approve]);
  };
  await test('0150: approving a membership refund also refunds the unused coach access days, approved and ready to repay', async () => {
    const m = await memberWithTerm(); const ca = await paidCoachAccess(m);
    await refundTerm(m);
    const r = (await db.query(`select * from refund_requests where payment_id=$1`, [ca.payment_id])).rows[0];
    assert.ok(r, 'a coach access refund was opened');
    assert.equal(r.kind, 'coach_access');
    assert.equal(r.status, 'approved');
    assert.ok(Number(r.amount) > 0 && Number(r.amount) < Number(ca.amount), `prorated: ${r.amount} of ${ca.amount}`);
    await as(admin, `select record_membership_refund_paid($1,'gcash','TEST-REF')`, [r.refund_request_id]);
    assert.equal((await db.query('select status from refund_requests where refund_request_id=$1', [r.refund_request_id])).rows[0].status, 'paid');
  });
  await test('0150: no coach access, or a rejected membership refund: no coach access refund', async () => {
    const plain = await memberWithTerm();
    await refundTerm(plain);
    assert.equal((await db.query(`select count(*) n from refund_requests where member_id=$1 and kind='coach_access'`, [plain.uid])).rows[0].n, '0');
    const kept = await memberWithTerm(); await paidCoachAccess(kept);
    await refundTerm(kept, false);
    assert.equal((await db.query(`select count(*) n from refund_requests where member_id=$1 and kind='coach_access'`, [kept.uid])).rows[0].n, '0');
  });

  await test('0149: reschedule requests are gone (nothing could approve them since 0121); cancel and book again instead', async () => {
    const n = (await db.query(`select count(*) n from pg_proc where proname = 'request_reschedule' and pronamespace = 'public'::regnamespace`)).rows[0].n;
    assert.equal(n, '0');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Live test fixes 2: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
