// 0075, Xendit plan (2026-11-16, X1-X15): members pay online through Xendit's hosted checkout instead of sending a GCash proof.
// xendit-checkout saves the checkout on the payment (save_online_checkout); xendit-webhook confirms it (confirm_online_payment),
// which verifies and activates like the desk's approval. Money for a payment that is no longer payable is recorded and sent to staff
// to refund (X9). Both functions are service_role only.
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
async function asRole(role, sql, args = []) {
  await db.query('begin');
  try { await db.query(`set local role ${role}`); const r = await db.query(sql, args); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
const service = (sql, args) => asRole('service_role', sql, args);
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`x-${++serial}@test.io`, JSON.stringify({ full_name: 'Online Payer' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30' where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('X Monthly',1500,30) returning membership_package_id id`)).rows[0].id;
const amenity = (await db.query(`insert into amenities(name,price) values('X Court',200) returning amenity_id id`)).rows[0].id;

const buy = async uid => (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
const payment = async id => (await db.query('select * from payments where payment_id=$1', [id])).rows[0];
const checkout = (p, xid) => service('select save_online_checkout($1,$2,$3)', [p.payment_id, xid, `https://checkout.test/${xid}`]);
const confirm = async (xid, amount, channel = 'GCASH') =>
  (await service('select confirm_online_payment($1,$2,$3) r', [xid, amount, channel]))[0].r;
const staffNotes = async title => Number((await db.query('select count(*) n from user_notifications where title=$1', [title])).rows[0].n);
let xids = 0;
const xid = () => `ps-test-${++xids}`;
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const at = hm => new Date(`${day}T${hm}:00+08:00`).toISOString();
async function member() { const uid = await user(); const p = await buy(uid); await as(desk, 'select verify_payment($1,true)', [p.payment_id]); return uid; }
const book = (uid, hm) => as(uid, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method)`,
  [amenity, at(hm)]).then(r => r[0]);

try {
  await test('membership: confirm verifies, gives a receipt and starts the term', async () => {
    const uid = await user(), p = await buy(uid), id = xid();
    await checkout(p, id);
    assert.equal((await payment(p.payment_id)).xendit_url, `https://checkout.test/${id}`);
    assert.equal(await confirm(id, p.amount), 'confirmed');
    const after = await payment(p.payment_id);
    assert.equal(after.status, 'verified');
    assert.match(after.receipt_no, /^OR-/);
    assert.deepEqual([after.reference_no, after.online_channel, after.verified_by], [id, 'GCASH', null]);
    assert.equal(Number(after.online_paid_amount), Number(p.amount));
    assert.equal((await db.query('select count(*) n from membership_terms where member_id=$1', [uid])).rows[0].n, '1');
    assert.equal((await db.query(`select count(*) n from user_notifications where user_id=$1 and title='Payment received'`, [uid])).rows[0].n, '1');
  });

  await test('the first saved checkout stays: a second save does not replace it', async () => {
    const p = await buy(await user()), one = xid(), two = xid();
    await checkout(p, one); await checkout(p, two);
    assert.equal((await payment(p.payment_id)).xendit_id, one);
  });

  // 0076: Xendit sessions are short-lived; an expired one is replaced only by naming it (a double tap can't swap a paid one out)
  await test('an expired checkout is replaced only by naming the one it replaces', async () => {
    const p = await buy(await user()), one = xid(), two = xid(), three = xid();
    await checkout(p, one);
    await service('select save_online_checkout($1,$2,$3,$4)', [p.payment_id, two, 'https://checkout.test/2', 'ps-wrong']);
    assert.equal((await payment(p.payment_id)).xendit_id, one);
    await service('select save_online_checkout($1,$2,$3,$4)', [p.payment_id, two, 'https://checkout.test/2', one]);
    await service('select save_online_checkout($1,$2,$3,$4)', [p.payment_id, three, 'https://checkout.test/3', one]);
    const after = await payment(p.payment_id);
    assert.deepEqual([after.xendit_id, after.xendit_url], [two, 'https://checkout.test/2']);
  });

  await test('amenity down payment paid online confirms the booking', async () => {
    const uid = await member(), r = await book(uid, '09:00'), id = xid();
    await checkout(await payment(r.payment_id), id);
    assert.equal(await confirm(id, r.down_payment), 'confirmed');
    assert.equal((await db.query('select status from reservations where reservation_id=$1', [r.reservation_id])).rows[0].status, 'confirmed');
  });

  await test('a repeated webhook does nothing', async () => {
    const p = await buy(await user()), id = xid();
    await checkout(p, id); await confirm(id, p.amount);
    const receipt = (await payment(p.payment_id)).receipt_no;
    assert.equal(await confirm(id, p.amount), 'already');
    assert.equal((await payment(p.payment_id)).receipt_no, receipt);
  });

  await test('wrong amount is not activated; staff are told to refund it', async () => {
    const before = await staffNotes('Online payment to refund');
    const p = await buy(await user()), id = xid();
    await checkout(p, id);
    assert.equal(await confirm(id, Number(p.amount) + 1), 'refund');
    const after = await payment(p.payment_id);
    assert.equal(after.status, 'pending');
    assert.equal(Number(after.online_paid_amount), Number(p.amount) + 1);
    assert.ok(await staffNotes('Online payment to refund') > before);
  });

  await test('paid after expiry is not activated', async () => {
    const uid = await user(), p = await buy(uid), id = xid();
    await checkout(p, id);
    await db.query(`update payments set expires_at=now()-interval '1 minute' where payment_id=$1`, [p.payment_id]);
    assert.equal(await confirm(id, p.amount), 'refund');
    assert.notEqual((await payment(p.payment_id)).status, 'verified');
    assert.equal((await db.query('select count(*) n from membership_terms where member_id=$1', [uid])).rows[0].n, '0');
  });

  await test('paid after the desk rejected it is not activated', async () => {
    const p = await buy(await user()), id = xid();
    await checkout(p, id);
    await as(desk, `select verify_payment($1,false,'Wrong item')`, [p.payment_id]);
    assert.equal(await confirm(id, p.amount), 'refund');
    assert.equal((await payment(p.payment_id)).status, 'rejected');
  });

  await test('an unknown Xendit id raises not_found', async () => {
    await assert.rejects(confirm('ps-nobody', 100), /not_found/);
  });

  await test('slot taken still refunds as before', async () => {
    const one = await member(), two = await member();
    const r1 = await book(one, '15:00'), r2 = await book(two, '15:00'), id = xid();
    await as(desk, 'select verify_payment($1,true)', [r1.payment_id]);
    await checkout(await payment(r2.payment_id), id);
    await confirm(id, r2.down_payment);
    const lost = (await db.query('select * from reservations where reservation_id=$1', [r2.reservation_id])).rows[0];
    assert.deepEqual([lost.status, lost.cancel_reason], ['cancelled', 'Slot taken']);
    assert.equal((await db.query('select status from refund_requests where reservation_id=$1', [r2.reservation_id])).rows[0].status, 'approved');
  });

  await test('the desk can\'t approve a payment once its Xendit checkout is open; rejecting still works', async () => {
    const p = await buy(await user());
    await checkout(p, xid());
    await assert.rejects(as(desk, 'select verify_payment($1,true)', [p.payment_id]), /pay_online/);
    await as(desk, `select verify_payment($1,false,'Duplicate')`, [p.payment_id]);
    assert.equal((await payment(p.payment_id)).status, 'rejected');
  });

  await test('an old proof sent before the switch can still be approved', async () => {
    const p = await buy(await user());
    await db.query(`update payments set reference_no='GC-OLD-1', proof_path='x/old.png' where payment_id=$1`, [p.payment_id]);
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    assert.equal((await payment(p.payment_id)).status, 'verified');
  });

  await test('members can\'t send proofs any more', async () => {
    const uid = await user(), p = await buy(uid);
    await assert.rejects(as(uid, `select submit_payment_proof($1,'GC-NEW','x/new.png')`, [p.payment_id]), /permission denied/);
  });

  await test('nobody but service_role can call the new functions', async () => {
    const uid = await user(), p = await buy(uid);
    for (const who of [uid, desk, admin]) {
      await assert.rejects(as(who, `select save_online_checkout($1,'ps-evil','https://evil')`, [p.payment_id]), /permission denied/);
      await assert.rejects(as(who, `select confirm_online_payment('ps-evil',1,null)`), /permission denied/);
    }
    await assert.rejects(asRole('anon', `select confirm_online_payment('ps-evil',1,null)`), /permission denied/);
    await assert.rejects(asRole('anon', `select save_online_checkout(1,'ps-evil','https://evil')`), /permission denied/);
  });
} finally {
  console.log(`\nxendit: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
