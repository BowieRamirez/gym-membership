// Phase 6 of the 2026-11-12 plan (0064, owner decisions G13, G14): a booking is a request until its down payment is verified (50% by
// default, Gym settings); before that nobody's slot is held, and of two paid requests for one slot the first verified wins and the
// other's down payment goes to Refunds. The rest is paid at the desk. Cancelling 24 hours or more before refunds the down payment.
// Solo or with a coach: a coach who runs the amenity, has a shift over the whole time and isn't booked; a coach fee set on the amenity.
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
  [`d-${++serial}@test.io`, JSON.stringify({ full_name: "Deposit Person" }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), desk = await user('employee'), coach = await user('coach'), coach2 = await user('coach');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30',payment_expiry_hours=24 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('D Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const amenity = async (name, fee = 0) => (await db.query('insert into amenities(name,price,coach_fee) values($1,200,$2) returning amenity_id id', [name, fee])).rows[0].id;
async function member() {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
const day = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 3)::text d`)).rows[0].d;
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
const book = (uid, a, hm, extra = {}) =>
  as(uid, 'select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>$3, p_method=>$4::payment_method, p_coach=>$5, p_code=>$6)',
    [a, at(hm), extra.minutes ?? 60, extra.method ?? 'qr', extra.coach ?? null, extra.code ?? null]).then(r => r[0]);
const res = async id => (await db.query('select * from reservations where reservation_id=$1', [id])).rows[0];
const payOf = async r => (await db.query('select * from payments where payment_id=$1', [r.payment_id])).rows[0];
const approve = r => as(desk, 'select verify_payment($1,true)', [r.payment_id]);
const refundOf = async id => (await db.query('select * from refund_requests where reservation_id=$1', [id])).rows[0];
async function shiftFor(c, a, from = '08:00', to = '20:00') {
  await db.query('insert into coach_amenities(coach_id, amenity_id) values ($1,$2) on conflict do nothing', [c, a]);
  // one coach can't have overlapping shifts: tests that share a coach on the same day reuse the shift already there
  await db.query(`insert into shift_posts(coach_id, starts_at, ends_at) select $1,$2,$3 where not exists (select 1 from shift_posts
    where coach_id=$1 and status='scheduled' and tstzrange(starts_at,ends_at) && tstzrange($2::timestamptz,$3::timestamptz))`, [c, at(from), at(to)]);
}
const free = (uid, a, hm, minutes = 60) => as(uid, 'select coach_id from free_coaches($1,$2,$3)',
  [a, at(hm), new Date(new Date(at(hm)).getTime() + minutes * 60000).toISOString()]).then(r => r.map(x => x.coach_id));

try {
  await test('G13: the down payment is the Gym settings percent (50 by default) of the price; the rest is the balance', async () => {
    assert.equal((await db.query('select down_payment_percent p from gym_settings')).rows[0].p, 50);
    const uid = await member(), a = await amenity('D Half');
    const r = await book(uid, a, '10:00');
    assert.equal(r.status, 'awaiting_payment');
    assert.deepEqual([Number(r.total_price), Number(r.down_payment)], [200, 100]);
    assert.equal(Number((await payOf(r)).amount), 100);
    await db.query('update gym_settings set down_payment_percent=30');
    const s = await book(uid, a, '14:00');
    assert.deepEqual([Number(s.total_price), Number(s.down_payment)], [200, 60]);
    await db.query('update gym_settings set down_payment_percent=50');
    await assert.rejects(db.query('update gym_settings set down_payment_percent=0'), /check/);
  });
  await test('G13: a request holds nothing: someone else can ask for the same slot', async () => {
    const one = await member(), two = await member(), a = await amenity('D Free');
    await book(one, a, '10:00');
    const r2 = await book(two, a, '10:00');
    assert.equal(r2.status, 'awaiting_payment');
    const grid = (await as(two, 'select amenity_grid($1::date) g', [day]))[0].g;
    assert.deepEqual(grid.blocks.filter(b => b.amenity === Number(a)).map(b => b.mine), [true]);   // only your own request shows
  });
  await test('G13: of two paid requests for one slot the first verified wins; the other\'s down payment goes to Refunds', async () => {
    const one = await member(), two = await member(), a = await amenity('D Race');
    const r1 = await book(one, a, '10:00'), r2 = await book(two, a, '10:30');   // overlapping
    await approve(r2);
    assert.equal((await res(r2.reservation_id)).status, 'confirmed');
    await approve(r1);                                                             // paid, but the slot went to the other
    const lost = await res(r1.reservation_id);
    assert.deepEqual([lost.status, lost.cancel_reason], ['cancelled', 'Slot taken']);
    assert.equal((await payOf(r1)).status, 'verified');                           // the money was received
    const refund = await refundOf(r1.reservation_id);
    assert.deepEqual([refund.status, Number(refund.amount), refund.kind], ['approved', 100, 'cancel']);
    const told = (await db.query(`select title from user_notifications where user_id=$1 order by created_at desc limit 1`, [one])).rows[0].title;
    assert.match(told, /slot was taken/i);
  });
  await test('G13: the balance is paid at the desk once, by cash into the shift or GCash with a reference', async () => {
    const uid = await member(), a = await amenity('D Balance');
    const r = await book(uid, a, '10:00'); await approve(r);
    await assert.rejects(as(uid, `select * from record_booking_balance($1,'cash'::payment_method)`, [r.reservation_id]), /forbidden|permission/);
    await assert.rejects(as(desk, `select * from record_booking_balance($1,'gcash'::payment_method)`, [r.reservation_id]), /bad_input/);
    const p = (await as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [r.reservation_id]))[0];
    assert.deepEqual([p.status, Number(p.amount), p.method, p.kind], ['verified', 100, 'cash', 'amenity']);
    assert.ok(p.cash_shift_id && p.receipt_no);
    assert.equal((await res(r.reservation_id)).balance_payment_id, p.payment_id);
    await assert.rejects(as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [r.reservation_id]), /already_processed/);
    const unpaid = await book(uid, a, '15:00');
    await assert.rejects(as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [unpaid.reservation_id]), /not_confirmed/);
  });
  await test('G13: cancelling 24 hours or more before refunds the down payment; later, the gym keeps it', async () => {
    const uid = await member(), a = await amenity('D Cancel');
    const early = await book(uid, a, '10:00'); await approve(early);
    await as(uid, 'select cancel_reservation($1,null)', [early.reservation_id]);
    assert.deepEqual([(await refundOf(early.reservation_id)).status, Number((await refundOf(early.reservation_id)).amount)], ['approved', 100]);
    const late = await book(uid, a, '14:00'); await approve(late);
    await db.query(`update reservations set starts_at=now()+interval '5 hours', ends_at=now()+interval '6 hours' where reservation_id=$1`, [late.reservation_id]);
    await as(uid, 'select cancel_reservation($1,null)', [late.reservation_id]);   // allowed, but no refund
    assert.equal((await res(late.reservation_id)).status, 'cancelled');
    assert.equal(await refundOf(late.reservation_id), undefined);
  });
  await test('G13: the gym cancelling refunds the down payment whenever it is; nobody cancels once the balance is paid', async () => {
    const uid = await member(), a = await amenity('D Staff');
    const r = await book(uid, a, '10:00'); await approve(r);
    await db.query(`update reservations set starts_at=now()+interval '5 hours', ends_at=now()+interval '6 hours' where reservation_id=$1`, [r.reservation_id]);
    await as(desk, 'select cancel_reservation($1,$2)', [r.reservation_id, 'Pool closed for repairs']);
    assert.equal((await refundOf(r.reservation_id)).status, 'approved');
    const paid = await book(uid, a, '16:00'); await approve(paid);
    await as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [paid.reservation_id]);
    await assert.rejects(as(uid, 'select cancel_reservation($1,null)', [paid.reservation_id]), /balance_paid/);
  });
  await test('G14: free coaches run the amenity, have a shift over the whole time and are not booked then', async () => {
    const uid = await member(), a = await amenity('D Coach', 150);
    await shiftFor(coach, a, '08:00', '12:00');
    await shiftFor(coach2, a, '11:00', '20:00');
    assert.deepEqual(await free(uid, a, '09:00'), [coach]);
    assert.deepEqual(await free(uid, a, '07:30', 60), []);              // the shift must cover the whole time: coach starts at 08:00
    assert.deepEqual(await free(uid, a, '11:30', 60), [coach2]);        // coach's shift ends at 12:00, before the session does
    assert.deepEqual((await free(uid, a, '11:00', 60)).sort(), [coach, coach2].sort());
    const other = await amenity('D Other');
    assert.deepEqual(await free(uid, other, '09:00'), []);              // runs nothing there
  });
  await test('G14: with a coach costs the price plus the amenity\'s coach fee; a coach who isn\'t free is refused', async () => {
    const one = await member(), two = await member(), a = await amenity('D Coached', 150);
    await shiftFor(coach, a, '06:00', '22:00');
    const r = await book(one, a, '10:00', { coach });
    assert.deepEqual([Number(r.total_price), Number(r.down_payment), r.coach_id], [350, 175, coach]);
    await approve(r);
    assert.deepEqual(await free(two, a, '10:00'), []);                    // booked now
    await assert.rejects(book(two, a, '10:00', { coach }), /coach_not_free/);
    await assert.rejects(book(two, a, '20:00', { coach: coach2 }), /coach_not_free/);   // no shift, runs nothing
  });
  await test('G14: two paid requests for one coach: the first verified wins, the other is refunded', async () => {
    const one = await member(), two = await member();
    const a = await amenity('D Coach Race A', 100), b = await amenity('D Coach Race B', 100);
    await shiftFor(coach2, a, '06:00', '22:00'); await shiftFor(coach2, b, '06:00', '22:00');
    const r1 = await book(one, a, '18:00', { coach: coach2 }), r2 = await book(two, b, '18:00', { coach: coach2 });
    await approve(r1); await approve(r2);
    assert.deepEqual([(await res(r2.reservation_id)).status, (await res(r2.reservation_id)).cancel_reason], ['cancelled', 'Coach not free']);
    assert.equal((await refundOf(r2.reservation_id)).status, 'approved');
  });
  await test('G4: a code that fits the amenity takes its percent off the price before the down payment', async () => {
    const uid = await member(), a = await amenity('D Code');
    await as(admin, `select create_discount('DEP20',20,now()+interval '30 days',100,true,$1::jsonb)`, [JSON.stringify([{ kind: 'amenity', id: Number(a) }])]);
    const r = await book(uid, a, '10:00', { code: 'dep20' });
    assert.deepEqual([Number(r.total_price), Number(r.down_payment)], [160, 80]);
    const p = await payOf(r);
    assert.deepEqual([Number(p.amount), Number(p.discount_amount)], [80, 40]);
    const other = await amenity('D Not Coded');
    await assert.rejects(book(await member(), other, '10:00', { code: 'DEP20' }), /code_not_for_item/);
  });
  await test('a pass holder still books solo straight away; with a coach, the coach fee is the balance at the desk', async () => {
    const uid = await member(), a = (await db.query(`insert into amenities(name,price,duration_days,coach_fee) values('D Pass',300,30,120) returning amenity_id id`)).rows[0].id;
    const buy = (await as(uid, 'select * from avail_amenity($1)', [a]))[0];
    await as(desk, 'select verify_payment($1,true)', [buy.payment_id]);
    const solo = await book(uid, a, '09:00');
    assert.deepEqual([solo.status, Number(solo.total_price ?? 0)], ['confirmed', 0]);
    const own = await user('coach');
    await shiftFor(own, a, '06:00', '22:00');
    const coached = await book(uid, a, '15:00', { coach: own });
    assert.deepEqual([coached.status, Number(coached.total_price), Number(coached.down_payment), coached.coach_id], ['confirmed', 120, 0, own]);
    const p = (await as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [coached.reservation_id]))[0];
    assert.equal(Number(p.amount), 120);
  });
  await test('the new functions are closed to anon; free_coaches and record_booking_balance need the right people', async () => {
    for (const fn of ['free_coaches(bigint,timestamptz,timestamptz)', 'record_booking_balance(bigint,payment_method,text)',
                      'book_amenity(bigint,timestamptz,integer,payment_method,uuid,text)']) {
      const g = (await db.query(`select has_function_privilege('anon','public.${fn}','execute') a`)).rows[0];
      assert.equal(g.a, false, fn);
    }
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Amenity down payment: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
