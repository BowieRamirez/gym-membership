// 0141 (owner, 2026-11-23): the front desk takes a booking's balance online too. The desk starts an online payment for the balance;
// Xendit confirming it marks the balance paid as the cash button does. While it waits, cash/GCash for the same balance is refused.
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
async function service(sql, args = []) {
  await db.query('begin');
  try { await db.query('set local role service_role'); const r = await db.query(sql, args); await db.query('commit'); return r.rows; }
  catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role = 'member') => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`bb-${++serial}@test.io`, JSON.stringify({ full_name: 'Balance Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const desk = await user('employee');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='24:00',payment_expiry_hours=24,down_payment_percent=50 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('BB Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const amenity = (await db.query(`insert into amenities(name,price) values('BB Ring',100) returning amenity_id id`)).rows[0].id;
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const at = hm => new Date(`${day}T${hm}:00+08:00`).toISOString();
const res = async id => (await db.query('select * from reservations where reservation_id=$1', [id])).rows[0];
let xids = 0;
async function member() {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
// a 3-hour booking (₱300): ₱150 down payment confirmed, ₱150 balance left for the desk
async function confirmedBooking(hm) {
  const uid = await member();
  const r = (await as(uid, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>180, p_method=>'qr'::payment_method)`, [amenity, at(hm)]))[0];
  await as(desk, 'select verify_payment($1,true)', [r.payment_id]);
  return { uid, r: await res(r.reservation_id) };
}
const payOnline = async pay => {
  const xid = `ps-bb-${++xids}`;
  await service('select save_online_checkout($1,$2,$3)', [pay.payment_id, xid, `https://checkout.test/${xid}`]);
  return (await service('select confirm_online_payment($1,$2,$3) r', [xid, pay.amount, 'GCASH']))[0].r;
};

try {
  await test('the desk starts an online payment for the ₱150 balance; nothing is paid until Xendit confirms', async () => {
    const { r } = await confirmedBooking('08:00');
    assert.equal(r.status, 'confirmed');
    const pay = (await as(desk, 'select * from start_booking_balance_online($1)', [r.reservation_id]))[0];
    assert.equal(Number(pay.amount), 150); assert.equal(pay.status, 'pending'); assert.equal(pay.method, 'qr');
    assert.equal(pay.recorded_by, desk);
    assert.equal((await res(r.reservation_id)).balance_payment_id, null);
    const again = (await as(desk, 'select * from start_booking_balance_online($1)', [r.reservation_id]))[0];
    assert.equal(again.payment_id, pay.payment_id, 'a second press opens the same payment');
    await assert.rejects(as(desk, `select record_booking_balance($1,'cash')`, [r.reservation_id]), /balance_online_pending/);
    const ua = async () => (await db.query('select status from user_amenities where user_amenity_id=$1', [r.user_amenity_id])).rows[0].status;
    const before = await ua();
    await payOnline(pay);
    assert.equal(await ua(), before, "the member's used session is not switched back on");
    const after = await res(r.reservation_id);
    assert.equal(after.balance_payment_id, pay.payment_id);
    assert.equal((await db.query('select status from payments where payment_id=$1', [pay.payment_id])).rows[0].status, 'verified');
    await assert.rejects(as(desk, 'select * from start_booking_balance_online($1)', [r.reservation_id]), /already_processed/);
    await assert.rejects(as(desk, `select record_booking_balance($1,'cash')`, [r.reservation_id]), /already_processed/);
  });
  await test('cash still works when no online payment is waiting; a rejected online one frees it', async () => {
    const { r } = await confirmedBooking('12:00');
    const pay = (await as(desk, 'select * from start_booking_balance_online($1)', [r.reservation_id]))[0];
    await as(desk, `select verify_payment($1,false,'Customer left')`, [pay.payment_id]);
    const cash = (await as(desk, `select * from record_booking_balance($1,'cash')`, [r.reservation_id]))[0];
    assert.equal(Number(cash.amount), 150);
    assert.equal((await res(r.reservation_id)).balance_payment_id, cash.payment_id);
  });
  await test('only the desk starts it; members and anonymous are refused; an unpaid booking has no balance to take', async () => {
    const { uid, r } = await confirmedBooking('16:00');
    await assert.rejects(as(uid, 'select * from start_booking_balance_online($1)', [r.reservation_id]), /forbidden/);
    const other = await member();
    const pending = (await as(other, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method)`, [amenity, at('20:00')]))[0];
    await assert.rejects(as(desk, 'select * from start_booking_balance_online($1)', [pending.reservation_id]), /not_confirmed/);
    await db.query('begin'); await db.query('set local role anon');
    try { await assert.rejects(db.query('select start_booking_balance_online(1)'), /permission denied/); } finally { await db.query('rollback'); }
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Booking balance online: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
