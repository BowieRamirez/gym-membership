// 0075, Xendit plan (2026-11-16, X1-X15): members pay online through Xendit's hosted checkout instead of sending a GCash proof.
// xendit-checkout saves the checkout on the payment (save_online_checkout); xendit-webhook confirms it (confirm_online_payment),
// which verifies and activates like the desk's approval. Money for a payment that is no longer payable is recorded and sent to staff
// to refund (X9). Both functions are service_role only.
import { start, openCashShifts } from './harness.mjs';
import assert from 'node:assert/strict';

// 0079: an online payment logged before 0078 (as System, no role) gets its member named
let oldPay = null;
const { server, client: db } = await start({ beforeMigration: async (c, f) => {
  if (!f.startsWith('0079')) return;
  const u = (await c.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values('x-old@test.io','{"full_name":"Old Payer"}','{"role":"member"}') returning id`)).rows[0].id;
  const pk = (await c.query(`insert into membership_packages(name,price,duration_days) values('X Old',500,30) returning membership_package_id id`)).rows[0].id;
  await c.query(`select set_config('request.jwt.claim.sub', $1, false)`, [u]);
  oldPay = { member: u, id: (await c.query('select payment_id from extend_membership($1,null)', [pk])).rows[0].payment_id };
  await c.query(`select set_config('request.jwt.claim.sub', '', false)`);
  await c.query(`insert into audit_log(action, entity, entity_id, details) values('payment.online','payments',$1,'{"channel":null}')`, [String(oldPay.id)]);
} });
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

  // cashier plan Y5 (0078): the log row of an online payment names the member who paid, their role, and how they paid
  await test('an online payment is logged as the member who paid, with the method', async () => {
    const uid = await user(), p = await buy(uid), id = xid();
    await checkout(p, id); await confirm(id, p.amount, 'GCASH');
    const row = (await db.query(`select actor_id, role, ip_address, details from audit_log where action='payment.online' and entity_id=$1`, [String(p.payment_id)])).rows[0];
    assert.deepEqual([row.actor_id, row.role, row.ip_address, row.details.channel], [uid, 'member', null, 'GCASH']);
  });
  await test('0079 named the member on an online payment logged before 0078', async () => {
    const row = (await db.query(`select actor_id, role from audit_log where action='payment.online' and entity_id=$1`, [String(oldPay.id)])).rows[0];
    assert.deepEqual([row.actor_id, row.role], [oldPay.member, 'member']);
  });
  await test('a refund row is logged as the member too', async () => {
    const uid = await user(), p = await buy(uid), id = xid();
    await checkout(p, id); await confirm(id, Number(p.amount) + 1, 'QRPH');
    const row = (await db.query(`select actor_id, role, details from audit_log where action='payment.online_refund' and entity_id=$1`, [String(p.payment_id)])).rows[0];
    assert.deepEqual([row.actor_id, row.role, row.details.channel], [uid, 'member', 'QRPH']);
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

  // final review, Important 1 (0077): a missing amount must never pass the amount check
  await test('a missing amount is not activated', async () => {
    const p = await buy(await user()), id = xid();
    await checkout(p, id);
    assert.equal(await confirm(id, null), 'refund');
    assert.equal((await payment(p.payment_id)).status, 'pending');
  });

  // final review, Important 2 (0077): the app re-checks every 20 s and Xendit retries; staff hear about a refund once
  await test('a refund is reported to staff once, however often it is confirmed again', async () => {
    const p = await buy(await user()), id = xid();
    await checkout(p, id);
    const before = await staffNotes('Online payment to refund');
    for (let i = 0; i < 3; i++) assert.equal(await confirm(id, Number(p.amount) + 5), 'refund');
    const staff = Number((await db.query(`select count(distinct user_id) n from user_notifications where title='Online payment to refund'`)).rows[0].n);
    assert.equal(await staffNotes('Online payment to refund') - before, staff);   // one notice per staff member, not three
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
    // 0082: notify() was open to anyone since 0003 (anyone could message any user); now only server functions use it
    await assert.rejects(as(uid, `select notify($1,'payment','spam')`, [admin]), /permission denied/);
    await assert.rejects(asRole('anon', `select notify($1,'payment','spam')`, [admin]), /permission denied/);
  });

  // cashier plan Y6 (0080): e-wallet at the desk goes through Xendit. The desk records a pending online payment (30 minutes);
  // Xendit's confirmation activates it like any online payment; the cash drawer is never involved.
  const deskOnline = (kind, member, item, name = null) => as(desk, `select * from start_desk_online_payment($1::payment_kind,$2,$3,$4,null)`,
    [kind, member, item, name]).then(r => r[0]);
  await test('the desk starts a pending online payment; nothing is active until Xendit confirms', async () => {
    const uid = await user();
    const p = await deskOnline('membership', uid, pack);
    assert.deepEqual([p.status, p.method, p.recorded_by, p.verified_by, p.receipt_no, p.cash_shift_id], ['pending', 'qr', desk, null, null, null]);
    const minutes = (new Date(p.expires_at) - Date.now()) / 60000;
    assert.ok(minutes > 28 && minutes <= 30, `expires in ${minutes} minutes`);
    assert.equal((await db.query(`select count(*) n from user_membership_packages where member_id=$1 and status='active'`, [uid])).rows[0].n, '0');
    const id = xid(); await checkout(p, id);
    assert.equal(await confirm(id, p.amount, 'QRPH'), 'confirmed');
    const after = await payment(p.payment_id);
    assert.equal(after.status, 'verified'); assert.match(after.receipt_no, /^OR-/); assert.equal(after.cash_shift_id, null);
    assert.equal((await db.query('select count(*) n from membership_terms where member_id=$1', [uid])).rows[0].n, '1');
  });
  await test('a day pass paid online at the desk checks the person in once Xendit confirms', async () => {
    const p = await deskOnline('walk_in', null, null, 'Walk-in Wendy');
    assert.equal((await db.query('select count(*) n from gym_check_ins where payment_id=$1', [p.payment_id])).rows[0].n, '0');
    const id = xid(); await checkout(p, id); await confirm(id, p.amount, 'GCASH');
    assert.equal((await db.query('select count(*) n from gym_check_ins where payment_id=$1', [p.payment_id])).rows[0].n, '1');
  });
  await test('an unpaid desk online payment expires and releases what it held', async () => {
    const uid = await user();
    const p = await deskOnline('membership', uid, pack);
    await db.query(`update payments set expires_at=now()-interval '1 minute' where payment_id=$1`, [p.payment_id]);
    await db.query('select expire_payments()');
    assert.equal((await payment(p.payment_id)).status, 'expired');
    assert.equal((await db.query(`select count(*) n from user_membership_packages where member_id=$1 and status in ('pending','active')`, [uid])).rows[0].n, '0');
  });
  // final review, Critical (0082): an unpaid anonymous Day pass has no member to notify; expiry must not fail on it (it rolled back
  // every expiry run, and book_amenity runs expiry first, so all bookings failed)
  await test('an unpaid desk Day pass expires without stopping the other expiries', async () => {
    const dayPass = await deskOnline('walk_in', null, null, null);
    const other = await buy(await user());
    await db.query(`update payments set expires_at=now()-interval '1 minute' where payment_id = any($1)`, [[dayPass.payment_id, other.payment_id]]);
    await db.query('select expire_payments()');
    assert.deepEqual([(await payment(dayPass.payment_id)).status, (await payment(other.payment_id)).status], ['expired', 'expired']);
  });
  await test('Customer left: the desk rejects the pending payment, which frees the member at once', async () => {
    const uid = await user();
    const p = await deskOnline('membership', uid, pack);
    await as(desk, `select verify_payment($1,false,'Customer left at the desk')`, [p.payment_id]);
    assert.equal((await payment(p.payment_id)).status, 'rejected');
    const cash = (await as(desk, `select * from record_counter_payment('membership'::payment_kind,'cash'::payment_method,$1,$2,null,null,null)`, [uid, pack]))[0];
    assert.equal(cash.status, 'verified');
  });
  await test('only the desk may start one', async () => {
    const uid = await user();
    await assert.rejects(as(uid, `select * from start_desk_online_payment('membership'::payment_kind,$1,$2,null,null)`, [uid, pack]), /forbidden|permission/);
    await assert.rejects(asRole('anon', `select * from start_desk_online_payment('membership'::payment_kind,null,1,null,null)`), /permission denied/);
  });

  // Phase 5 break attempts: the only ways in are the two service functions; a direct write can't plant a checkout or mark it paid
  await test('a member, the desk or an admin can\'t write the Xendit columns or mark a payment verified directly', async () => {
    const uid = await user(), p = await buy(uid);
    for (const who of [uid, desk, admin]) {
      for (const sql of [`update payments set xendit_id='ps-evil' where payment_id=$1`, `update payments set xendit_url='https://evil' where payment_id=$1`,
                         `update payments set online_paid_amount=1 where payment_id=$1`, `update payments set status='verified' where payment_id=$1`]) {
        try { await as(who, sql, [p.payment_id]); } catch { /* refused outright is fine too */ }
      }
    }
    const after = await payment(p.payment_id);
    assert.deepEqual([after.xendit_id, after.xendit_url, after.online_paid_amount, after.status], [null, null, null, 'pending']);
  });

  await test('hostile input: a huge or odd Xendit id and a negative amount change nothing they shouldn\'t', async () => {
    const p = await buy(await user()), id = xid();
    await checkout(p, id);
    await assert.rejects(confirm('x'.repeat(5000), 1), /not_found/);
    assert.equal(await confirm(id, -1), 'refund');
    assert.equal((await payment(p.payment_id)).status, 'pending');
  });
} finally {
  console.log(`\nxendit: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
