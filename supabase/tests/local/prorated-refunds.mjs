// Phase 10 of the 2026-11-12 plan, G22 (owner, 2026-11-15): a membership refund is only the unused days, counted when staff approve,
// and (G23, 0068) only within 7 days of the term's start; asking in time is what counts. 30 days for ₱1,500: ₱50 a day.
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
  [`pr-${++serial}@test.io`, JSON.stringify({ full_name: 'Refund Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach');
await openCashShifts(db);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('PR Monthly',1500,30) returning membership_package_id id`)).rows[0].id;
await db.query(`insert into discounts(code,percent,starts_at,expires_at,max_uses) values('PR10',10,now()-interval '1 day',now()+interval '1 day',100)`);
await db.query(`insert into discount_items (discount_id, kind) select discount_id, 'package' from discounts where code='PR10'`);

async function member(code = null) {
  const uid = await user();
  const pay = (await as(uid, 'select * from extend_membership($1,$2)', [pack, code]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
  const term = (await db.query('select * from membership_terms where member_id=$1', [uid])).rows[0];
  return { uid, pay, term };
}
// the term started `days` days ago (plus a minute, so the started day counts as used)
const age = (term, days) => db.query(`update membership_terms set starts_at=now()-($1*interval '1 day')-interval '1 minute',
  ends_at=now()-($1*interval '1 day')-interval '1 minute'+interval '30 days' where term_id=$2`, [days, term.term_id]);
const ask = (m) => as(m.uid, `select * from request_membership_refund($1,'Moving away')`, [m.term.term_id]).then(r => r[0]);
const approve = (r) => as(admin, `select decide_membership_refund($1,true,'Ok')`, [r.refund_request_id]);
const amount = async (r) => Number((await db.query('select amount from refund_requests where refund_request_id=$1', [r.refund_request_id])).rows[0].amount);

try {
  console.log('\nThe refund is the days left when staff approve');
  for (const [used, due] of [[1, 1450], [7, 1150]])
    await test(`${used} of 30 days used: ₱${due}`, async () => {
      const m = await member(); await age(m.term, used - 1);   // the day in progress is day `used`
      const r = await ask(m);
      assert.equal(Number(r.amount), due, 'the request saves the estimate');
      await approve(r);
      assert.equal(await amount(r), due);
    });
  await test('a term that has not started gives everything back', async () => {
    const m = await member();
    await db.query(`update membership_terms set starts_at=now()+interval '2 days', ends_at=now()+interval '32 days' where term_id=$1`, [m.term.term_id]);
    const r = await ask(m); await approve(r);
    assert.equal(await amount(r), 1500);
  });
  await test('a discounted payment (₱1,350) is split, not the list price', async () => {
    const m = await member('PR10'); await age(m.term, 6);   // 7 used, 23 left
    const r = await ask(m); await approve(r);
    assert.equal(await amount(r), 1035);   // 1350 × 23 / 30
  });

  console.log('\nOnly the first week: asking in time counts');
  await test('asking on day 8 (7 days and a minute after the start) is refused, and on day 15', async () => {
    for (const days of [7, 14]) {
      const m = await member(); await age(m.term, days);
      await assert.rejects(ask(m), /refund_week_passed/);
    }
  });
  await test('asked on day 7, approved on day 9: 21 days left, ₱1,050', async () => {
    const m = await member(); await age(m.term, 6);
    const r = await ask(m);
    await age(m.term, 8);   // two days go by before staff get to it
    await approve(r);
    assert.equal(await amount(r), 1050);
  });
  await test('approved after the term has fully ended: nothing to refund, staff reject instead', async () => {
    const m = await member(); await age(m.term, 2);
    const r = await ask(m);
    await db.query(`update membership_terms set starts_at=now()-interval '31 days', ends_at=now()-interval '1 day' where term_id=$1`, [m.term.term_id]);
    await assert.rejects(approve(r), /nothing_to_refund/);
    await as(admin, `select decide_membership_refund($1,false,'Term ended')`, [r.refund_request_id]);
  });

  console.log('\nRepayment');
  await test('record repaid pays the approved amount, and the cash shift counts that amount', async () => {
    const m = await member(); await age(m.term, 6);
    const r = await ask(m); await approve(r);
    await as(admin, `select record_membership_refund_paid($1,'cash'::payment_method,null)`, [r.refund_request_id]);
    const row = (await db.query('select status, amount, cash_shift_id from refund_requests where refund_request_id=$1', [r.refund_request_id])).rows[0];
    assert.deepEqual([row.status, Number(row.amount)], ['paid', 1150]);
    assert.ok(row.cash_shift_id, 'not tied to the drawer');
  });
  await test('a request approved before 0067 (amount = the payment) still records repaid', async () => {
    const m = await member();
    const r = await ask(m);
    // as 0018-0066 left it: approved for the whole payment
    await db.query(`update refund_requests set status='approved', amount=1500, decided_by=$2, decided_at=now() where refund_request_id=$1`, [r.refund_request_id, admin]);
    await as(admin, `select record_membership_refund_paid($1,'gcash'::payment_method,'GC-1')`, [r.refund_request_id]);
    assert.equal(await amount(r), 1500);
  });
  await test('a member, a coach or the front desk still cannot decide or record a refund', async () => {
    const m = await member(); const r = await ask(m);
    for (const who of [m.uid, coach, desk]) {
      await assert.rejects(as(who, `select decide_membership_refund($1,true,'Mine')`, [r.refund_request_id]), /forbidden|self_decision/);
      await assert.rejects(as(who, `select record_membership_refund_paid($1,'cash'::payment_method,null)`, [r.refund_request_id]), /forbidden|self_decision/);
    }
  });
  await test('the day counter is internal: the app role cannot call it', async () =>
    assert.rejects(as(admin, `select term_days_used(now(), 30, now())`), /permission denied/));
} finally {
  console.log(`\n${passed} passed, ${failed} failed`);
  await db.end(); await server.stop();
  process.exit(failed ? 1 : 0);
}
