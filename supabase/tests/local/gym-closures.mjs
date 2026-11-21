// Phase 7 of the 2026-11-12 plan (0065, owner decisions G16-G18): Admin and System admin close the gym for a whole day (Manila) with
// a reason. Nothing can be set up on a closed day (amenity bookings, session requests and proposals, coach shifts, membership
// check-in: `day_closed`). Closing cancels what is already set up that day and not yet over (paid bookings and down payments go to
// Refunds), posts an announcement to everyone and a personal note to each person affected. Reopening announces it and restores nothing.
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
  [`gc-${++serial}@test.io`, JSON.stringify({ full_name: 'Closure Person' }), JSON.stringify({ role })])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}
const admin = await user('admin'), sub = await user('subadmin'), desk = await user('employee');
await openCashShifts(db);
await db.query(`update gym_settings set opens_at='00:00',closes_at='23:30',payment_expiry_hours=24 where id=1`);
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('GC Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
const amenity = (await db.query(`insert into amenities(name,price) values('GC Court',200) returning amenity_id id`)).rows[0].id;
async function member() {
  const uid = await user();
  const p = (await as(uid, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [p.payment_id]);
  return uid;
}
const dayAt = async n => (await db.query(`select ((now() at time zone 'Asia/Manila')::date + $1::int)::text d`, [n])).rows[0].d;
const today = await dayAt(0), day = await dayAt(3), next = await dayAt(4), later = await dayAt(5);
const at = (hm, d = day) => new Date(`${d}T${hm}:00+08:00`).toISOString();
const book = (uid, hm, d = day, coach = null) => as(uid, `select * from book_amenity(p_amenity=>$1, p_start=>$2, p_minutes=>60, p_method=>'qr'::payment_method, p_coach=>$3)`,
  [amenity, at(hm, d), coach]).then(r => r[0]);
const approve = r => as(desk, 'select verify_payment($1,true)', [r.payment_id]);
const close = (who, d, reason = 'Holiday') => as(who, 'select close_gym_day($1::date,$2)', [d, reason]);
const notes = async uid => (await db.query('select title, body from user_notifications where user_id=$1 order by notification_id', [uid])).rows;
const coachWithShift = async (d, from = '06:00', to = '22:00') => {
  const c = await user('coach');
  await as(c, 'insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)', [c, at(from, d), at(to, d)]);
  return c;
};

try {
  await test('only Admin and System admin close or reopen a day; anon cannot call the functions', async () => {
    const m = await member(), c = await user('coach');
    await assert.rejects(as(m, `select close_gym_day($1::date,'Mine now')`, [later]), /forbidden/);   // D16: tried directly
    for (const who of [m, c, desk]) {
      await assert.rejects(close(who, later), /forbidden|permission/);
      await assert.rejects(as(who, 'select reopen_gym_day($1::date)', [later]), /forbidden|permission/);
      await assert.rejects(as(who, 'select closure_preview($1::date) p', [later]), /forbidden|permission/);
    }
    for (const fn of ['close_gym_day(date,text)', 'reopen_gym_day(date)', 'closure_preview(date)', 'day_is_closed(timestamptz)']) {
      assert.equal((await db.query(`select has_function_privilege('anon','public.${fn}','execute') a`)).rows[0].a, false, fn);
    }
    assert.equal((await db.query(`select has_table_privilege('authenticated','public.gym_closures','insert, update, delete') a`)).rows[0].a, false);
  });

  await test('a past date, a missing reason or a day already closed is refused; everyone active can read the list', async () => {
    await assert.rejects(close(admin, await dayAt(-1)), /day_past/);
    await assert.rejects(close(admin, later, '  '), /bad_reason/);
    await close(sub, later, 'Repairs');
    await assert.rejects(close(admin, later), /already_processed/);
    const m = await member();
    const rows = await as(m, 'select closed_on::text d, reason from gym_closures where closed_on=$1::date', [later]);
    assert.deepEqual(rows, [{ d: later, reason: 'Repairs' }]);
    await as(admin, 'select reopen_gym_day($1::date)', [later]);
  });

  await test('nothing can be set up on a closed day: bookings and shifts', async () => {
    const m = await member(), c = await user('coach');
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [c]);
    await as(c, 'insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)', [c, at('06:00', next), at('22:00', next)]);
    await close(admin, day);
    await assert.rejects(book(m, '10:00'), /day_closed/);
    await assert.rejects(as(c, 'insert into shift_posts(coach_id,starts_at,ends_at) values($1,$2,$3)', [c, at('06:00'), at('12:00')]), /day_closed/);
    assert.ok(await book(m, '10:00', next), 'the next day is open');
    await as(admin, 'select reopen_gym_day($1::date)', [day]);
  });

  await test('membership check-in is refused on a closed day', async () => {
    const m = await member();
    await close(admin, today, 'Typhoon');
    const checkIn = () => as(admin, `select record_check_in(p_member_id=>$1, p_method=>'manual', p_reason=>'No phone today')`, [m]);
    await assert.rejects(checkIn(), /day_closed/);
    await as(admin, 'select reopen_gym_day($1::date)', [today]);
    await checkIn();
  });

  await test('the preview counts what closing cancels; closing cancels it, refunds what was paid and tells everyone', async () => {
    const paidM = await member(), waitM = await member(), fullM = await member(), sessM = await member();
    const c = await coachWithShift(day);
    const paid = await book(paidM, '09:00'); await approve(paid);
    const waiting = await book(waitM, '11:00');
    const full = await book(fullM, '13:00'); await approve(full);
    const bal = (await as(desk, `select * from record_booking_balance($1,'cash'::payment_method)`, [full.reservation_id]))[0];
    const sess = (await db.query(`insert into training_sessions(coach_id,member_id,scheduled_start,scheduled_end) values($1,$2,$3,$4) returning training_session_id id`,
      [c, sessM, at('15:00'), at('16:00')])).rows[0].id;
    const preview = (await as(admin, 'select closure_preview($1::date) p', [day]))[0].p;
    assert.deepEqual(preview, { bookings: 3, paid: 2, sessions: 1, shifts: 1, requests: 0 });

    await close(admin, day, 'Holiday');
    const st = async id => (await db.query('select status, cancel_reason from reservations where reservation_id=$1', [id])).rows[0];
    for (const r of [paid, waiting, full]) assert.deepEqual(await st(r.reservation_id), { status: 'cancelled', cancel_reason: 'Gym closed: Holiday' });
    const refund = async id => (await db.query('select status, amount::numeric::text a from refund_requests where reservation_id=$1', [id])).rows[0];
    assert.deepEqual(await refund(paid.reservation_id), { status: 'approved', a: String(Number(paid.down_payment ?? 150).toFixed(2)) });
    assert.equal((await refund(full.reservation_id)).a, (Number(full.down_payment ?? 150) + Number(bal.amount)).toFixed(2), 'down payment and balance');
    assert.equal(await refund(waiting.reservation_id), undefined, 'nothing was paid');
    assert.equal((await db.query('select status from payments where payment_id=$1', [waiting.payment_id])).rows[0].status, 'rejected');
    assert.equal((await db.query('select status from training_sessions where training_session_id=$1', [sess])).rows[0].status, 'cancelled');
    assert.equal((await db.query(`select count(*)::int n from shift_posts where coach_id=$1 and status='scheduled'`, [c])).rows[0].n, 0);

    const body = (await db.query(`select body from announcements order by announcement_id desc limit 1`)).rows[0].body;
    assert.match(body, /^The gym is closed on \w+, \d{1,2} \w{3}: Holiday\.$/);
    for (const uid of [paidM, waitM, fullM, sessM, c]) {
      const n = await notes(uid);
      assert.ok(n.some(x => x.title === 'Announcement'), 'announcement');
      assert.ok(n.some(x => x.title !== 'Announcement' && /Gym closed: Holiday/.test(x.body ?? '')), `personal note for ${uid}`);
    }
  });

  await test('reopening announces it, lets people book again and brings nothing back', async () => {
    const m = await member();
    await as(admin, 'select reopen_gym_day($1::date)', [day]);
    assert.match((await db.query(`select body from announcements order by announcement_id desc limit 1`)).rows[0].body, /^The gym is open again on \w+, \d{1,2} \w{3}\.$/);
    assert.ok(await book(m, '17:00'));
    assert.equal((await db.query(`select count(*)::int n from reservations where cancel_reason='Gym closed: Holiday' and status='cancelled'`)).rows[0].n, 3);
    await assert.rejects(as(admin, 'select reopen_gym_day($1::date)', [day]), /not_found/);
    await assert.rejects(as(admin, 'select reopen_gym_day($1::date)', [await dayAt(-1)]), /day_past/);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Gym closures: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
