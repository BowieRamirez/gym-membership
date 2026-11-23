// Phase 12 (E9): Session QR check-in, Coach is here, session status apart from payment, coach cancel with reason.
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
async function user(role = 'member', name = 'Test') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,$2,$3) returning id`, [`sc-${++serial}@test.io`, JSON.stringify({ full_name: name }), JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const desk = await user('employee', 'Desk Dana');
const coach = await user('coach', 'Coach Kai');
const member = await user('member', 'Mia Santos');
const pack = (await db.query(`insert into membership_packages(name,price,duration_days,includes_coach) values('SC Pack',1000,30,true) returning membership_package_id`)).rows[0].membership_package_id;
const plainPack = (await db.query(`insert into membership_packages(name,price,duration_days,includes_coach,coach_addon_price) values('SC Plain',500,30,false,400) returning membership_package_id`)).rows[0].membership_package_id;

const pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);

try {
  await test('Review Focus 5: cancelled or unpaid add-on session QR refused with reason', async () => {
    const m2 = await user('member', 'Leo Cruz');
    const p2 = (await as(m2, 'select * from extend_membership($1,null)', [plainPack]))[0];
    await as(desk, 'select verify_payment($1,true)', [p2.payment_id]);

    // Insert held (unpaid) session for today
    const held = (await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status, held_until)
       values($1,$2, now() + interval '1 hour', now() + interval '2 hours', 'scheduled', now() + interval '15 minutes')
       returning training_session_id, session_code`,
      [coach, m2]
    )).rows[0];
    await assert.rejects(as(desk, 'select * from scan_session($1)', [held.session_code]), /payment_required/);

    // Cancelled session
    const canc = (await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
       values($1,$2, now() + interval '3 hours', now() + interval '4 hours', 'cancelled')
       returning training_session_id, session_code`,
      [coach, m2]
    )).rows[0];
    await assert.rejects(as(desk, 'select * from scan_session($1)', [canc.session_code]), /session_cancelled/);

    // Wrong day (tomorrow)
    const tom = (await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status)
       values($1,$2, now() + interval '1 day', now() + interval '1 day 1 hour', 'scheduled')
       returning training_session_id, session_code`,
      [coach, m2]
    )).rows[0];
    await assert.rejects(as(desk, 'select * from scan_session($1)', [tom.session_code]), /wrong_day/);
  });

  await test('scheduled session today checks in; not In session until Coach is here; payment status untouched', async () => {
    const m3 = await user('member', 'Nora Reyes');
    const p3 = (await as(m3, 'select * from extend_membership($1,null)', [plainPack]))[0];
    await as(desk, 'select verify_payment($1,true)', [p3.payment_id]);

    // Verified add-on payment + session today
    const addonPayId = (await db.query(
      `insert into payments(member_id, kind, method, amount, status, payment_code, recorded_by, verified_by, verified_at, receipt_no)
       values($1,'coach_addon','cash',400,'verified','GYM-ADDON1',$2,$2,now(),'OR-SC1') returning payment_id`,
      [m3, desk]
    )).rows[0].payment_id;
    const ses = (await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status, payment_id)
       values($1,$2, ((now() at time zone 'Asia/Manila')::date + time '08:00') at time zone 'Asia/Manila', ((now() at time zone 'Asia/Manila')::date + time '09:00') at time zone 'Asia/Manila', 'scheduled', $3)
       returning training_session_id, session_code`,
      [coach, m3, addonPayId]
    )).rows[0];

    // Cannot mark coach arrived before member check-in
    await assert.rejects(as(desk, 'select mark_coach_arrived($1)', [ses.training_session_id]), /not_checked_in/);

    // Member checks in -> status is Checked in, not In session
    const scanned = (await as(desk, 'select * from scan_session($1)', [ses.session_code]))[0];
    assert.equal(scanned.status, 'Checked in');
    assert.ok(scanned.checked_in_at);
    assert.equal(scanned.coach_arrived_at, null);

    // Coach is here -> status becomes In session
    await as(desk, 'select mark_coach_arrived($1)', [ses.training_session_id]);
    const today = (await as(desk, 'select * from today_coach_sessions()')).find(x => x.training_session_id === ses.training_session_id);
    assert.equal(today.status, 'In session');
    assert.ok(today.coach_arrived_at);
    await assert.rejects(as(m3, 'select * from today_coach_sessions()'), /forbidden/);

    // Payment status untouched
    const payRow = (await db.query('select status from payments where payment_id = $1', [addonPayId])).rows[0];
    assert.equal(payRow.status, 'verified');
  });

  await test('coach cancel requires reason, alerts member, and refunds paid add-on', async () => {
    const m4 = await user('member', 'Owen Tan');
    const addonPayId = (await db.query(
      `insert into payments(member_id, kind, method, amount, status, payment_code, recorded_by, verified_by, verified_at, receipt_no)
       values($1,'coach_addon','cash',400,'verified','GYM-ADDON2',$2,$2,now(),'OR-SC2') returning payment_id`,
      [m4, desk]
    )).rows[0].payment_id;
    const ses = (await db.query(
      `insert into training_sessions(coach_id, member_id, scheduled_start, scheduled_end, status, payment_id)
       values($1,$2, now() + interval '2 hours', now() + interval '3 hours', 'scheduled', $3)
       returning training_session_id`,
      [coach, m4, addonPayId]
    )).rows[0];

    await assert.rejects(as(coach, 'select cancel_session($1, $2)', [ses.training_session_id, '   ']), /reason_required/);
    await as(coach, 'select cancel_session($1, $2)', [ses.training_session_id, 'Coach unwell']);

    const notif = (await db.query(`select title, body from user_notifications where user_id=$1 order by notification_id desc limit 1`, [m4])).rows[0];
    assert.equal(notif.title, 'Your coach session was cancelled');
    assert.equal(notif.body, 'Coach unwell');

    const ref = (await db.query(`select status, amount::numeric from refund_requests where training_session_id=$1`, [ses.training_session_id])).rows[0];
    assert.equal(ref.status, 'approved');
    assert.equal(Number(ref.amount), 400);
  });

  await test('desk can book walk-in coach session without member account; member cannot call it', async () => {
    const m5 = await user('member', 'Ria Cruz');
    const tomorrow = (await db.query(`select ((now() at time zone 'Asia/Manila')::date + 2)::text d`)).rows[0].d;
    const wStart = `${tomorrow}T10:00:00+08:00`, wEnd = `${tomorrow}T18:00:00+08:00`;
    await db.query(`insert into shift_posts(coach_id, starts_at, ends_at) values($1,$2,$3)`, [coach, wStart, wEnd]);
    await assert.rejects(as(m5, `select desk_book_walkin_coach_session($1,$2,1,'test23')`, [coach, `${tomorrow}T11:00:00+08:00`]), /forbidden/);
    const wid = (await as(desk, `select desk_book_walkin_coach_session($1,$2,1,'test23') id`, [coach, `${tomorrow}T11:00:00+08:00`]))[0].id;
    const row = (await db.query(`select member_id, walk_in_name from training_sessions where training_session_id=$1`, [wid])).rows[0];
    assert.equal(row.member_id, null);
    assert.equal(row.walk_in_name, 'test23');

    // B2: unrelated member and other coach cannot cancel walk-in session; owning coach must give a reason
    const otherCoach = await user('coach', 'Coach Lee');
    await assert.rejects(as(m5, 'select cancel_session($1, null)', [wid]), /not_found/);
    await assert.rejects(as(otherCoach, 'select cancel_session($1, $2)', [wid, 'Not mine']), /not_found/);
    await assert.rejects(as(coach, 'select cancel_session($1, $2)', [wid, '   ']), /reason_required/);
    await as(coach, 'select cancel_session($1, $2)', [wid, 'Coach unavailable']);
    const cancelled = (await db.query(`select status, cancel_reason from training_sessions where training_session_id=$1`, [wid])).rows[0];
    assert.equal(cancelled.status, 'cancelled');
    assert.equal(cancelled.cancel_reason, 'Coach unavailable');
  });
} finally {
  await db.end(); await server.stop();
  console.log(`\nSessionCheckin: ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
}
