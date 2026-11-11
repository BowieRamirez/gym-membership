// Phase 5 of the 2026-11-09 plan (0052, owner decisions F12, F13, F14): book an amenity, then pay; the desk approves; holds run out;
// the payment's expiry is the hold; cancel up to 1 hour before for a refund; "I didn't use it" within 24 hours; coaches say which amenities they run.
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
  [`b-${++serial}@test.io`, JSON.stringify({ full_name: 'Booking Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach'), coach2 = await user('coach');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30',payment_expiry_hours=24 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('B Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const amenity = async (name, days = null) => (await db.query('insert into amenities(name,price,duration_days) values($1,150,$2) returning amenity_id id', [name, days])).rows[0].id;
async function member() {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
const book = (uid, a, hm, minutes = 60, method = 'qr') =>
  as(uid, 'select * from book_amenity($1,$2,$3,$4::payment_method)', [a, at(hm), minutes, method]).then(r => r[0]);
const res = async id => (await db.query('select * from reservations where reservation_id=$1', [id])).rows[0];
const payOf = async r => (await db.query('select * from payments where payment_id=$1', [r.payment_id])).rows[0];
const approve = r => as(desk, 'select verify_payment($1,true)', [r.payment_id]);
// move a booking in time (as the owner of the database), so "1 hour before" and "24 hours after" can be tested now
const shift = (id, startSql, minutes = 60) => db.query(`update reservations set starts_at=${startSql}, ends_at=${startSql} + make_interval(mins => $2) where reservation_id=$1`, [id, minutes]);
const refundOf = async id => (await db.query('select * from refund_requests where reservation_id=$1', [id])).rows[0];

try {
  // ---------- F12: book, then pay ----------
  await test('booking with no pass holds the slot and creates the payment for one session', async () => {
    const uid = await member(), a = await amenity('B Hold');
    const r = await book(uid, a, '10:00', 90);
    assert.equal(r.status, 'awaiting_payment');
    const p = await payOf(r);
    assert.deepEqual([p.status, p.kind, p.method, Number(p.amount), p.member_id], ['pending', 'amenity', 'qr', 150, uid]);
    assert.equal(new Date(r.ends_at) - new Date(r.starts_at), 90 * 60000);
  });
  await test('the hold ends after the payment expiry hours or 1 hour before the start, whichever comes first', async () => {
    const uid = await member(), a = await amenity('B Hold Until');
    const far = await book(uid, a, '10:00');   // two days ahead: the 24 expiry hours come first
    const p = await payOf(far);
    assert.ok(Math.abs(new Date(p.expires_at) - (Date.now() + 24 * 3600000)) < 60000);
    const soon = (await db.query(`select to_char(date_trunc('hour', now() at time zone 'Asia/Manila') + interval '3 hours','YYYY-MM-DD"T"HH24:MI') t`)).rows[0].t;
    const [d, hm] = soon.split('T');
    if (hm >= '22:30') return;   // a 1-hour booking would run past closing
    const near = (await as(uid, 'select * from book_amenity($1,$2,60,$3::payment_method)', [a, at(hm, d), 'cash']))[0];
    assert.equal(new Date((await payOf(near)).expires_at).getTime(), new Date(near.starts_at).getTime() - 3600000);
    assert.equal((await payOf(near)).method, 'cash');
  });
  await test('a slot that starts within the hour cannot be held', async () => {
    const uid = await member(), a = await amenity('B Too Soon');
    const next = new Date(Math.ceil((Date.now() + 1000) / 1800000) * 1800000).toISOString();
    await assert.rejects(as(uid, 'select * from book_amenity($1,$2,30,$3::payment_method)', [a, next, 'qr']), /too_soon/);
  });
  await test('two members cannot hold the same slot, and a hold keeps its cooldown', async () => {
    const one = await member(), two = await member(), a = await amenity('B Clash');
    await book(one, a, '10:00');
    await assert.rejects(book(two, a, '10:30'), /slot_taken/);
    await assert.rejects(book(two, a, '11:00'), /slot_taken/);   // 11:00-11:30 is the hold's cooldown
    assert.equal((await book(two, a, '11:30')).status, 'awaiting_payment');
  });
  await test('length is 30 minutes to 2 hours in 30-minute steps', async () => {
    const uid = await member(), a = await amenity('B Length');
    for (const m of [0, 20, 150]) await assert.rejects(book(uid, a, '10:00', m), /bad_input|too_long/);
    await assert.rejects(book(uid, a, '10:00', 60, 'gcash'), /bad_input/);
  });
  await test('a guest without a membership cannot book', async () => {
    const guest = await user(), a = await amenity('B Guest');
    await assert.rejects(book(guest, a, '10:00'), /no_active_membership/);
  });
  await test('the desk approving the payment confirms the booking, uses the session and tells the member', async () => {
    const uid = await member(), a = await amenity('B Approve');
    const r = await book(uid, a, '10:00');
    await approve(r);
    const after = await res(r.reservation_id);
    assert.equal(after.status, 'confirmed');
    assert.equal((await db.query('select status from user_amenities where user_amenity_id=$1', [r.user_amenity_id])).rows[0].status, 'expired');
    const n = (await db.query(`select title from user_notifications where user_id=$1 order by 1`, [uid])).rows.map(x => x.title);
    assert.ok(n.includes('Booking confirmed'), n.join(', '));
  });
  await test('a pay-at-the-desk booking is approved in cash into the cashier\'s shift', async () => {
    const uid = await member(), a = await amenity('B Cash');
    const r = await book(uid, a, '10:00', 30, 'cash');
    await approve(r);
    assert.ok((await payOf(r)).cash_shift_id);
    assert.equal((await res(r.reservation_id)).status, 'confirmed');
  });
  await test('rejecting the payment frees the slot for someone else', async () => {
    const one = await member(), two = await member(), a = await amenity('B Reject');
    const r = await book(one, a, '10:00');
    await as(desk, `select verify_payment($1,false,'No transfer found')`, [r.payment_id]);
    assert.equal((await res(r.reservation_id)).status, 'cancelled');
    assert.equal((await book(two, a, '10:00')).status, 'awaiting_payment');
  });
  await test('an unpaid hold that runs out frees the slot, and its payment can no longer be approved', async () => {
    const one = await member(), two = await member(), a = await amenity('B Expire');
    const r = await book(one, a, '10:00', 60, 'cash');
    await db.query(`update payments set expires_at=now()-interval '1 minute' where payment_id=$1`, [r.payment_id]);
    await assert.rejects(approve(r), /payment_expired/);
    await db.query('select expire_payments()');
    assert.equal((await res(r.reservation_id)).status, 'cancelled');
    assert.equal((await payOf(r)).status, 'expired');
    assert.equal((await book(two, a, '10:00')).status, 'awaiting_payment');
  });
  await test('a hold that has run out does not block a new booking even before the job runs', async () => {
    const one = await member(), two = await member(), a = await amenity('B Stale');
    const r = await book(one, a, '10:00');
    await db.query(`update payments set expires_at=now()-interval '1 minute' where payment_id=$1`, [r.payment_id]);
    assert.equal((await book(two, a, '10:00')).status, 'awaiting_payment');
  });
  await test('a member with a pass still books without paying until it expires', async () => {
    const uid = await member(), a = await amenity('B Pass', 30);
    const p = (await as(uid, 'select * from avail_amenity($1)', [a]))[0];
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    const r = await book(uid, a, '10:00');
    assert.deepEqual([r.status, r.payment_id], ['confirmed', null]);
  });
  await test('the grid shows holds as taken', async () => {
    const uid = await member(), other = await member(), a = await amenity('B Grid');
    await book(uid, a, '10:00');
    const g = (await as(other, 'select amenity_grid($1::date) g', [day]))[0].g;
    assert.ok(g.blocks.some(b => b.amenity == a && !b.mine));
  });
  await test('the member cancels an unpaid hold: no refund, the payment is withdrawn, the slot is free', async () => {
    const uid = await member(), a = await amenity('B Withdraw');
    const r = await book(uid, a, '10:00');
    await as(uid, `select cancel_reservation($1,'')`, [r.reservation_id]);
    assert.equal((await res(r.reservation_id)).status, 'cancelled');
    assert.equal((await payOf(r)).status, 'rejected');
    assert.equal(await refundOf(r.reservation_id), undefined);
  });

  // ---------- F13: refunds and no-shows ----------
  await test('cancelling a paid booking 1 hour or more before gives a full refund, approved at once', async () => {
    const uid = await member(), a = await amenity('B Refund');
    const r = await book(uid, a, '10:00');
    await approve(r);
    await as(uid, `select cancel_reservation($1,'')`, [r.reservation_id]);
    const f = await refundOf(r.reservation_id);
    assert.deepEqual([f.status, Number(f.amount), f.kind, f.payment_id, f.term_id], ['approved', 150, 'cancel', r.payment_id, null]);
    assert.equal((await res(r.reservation_id)).status, 'cancelled');
    await as(admin, `select record_membership_refund_paid($1,'cash')`, [f.refund_request_id]);
    assert.equal((await refundOf(r.reservation_id)).status, 'paid');
  });
  await test('cancelling a paid booking 59 minutes before is refused', async () => {
    const uid = await member(), a = await amenity('B Late');
    const r = await book(uid, a, '10:00');
    await approve(r);
    await shift(r.reservation_id, `now() + interval '59 minutes'`);
    await assert.rejects(as(uid, `select cancel_reservation($1,'')`, [r.reservation_id]), /too_late_to_cancel/);
  });
  await test('"I didn\'t use it" within 24 hours after the end makes a claim the front desk decides', async () => {
    const uid = await member(), a = await amenity('B Claim');
    const r = await book(uid, a, '10:00');
    await approve(r);
    await assert.rejects(as(uid, `select claim_amenity_no_show($1,'Flooded')`, [r.reservation_id]), /not_ended/);
    await shift(r.reservation_id, `now() - interval '3 hours'`);
    await assert.rejects(as(uid, `select claim_amenity_no_show($1,'')`, [r.reservation_id]), /bad_input/);
    await as(uid, `select claim_amenity_no_show($1,'The pool was closed for cleaning')`, [r.reservation_id]);
    await assert.rejects(as(uid, `select claim_amenity_no_show($1,'again')`, [r.reservation_id]), /already_requested/);
    const f = await refundOf(r.reservation_id);
    assert.deepEqual([f.status, f.kind, f.reason], ['pending', 'no_show', 'The pool was closed for cleaning']);
    await assert.rejects(as(uid, `select decide_amenity_refund($1,true,'ok')`, [f.refund_request_id]), /forbidden/);
    await assert.rejects(as(desk, `select decide_amenity_refund($1,false,'')`, [f.refund_request_id]), /bad_input/);
    await as(desk, `select decide_amenity_refund($1,true,'No check-in that day; pool log shows cleaning')`, [f.refund_request_id]);
    assert.equal((await refundOf(r.reservation_id)).status, 'approved');
    const n = (await db.query(`select title from user_notifications where user_id=$1`, [uid])).rows.map(x => x.title);
    assert.ok(n.includes('Refund approved'), n.join(', '));
  });
  await test('the desk can decline a claim with a reason', async () => {
    const uid = await member(), a = await amenity('B Decline');
    const r = await book(uid, a, '10:00');
    await approve(r);
    await shift(r.reservation_id, `now() - interval '2 hours'`);
    await as(uid, `select claim_amenity_no_show($1,'Did not go')`, [r.reservation_id]);
    const f = await refundOf(r.reservation_id);
    await as(desk, `select decide_amenity_refund($1,false,'Checked in at 10:02 and used the pool')`, [f.refund_request_id]);
    await assert.rejects(as(desk, `select decide_amenity_refund($1,true,'changed my mind')`, [f.refund_request_id]), /already_processed/);
    assert.equal((await refundOf(r.reservation_id)).status, 'rejected');
  });
  await test('a claim more than 24 hours after the end is refused', async () => {
    const uid = await member(), a = await amenity('B Old');
    const r = await book(uid, a, '10:00');
    await approve(r);
    await shift(r.reservation_id, `now() - interval '26 hours'`);
    await assert.rejects(as(uid, `select claim_amenity_no_show($1,'Forgot')`, [r.reservation_id]), /claim_window_closed/);
  });
  await test('only a paid booking of your own can be claimed, and membership refund decisions do not take amenity claims', async () => {
    const uid = await member(), other = await member(), a = await amenity('B Own', 30);
    const p = (await as(uid, 'select * from avail_amenity($1)', [a]))[0];
    await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
    const r = await book(uid, a, '10:00');   // a pass booking: nothing paid for it
    await shift(r.reservation_id, `now() - interval '2 hours'`);
    await assert.rejects(as(uid, `select claim_amenity_no_show($1,'x')`, [r.reservation_id]), /not_refundable/);
    const b = await amenity('B Own 2'), paid = await book(uid, b, '12:00');
    await approve(paid);
    await shift(paid.reservation_id, `now() - interval '5 hours'`);
    await assert.rejects(as(other, `select claim_amenity_no_show($1,'x')`, [paid.reservation_id]), /not_found/);
    await as(uid, `select claim_amenity_no_show($1,'x')`, [paid.reservation_id]);
    const f = await refundOf(paid.reservation_id);
    await assert.rejects(as(admin, `select decide_membership_refund($1,true,'x')`, [f.refund_request_id]), /not_found|not_refundable/);
  });
  await test('members cannot write bookings or refund rows directly', async () => {
    const uid = await member(), a = await amenity('B Direct');
    await assert.rejects(as(uid, `insert into reservations(member_id,amenity_id,starts_at,ends_at,status) values($1,$2,now()+interval '1 day',now()+interval '25 hours','confirmed')`, [uid, a]), /permission denied/);
    await assert.rejects(as(uid, `insert into refund_requests(reservation_id,payment_id,member_id,amount,reason) values(1,1,$1,1,'x')`, [uid]), /permission denied/);
  });

  // ---------- F14: coaches run amenities ----------
  await test('a coach switches "I run this" on and off for themselves; members and the front desk cannot', async () => {
    const a = await amenity('C Runs'), uid = await member();
    await as(coach, 'select set_runs_amenity($1,true)', [a]);
    await as(coach, 'select set_runs_amenity($1,true)', [a]);   // twice is fine
    assert.deepEqual((await as(uid, 'select coach_id from coach_amenities where amenity_id=$1', [a])).map(x => x.coach_id), [coach]);
    for (const who of [uid, desk]) await assert.rejects(as(who, 'select set_runs_amenity($1,true)', [a]), /forbidden/);
    await assert.rejects(as(coach, 'insert into coach_amenities(coach_id,amenity_id) values($1,$2)', [coach2, a]), /permission denied/);
    await as(coach, 'select set_runs_amenity($1,false)', [a]);
    assert.equal((await as(uid, 'select count(*)::int n from coach_amenities where amenity_id=$1', [a]))[0].n, 0);
  });
  await test('with assigned coaches, only one of them in the gym counts; without, any coach does', async () => {
    await db.query('update coach_visits set checked_out_at=now(),checked_out_by=coach_id where checked_out_at is null');
    const uid = await member(), run = await amenity('C Assigned', 30), free = await amenity('C Free', 30);
    for (const a of [run, free]) { const p = (await as(uid, 'select * from avail_amenity($1)', [a]))[0]; await as(desk, 'select verify_payment($1,true)', [p.payment_id]); }
    await as(coach, 'select set_runs_amenity($1,true)', [run]);
    const use = async a => {
      const ua = (await db.query('select user_amenity_id from user_amenities where member_id=$1 and amenity_id=$2', [uid, a])).rows[0].user_amenity_id;
      return as(uid, 'insert into amenity_usages(user_amenity_id) values($1) returning status', [ua]);
    };
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach2]);
    await assert.rejects(use(run), /no_coach_present/);
    assert.equal((await use(free))[0].status, 'pending');
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]);
    assert.equal((await use(run))[0].status, 'pending');
    await db.query('update coach_visits set checked_out_at=now(),checked_out_by=coach_id where checked_out_at is null');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Amenity booking: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
