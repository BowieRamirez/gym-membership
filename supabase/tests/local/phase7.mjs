import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import pg from 'pg';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = [], client = db) {
  await client.query('begin');
  try {
    await client.query('set local role authenticated');
    await client.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await client.query(sql, args);
    await client.query('commit');
    return r.rows;
  } catch (e) { await client.query('rollback'); throw e; }
}
async function user(role = 'member') {
  const n = ++serial;
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`p7-${n}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), stranger = await user();
const [monthly, annual] = (await db.query(`insert into membership_packages(name,price,duration_days)
  values('Phase Seven Monthly',1000,30),('Phase Seven Annual',10000,365) returning membership_package_id`)).rows.map(x => x.membership_package_id);
await db.query(`insert into discounts(code,percent,starts_at,expires_at,max_uses) values('PHASE7',10,now()-interval '1 day',now()+interval '1 day',100)`);
const extend = async (uid, pkg = monthly, discount = null, client = db) =>
  (await as(uid, `select * from extend_membership($1,$2)`, [pkg, discount], client))[0];
async function verify(pay) { await as(desk, 'select verify_payment($1,true)', [pay.payment_id]); }
async function terms(uid) { return await as(uid, 'select * from membership_terms order by starts_at,term_id'); }
async function active(uid) { return (await as(uid, 'select is_active_member() active'))[0].active; }
async function fixture(pkg = monthly, discount = null) {
  const uid = await user(), pay = await extend(uid, pkg, discount);
  await verify(pay);
  return {uid, pay, term: (await terms(uid))[0]};
}
async function request(uid, term) { return (await as(uid,'select * from request_membership_refund($1,$2)',[term.term_id,'Changed my plans']))[0]; }
async function decide(id, approve = true) { await as(admin,'select decide_membership_refund($1,$2,$3)',[id,approve,approve ? 'Approved' : 'Not approved']); }
async function paid(id, method = 'cash', reference = null) { await as(admin,'select record_membership_refund_paid($1,$2,$3)',[id,method,reference]); }

try {
  await test('unverified extension grants no term or access and another pending purchase is refused', async () => {
    const uid = await user(), pay = await extend(uid);
    assert.equal(pay.status,'pending'); assert.equal((await terms(uid)).length,0); assert.equal(await active(uid),false);
    await assert.rejects(extend(uid,annual), /already_availed|renewal_pending/);
  });
  await test('verification creates a 30-day term with the original discounted price and package snapshot', async () => {
    const uid = await user(), p = await extend(uid, monthly, 'PHASE7');
    await db.query('update membership_packages set name=$1,price=2000,duration_days=31 where membership_package_id=$2',['Edited Monthly',monthly]);
    await verify(p);
    const t = (await terms(uid))[0];
    assert.equal(t.package_name,'Phase Seven Monthly'); assert.equal(t.duration_days,30); assert.equal(Number(t.amount_paid),900);
    assert.equal((t.ends_at-t.starts_at)/864e5,30); assert.equal(await active(uid),true);
    await db.query('update membership_packages set name=$1,price=1000,duration_days=30 where membership_package_id=$2',['Phase Seven Monthly',monthly]);
  });
  await test('a different package queues without replacing current access or dates', async () => {
    const {uid, term} = await fixture(); const p = await extend(uid,annual); await verify(p);
    const [first,next] = await terms(uid);
    assert.equal(first.package_name,'Phase Seven Monthly'); assert.equal(+first.ends_at,+term.ends_at);
    assert.equal(+next.starts_at,+first.ends_at); assert.equal((next.ends_at-next.starts_at)/864e5,365);
    assert.equal(next.package_name,'Phase Seven Annual'); assert.equal(Number(next.amount_paid),10000); assert.equal(await active(uid),true);
  });
  await test('expired unpaid QR cannot be verified or create entitlement', async () => {
    const uid = await user(), p = await extend(uid);
    await db.query(`update payments set expires_at=now()-interval '1 second' where payment_id=$1`,[p.payment_id]);
    await assert.rejects(verify(p),/expired|already_processed/); assert.equal((await terms(uid)).length,0);
  });
  await test('delayed verification starts after the last paid term or verification, not the request date', async () => {
    const {uid, term} = await fixture(); const p = await extend(uid);
    await db.query(`update membership_terms set starts_at=now()-interval '31 days',ends_at=now()-interval '1 day' where term_id=$1`,[term.term_id]);
    const before = Date.now(); await verify(p);
    const next = (await terms(uid)).at(-1); assert.ok(+next.starts_at >= before-1000); assert.equal((next.ends_at-next.starts_at)/864e5,30);
  });
  await test('pending combined request refunds only actual paid amount and does not stop access', async () => {
    const {uid, term} = await fixture(monthly,'PHASE7'), r = await request(uid,term);
    assert.equal(r.status,'pending'); assert.equal(Number(r.amount),900); assert.equal(await active(uid),true);
    await assert.rejects(request(uid,term),/already_requested|already_processed/);
  });
  await test('deadline is strict at exactly 24h, permits one instant before it, and rejects later requests', async () => {
    for (const [elapsed,eligible] of [[86399,true],[86400,false],[86401,false]]) {
      const {uid,pay,term} = await fixture();
      await db.query('begin');
      try {
        await db.query(`update payments set verified_at=now()-($1*interval '1 second') where payment_id=$2`,[elapsed,pay.payment_id]);
        await db.query('set local role authenticated'); await db.query(`select set_config('request.jwt.claim.sub',$1,true)`,[uid]);
        if (eligible) assert.equal((await db.query(`select * from request_membership_refund($1,'Boundary')`,[term.term_id])).rows[0].status,'pending');
        else await assert.rejects(db.query(`select * from request_membership_refund($1,'Boundary')`,[term.term_id]),/refund_window_closed/);
      } finally { await db.query('rollback'); }
    }
  });
  await test('ownership, admin authorization, read privacy and direct-write restrictions are enforced', async () => {
    const {uid,term} = await fixture();
    assert.equal((await terms(stranger)).length,0);
    await assert.rejects(request(stranger,term),/not_found|forbidden/);
    const r = await request(uid,term);
    assert.equal((await as(stranger,'select * from refund_requests')).length,0);
    assert.equal((await as(desk,'select * from refund_requests')).length,0);
    await assert.rejects(as(uid,`update membership_terms set cancelled_at=now()`),/permission denied/);
    await assert.rejects(as(admin,`update refund_requests set amount=1`),/permission denied/);
    await assert.rejects(as(desk,'select decide_membership_refund($1,true,$2)',[r.refund_request_id,'Yes']),/forbidden/);
    await assert.rejects(as(uid,'select record_membership_refund_paid($1,$2,null)',[r.refund_request_id,'cash']),/forbidden/);
  });
  await test('rejection preserves access and approval later than the deadline cancels only its term', async () => {
    const a = await fixture(), rejected = await request(a.uid,a.term); await decide(rejected.refund_request_id,false);
    assert.equal(await active(a.uid),true);
    const b = await fixture(), pending = await request(b.uid,b.term);
    await db.query(`update payments set verified_at=now()-interval '2 days' where payment_id=$1`,[b.pay.payment_id]);
    await decide(pending.refund_request_id);
    assert.equal(await active(b.uid),false); assert.ok((await terms(b.uid))[0].cancelled_at);
    await assert.rejects(decide(pending.refund_request_id),/already_processed/);
  });
  await test('cancelling current term preserves future dates and a real gap; cancelling future leaves current active', async () => {
    const a = await fixture(); const nextPay = await extend(a.uid,annual); await verify(nextPay);
    const next = (await terms(a.uid))[1]; const r = await request(a.uid,a.term); await decide(r.refund_request_id);
    const after = (await terms(a.uid))[1]; assert.equal(+after.starts_at,+next.starts_at); assert.equal(+after.ends_at,+next.ends_at); assert.equal(after.cancelled_at,null);
    assert.equal(await active(a.uid),false);
    await db.query(`update membership_terms set starts_at=now()-interval '1 second',ends_at=now()+interval '365 days' where term_id=$1`,[next.term_id]);
    assert.equal(await active(a.uid),true);
    const b = await fixture(); const bp = await extend(b.uid,annual); await verify(bp);
    const future = (await terms(b.uid))[1], br = await request(b.uid,future); await decide(br.refund_request_id);
    assert.equal(await active(b.uid),true); assert.equal((await terms(b.uid))[0].cancelled_at,null);
  });
  await test('void and refund are mutually exclusive, including pending approval and repayment', async () => {
    const a = await fixture(), r = await request(a.uid,a.term);
    await assert.rejects(as(admin,'select void_payment($1,$2)',[a.pay.payment_id,'Void']),/refund_in_progress/);
    await decide(r.refund_request_id); await paid(r.refund_request_id);
    await assert.rejects(as(admin,'select void_payment($1,$2)',[a.pay.payment_id,'Void']),/refund_in_progress/);
    const b = await fixture(); await as(admin,'select void_payment($1,$2)',[b.pay.payment_id,'Wrong payment']);
    await assert.rejects(request(b.uid,b.term),/not_refundable|already_processed/); assert.equal(await active(b.uid),false);
  });
  await test('manual repayment is separate, validates method/reference, and cannot happen twice', async () => {
    const {uid,term} = await fixture(), r = await request(uid,term);
    await assert.rejects(paid(r.refund_request_id),/already_processed/); await decide(r.refund_request_id);
    assert.equal((await as(uid,'select status,paid_at from refund_requests where refund_request_id=$1',[r.refund_request_id]))[0].paid_at,null);
    await assert.rejects(paid(r.refund_request_id,'qr'),/bad_input/);
    await assert.rejects(paid(r.refund_request_id,'gcash'),/bad_input/);
    await paid(r.refund_request_id,'gcash','RETURN-P7');
    const result = (await as(uid,'select * from refund_requests where refund_request_id=$1',[r.refund_request_id]))[0];
    assert.equal(result.status,'paid'); assert.equal(result.paid_by,admin); assert.equal(result.repayment_reference,'RETURN-P7');
    await assert.rejects(paid(r.refund_request_id),/already_processed/);
  });
  await test('approval reduces net revenue at once, and repayment reduces only the cash drawer', async () => {
    const a = await fixture(monthly,'PHASE7'), r = await request(a.uid,a.term);
    const day = (await db.query(`select (now() at time zone 'Asia/Manila')::date d`)).rows[0].d;
    const total = async () => (await as(admin,'select * from revenue_summary($1,$1)',[day])).reduce((n,x)=>n+Number(x.total),0);
    const cash = async () => (await as(desk,'select * from daily_cash_summary($1)',[day])).reduce((n,x)=>n+Number(x.total),0);
    const before = await total(), cashBefore = await cash();
    await decide(r.refund_request_id);
    assert.equal(await total(),before-900); assert.equal(await cash(),cashBefore);
    await paid(r.refund_request_id);
    assert.equal(await total(),before-900); assert.equal(await cash(),cashBefore-900);
    assert.equal((await as(a.uid,'select status from payments where payment_id=$1',[a.pay.payment_id]))[0].status,'verified');
    const b = await fixture(monthly,'PHASE7'), pending = await request(b.uid,b.term), withB = await total();
    assert.equal(withB,before-900+900);   // the new receipt, with its request still pending, counts in full
    await decide(pending.refund_request_id,false); assert.equal(await total(),withB);
  });
  await test('voiding an earlier payment never deletes or moves a later paid term', async () => {
    const a = await fixture(), p = await extend(a.uid,annual); await verify(p);
    const next = (await terms(a.uid))[1];
    await as(admin,'select void_payment($1,$2)',[a.pay.payment_id,'Wrong payment']);
    assert.equal(await active(a.uid),false);
    const after = (await terms(a.uid))[1]; assert.equal(after.cancelled_at,null); assert.equal(+after.starts_at,+next.starts_at); assert.equal(+after.ends_at,+next.ends_at);
  });
  await test('new internal helpers and renamed legacy functions are not remotely callable', async () => {
    for (const sql of [
      `select create_membership_extension('${stranger}',${monthly},null)`,
      `select project_membership_terms('${stranger}')`,
      `select verify_payment_before_terms(1,true,null)`,
      `select void_payment_before_terms(1,'Bypass')`,
      `select record_counter_payment_before_terms('membership','cash','${stranger}',${monthly},null,null,null)`
    ]) await assert.rejects(as(admin,sql),/permission denied/);
  });
  await test('simultaneous extension submissions create only one pending purchase', async () => {
    const uid = await user(); const connection = new pg.Client(db.connectionParameters); await connection.connect();
    try {
      const results = await Promise.allSettled([extend(uid,monthly,null,db),extend(uid,annual,null,connection)]);
      assert.equal(results.filter(x=>x.status==='fulfilled').length,1); assert.equal(results.filter(x=>x.status==='rejected').length,1);
      assert.equal((await as(uid,`select * from payments where status='pending' and kind in ('membership','renewal')`)).length,1);
    } finally { await connection.end(); }
  });
  await test('simultaneous approval and void cannot both succeed', async () => {
    const a = await fixture(), r = await request(a.uid,a.term);
    const connection = new pg.Client(db.connectionParameters); await connection.connect();
    try {
      const results = await Promise.allSettled([decide(r.refund_request_id),as(admin,'select void_payment($1,$2)',[a.pay.payment_id,'Race'],connection)]);
      assert.equal(results[0].status,'fulfilled'); assert.equal(results[1].status,'rejected');
      assert.equal((await as(a.uid,'select status from refund_requests where refund_request_id=$1',[r.refund_request_id]))[0].status,'approved');
    } finally { await connection.end(); }
  });
  // --- review fixes: self-decision, zero amounts, the other races, counter payments, old clients, nightly jobs ---
  const second = async work => { const c = new pg.Client(db.connectionParameters); await c.connect(); try { return await work(c); } finally { await c.end(); } };
  const refundOf = async id => (await db.query('select * from refund_requests where refund_request_id=$1',[id])).rows[0];
  await test('an administrator cannot decide or repay their own refund request', async () => {
    const self = await user('admin'); await db.query('insert into members(user_id) values($1)',[self]);
    const pay = await extend(self); await verify(pay);
    const r = await request(self,(await db.query('select * from membership_terms where member_id=$1',[self])).rows[0]);
    await assert.rejects(as(self,'select decide_membership_refund($1,true,$2)',[r.refund_request_id,'Mine']),/self_decision/);
    await decide(r.refund_request_id);
    await assert.rejects(as(self,'select record_membership_refund_paid($1,$2,null)',[r.refund_request_id,'cash']),/self_decision/);
    await paid(r.refund_request_id); assert.equal((await refundOf(r.refund_request_id)).status,'paid');
  });
  await test('a zero-amount payment cannot be refunded', async () => {
    const {uid,pay,term} = await fixture();
    await db.query('update payments set amount=0 where payment_id=$1',[pay.payment_id]);
    await assert.rejects(request(uid,term),/not_refundable/);
  });
  await test('a deactivated member cannot request a refund', async () => {
    const {uid,term} = await fixture();
    await db.query('update profiles set is_active=false where id=$1',[uid]);
    await assert.rejects(request(uid,term),/forbidden/);
  });
  await test('two simultaneous refund requests for one term create one request', async () => {
    const {uid,term} = await fixture();
    await second(async c => {
      const results = await Promise.allSettled([request(uid,term),as(uid,'select * from request_membership_refund($1,$2)',[term.term_id,'Race'],c)]);
      assert.equal(results.filter(x=>x.status==='fulfilled').length,1);
    });
    assert.equal((await db.query('select * from refund_requests where term_id=$1',[term.term_id])).rowCount,1);
  });
  await test('simultaneous approve and reject: exactly one decision wins and the term matches it', async () => {
    const {uid,term} = await fixture(), r = await request(uid,term);
    await second(async c => {
      const results = await Promise.allSettled([decide(r.refund_request_id,true),as(admin,'select decide_membership_refund($1,false,$2)',[r.refund_request_id,'No'],c)]);
      assert.equal(results.filter(x=>x.status==='fulfilled').length,1);
    });
    const after = await refundOf(r.refund_request_id), cancelled = (await terms(uid))[0].cancelled_at !== null;
    assert.ok(['approved','rejected'].includes(after.status)); assert.equal(cancelled, after.status === 'approved');
  });
  await test('simultaneous repayment records the money going out once', async () => {
    const {uid,term} = await fixture(), r = await request(uid,term); await decide(r.refund_request_id);
    await second(async c => {
      const results = await Promise.allSettled([paid(r.refund_request_id),as(admin,'select record_membership_refund_paid($1,$2,null)',[r.refund_request_id,'cash'],c)]);
      assert.equal(results.filter(x=>x.status==='fulfilled').length,1);
    });
    assert.equal((await refundOf(r.refund_request_id)).status,'paid');
    assert.equal((await db.query(`select * from audit_log where action='refund.paid' and entity_id=$1`,[String(r.refund_request_id)])).rowCount,1);
  });
  await test('a counter-payment membership refunds inside 24h and not after', async () => {
    const buy = async () => { const uid = await user();
      const p = (await as(desk,`select * from record_counter_payment('membership','cash',$1,$2)`,[uid,monthly]))[0]; return {uid,p,term:(await terms(uid))[0]}; };
    const a = await buy(); assert.equal(a.term.payment_id,a.p.payment_id);
    const r = await request(a.uid,a.term); assert.equal(r.status,'pending'); assert.equal(Number(r.amount),1000);
    const b = await buy(); await db.query(`update payments set verified_at=now()-interval '25 hours' where payment_id=$1`,[b.p.payment_id]);
    await assert.rejects(request(b.uid,b.term),/refund_window_closed/);
  });
  await test('older clients: avail_membership then renew_membership then verify still queue exact paid terms', async () => {
    const uid = await user(); const first = (await as(uid,'select * from avail_membership($1)',[monthly]))[0]; await verify(first);
    const renewal = (await as(uid,'select * from renew_membership($1)',[first.user_membership_package_id]))[0]; await verify(renewal);
    const [one,two] = await terms(uid); assert.equal(+two.starts_at,+one.ends_at); assert.equal((two.ends_at-two.starts_at)/864e5,30);
    assert.equal(await active(uid),true);
  });
  await test('nightly expiry and reminders cope with a refunded current term and a queued term', async () => {
    const a = await fixture(); const queued = await extend(a.uid,annual); await verify(queued);
    const [t1,t2] = await terms(a.uid); const r = await request(a.uid,t1); await decide(r.refund_request_id);
    await db.query(`update membership_terms set ends_at=now()+interval '3 days',starts_at=now()-interval '1 day' where term_id=$1`,[t2.term_id]);
    await db.query('select expire_memberships()'); await db.query('select send_expiry_reminders()');
    const byId = Object.fromEntries((await terms(a.uid)).map(t=>[t.term_id,t])); assert.ok(byId[t1.term_id].cancelled_at); assert.equal(byId[t2.term_id].cancelled_at,null);
    assert.equal(await active(a.uid),true);
    assert.equal((await db.query(`select * from user_notifications where user_id=$1 and reference->>'reminder'='3d'`,[a.uid])).rowCount,1);
    assert.equal((await db.query(`select status from user_membership_packages where member_id=$1`,[a.uid])).rows[0].status,'active');
    // a term followed straight away by a queued one is not "ending"
    const b = await fixture(); const nextPay = await extend(b.uid,annual); await verify(nextPay);
    const [c1] = await terms(b.uid);
    await db.query(`update membership_terms set starts_at=starts_at-(ends_at-now()-interval '3 days'),ends_at=now()+interval '3 days' where term_id=$1`,[c1.term_id]);
    await db.query(`update membership_terms set starts_at=(select ends_at from membership_terms where term_id=$2) where member_id=$1 and term_id<>$2`,[b.uid,c1.term_id]);
    await db.query('select send_expiry_reminders()');
    assert.equal((await db.query(`select * from user_notifications where user_id=$1 and reference->>'reminder'='3d'`,[b.uid])).rowCount,0);
  });
  // --- 0019: a member can withdraw a payment nobody has verified yet ---
  const cancelPending = (uid, pay, client = db) => as(uid, 'select cancel_pending_payment($1)', [pay.payment_id], client);
  const payRow = async pay => (await db.query('select * from payments where payment_id=$1', [pay.payment_id])).rows[0];
  await test('a member can cancel their own unverified first purchase and then buy again', async () => {
    const uid = await user(), pay = await extend(uid);
    await cancelPending(uid, pay);
    const row = await payRow(pay); assert.equal(row.status, 'rejected'); assert.equal(row.rejected_reason, 'Cancelled by member');
    assert.equal((await db.query('select status from user_membership_packages where member_id=$1', [uid])).rows[0].status, 'cancelled');
    assert.equal((await terms(uid)).length, 0);
    const again = await extend(uid, annual); assert.equal(again.status, 'pending');
    assert.equal((await db.query(`select * from audit_log where action='payment.cancel' and entity_id=$1`, [String(pay.payment_id)])).rowCount, 1);
  });
  await test('cancelling an unverified extension leaves the paid term alone and frees the member to extend again', async () => {
    const {uid, term} = await fixture(), pay = await extend(uid, annual);
    await as(uid, `select submit_payment_proof($1,'REF-1','proofs/x.png')`, [pay.payment_id]);
    await cancelPending(uid, pay);
    assert.equal((await payRow(pay)).status, 'rejected');
    assert.equal((await db.query('select status from membership_renewals where renewal_id=$1', [pay.renewal_id])).rows[0].status, 'cancelled');
    const [only] = await terms(uid); assert.equal(only.term_id, term.term_id); assert.equal(only.cancelled_at, null); assert.equal(await active(uid), true);
    assert.equal((await extend(uid, annual)).status, 'pending');
  });
  await test('only the payment owner can cancel, and only while it is unverified', async () => {
    const uid = await user(), pay = await extend(uid);
    await assert.rejects(cancelPending(stranger, pay), /not_found/);
    await assert.rejects(cancelPending(desk, pay), /not_found/);
    assert.equal((await payRow(pay)).status, 'pending');
    await verify(pay);
    await assert.rejects(cancelPending(uid, pay), /already_processed/);
    assert.equal((await payRow(pay)).status, 'verified'); assert.equal(await active(uid), true);
  });
  await test('an amenity payment cannot be cancelled this way', async () => {
    const {uid} = await fixture(), amenity = (await db.query(`insert into amenities(name,price,duration_days) values('Cancel Test Sauna',200,30) returning amenity_id`)).rows[0].amenity_id;
    const pay = (await as(uid, 'select * from avail_amenity($1)', [amenity]))[0];
    await assert.rejects(cancelPending(uid, pay), /not_found/); assert.equal((await payRow(pay)).status, 'pending');
  });
  await test('simultaneous verify and cancel: exactly one wins and the result is consistent', async () => {
    const uid = await user(), pay = await extend(uid);
    await second(async c => {
      const results = await Promise.allSettled([verify(pay), cancelPending(uid, pay, c)]);
      assert.equal(results.filter(x => x.status === 'fulfilled').length, 1);
    });
    const status = (await payRow(pay)).status, hasTerm = (await terms(uid)).length === 1;
    assert.ok(['verified', 'rejected'].includes(status)); assert.equal(hasTerm, status === 'verified');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nPhase 7: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
